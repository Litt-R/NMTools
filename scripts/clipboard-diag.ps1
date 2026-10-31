# NMTools clipboard write failure diagnostic
#
# Pure ASCII on purpose: Windows PowerShell 5.1 reads .ps1 as the system ANSI
# code page unless the file has a BOM, so any non-ASCII character would turn
# into mojibake and break the parser.
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\clipboard-diag.ps1
#
# What it does:
#   STEP 1  passive sampling (read-only, no interference) while you reproduce the bug
#   STEP 2  read-only check: can this process open the clipboard at all
#   STEP 3  write test through the same OLE path WPF uses  <-- clipboard is REPLACED
#   STEP 4  write test through raw Win32 APIs              <-- clipboard is REPLACED
#   STEP 5  related clipboard / security / remote-desktop processes
#   STEP 6  NMTools process info
#
# STEP 3 and STEP 4 overwrite the current clipboard content with a short test
# string. Nothing is saved and nothing is restored. If your clipboard holds
# something important, copy it elsewhere before running this script.
#
# A transcript is written to clipboard-diag-log.txt next to this script.

Add-Type -Namespace NM -Name Clip -MemberDefinition @'
[DllImport("user32.dll", SetLastError=true)] public static extern IntPtr GetOpenClipboardWindow();
[DllImport("user32.dll", SetLastError=true)] public static extern bool OpenClipboard(IntPtr hWndNewOwner);
[DllImport("user32.dll")] public static extern bool CloseClipboard();
[DllImport("user32.dll", SetLastError=true)] public static extern bool EmptyClipboard();
[DllImport("user32.dll", SetLastError=true)] public static extern IntPtr SetClipboardData(uint format, IntPtr handle);
[DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder sb, int max);
[DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
[DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr GlobalLock(IntPtr memory);
[DllImport("kernel32.dll", SetLastError=true)] public static extern bool GlobalUnlock(IntPtr memory);
'@

$wpfAvailable = $false
try {
    Add-Type -AssemblyName PresentationFramework -ErrorAction Stop
    $wpfAvailable = $true
} catch {
    $wpfAvailable = $false
}

$scriptDirectory = $PSScriptRoot
if ([string]::IsNullOrEmpty($scriptDirectory)) {
    $scriptDirectory = (Get-Location).Path
}
$logPath = Join-Path -Path $scriptDirectory -ChildPath "clipboard-diag-log.txt"
try {
    Start-Transcript -Path $logPath -Force | Out-Null
    $transcriptStarted = $true
} catch {
    $transcriptStarted = $false
}

function Get-ClipboardOwnerInfo {
    param([IntPtr]$Hwnd)
    if ($Hwnd -eq [IntPtr]::Zero) { return $null }
    $procId = 0
    [void][NM.Clip]::GetWindowThreadProcessId($Hwnd, [ref]$procId)
    $sb = New-Object System.Text.StringBuilder 256
    [void][NM.Clip]::GetClassName($Hwnd, $sb, 256)
    $pname = try { (Get-Process -Id $procId -ErrorAction Stop).ProcessName } catch { "?" }
    [pscustomobject]@{ ProcessId = $procId; Process = $pname; Class = $sb.ToString() }
}

function Get-OwnerText {
    param([IntPtr]$Hwnd)
    $info = Get-ClipboardOwnerInfo -Hwnd $Hwnd
    if ($null -eq $info) { return "none (holder used a NULL owner or clipboard is free)" }
    return ("{0} pid={1} class={2}" -f $info.Process, $info.ProcessId, $info.Class)
}

Write-Host "NMTools clipboard diagnostic"
Write-Host ("OS: {0} (build {1})" -f [System.Environment]::OSVersion.VersionString, [System.Environment]::OSVersion.Version.Build)
Write-Host ("PowerShell: {0}, apartment: {1}" -f $PSVersionTable.PSVersion, [System.Threading.Thread]::CurrentThread.GetApartmentState())
Write-Host ("This process session: {0}" -f [System.Diagnostics.Process]::GetCurrentProcess().SessionId)
Write-Host ("WPF clipboard available: {0}" -f $wpfAvailable)
if ($transcriptStarted) {
    Write-Host ("Transcript file: {0}" -f $logPath)
}
Write-Host ""

# ---------------------------------------------------------------------------
Write-Host "=== STEP 1: passive sampling for 20 seconds ===" -ForegroundColor Cyan
Write-Host "READ-ONLY. Nothing is touched in this step."
Write-Host ""
Write-Host ">>> REPRODUCE THE BUG DURING THESE 20 SECONDS <<<"
Write-Host ">>> press the NMTools hotkey, type a few characters, press Enter,"
Write-Host ">>> and wait until the 'input bridge failed' dialog appears."
Write-Host ""
Write-Host "Sampling starts in 5 seconds..."
Start-Sleep -Seconds 5
Write-Host "Sampling now."

$busyCounts = @{}
$sampleCount = 0
$openFailures = 0
$sequence = [NM.Clip]::GetClipboardSequenceNumber()
$sequenceStart = $sequence
$sequenceChanges = 0
$deadline = (Get-Date).AddSeconds(20)

while ((Get-Date) -lt $deadline) {
    $sampleCount++

    $window = [NM.Clip]::GetOpenClipboardWindow()
    if ($window -ne [IntPtr]::Zero) {
        $owner = Get-ClipboardOwnerInfo -Hwnd $window
        if ($owner) {
            $key = "{0} (pid {1}, class {2})" -f $owner.Process, $owner.ProcessId, $owner.Class
            if (-not $busyCounts.ContainsKey($key)) { $busyCounts[$key] = 0 }
            $busyCounts[$key]++
        }
    }

    $current = [NM.Clip]::GetClipboardSequenceNumber()
    if ($current -ne $sequence) {
        $sequenceChanges++
        $sequence = $current
    }

    Start-Sleep -Milliseconds 50
}

Write-Host ("Samples taken: {0}" -f $sampleCount)
Write-Host ("Clipboard sequence number: start {0}, end {1}, changes {2}" -f $sequenceStart, $sequence, $sequenceChanges)
if ($sequenceChanges -eq 0) {
    Write-Host "  The clipboard content never changed during sampling." -ForegroundColor Yellow
    Write-Host "  If NMTools reported a write failure in this window, then not one of its writes"
    Write-Host "  landed, so the failure is not a transient mid-write race."
}
if ($busyCounts.Count -eq 0) {
    Write-Host "  No window-owning process held the clipboard during sampling." -ForegroundColor Green
    Write-Host "  NOTE: this is NOT proof of no contention. A process that calls OpenClipboard(NULL)"
    Write-Host "  is invisible to GetOpenClipboardWindow(). STEP 3/4 measure the real thing."
} else {
    foreach ($key in $busyCounts.Keys) {
        Write-Host ("  held {0} samples  <- {1}" -f $busyCounts[$key], $key) -ForegroundColor Yellow
    }
}

# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "=== STEP 2: read-only open probe (5 attempts) ===" -ForegroundColor Cyan
for ($i = 1; $i -le 5; $i++) {
    if ([NM.Clip]::OpenClipboard([IntPtr]::Zero)) {
        [void][NM.Clip]::CloseClipboard()
        Write-Host ("  attempt {0}: OPEN OK" -f $i) -ForegroundColor Green
    } else {
        $err = [System.Runtime.InteropServices.Marshal]::GetLastWin32Error()
        $ownerText = Get-OwnerText -Hwnd ([NM.Clip]::GetOpenClipboardWindow())
        Write-Host ("  attempt {0}: OPEN FAIL win32={1} owner={2}" -f $i, $err, $ownerText) -ForegroundColor Red
    }
    Start-Sleep -Milliseconds 100
}

# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "=== STEP 3: write test through the WPF/OLE path (clipboard WILL be replaced) ===" -ForegroundColor Cyan
$testText = "NMTools clipboard diagnostic test " + (Get-Date -Format "yyyyMMdd-HHmmss")
if (-not $wpfAvailable) {
    Write-Host "  SKIPPED: WPF assembly could not be loaded." -ForegroundColor Yellow
} else {
    for ($i = 1; $i -le 5; $i++) {
        try {
            [System.Windows.Clipboard]::SetText($testText, [System.Windows.TextDataFormat]::UnicodeText)
            Write-Host ("  attempt {0}: WPF WRITE OK" -f $i) -ForegroundColor Green
        } catch {
            $ex = $_.Exception
            $hr = try { ("0x{0:X8}" -f $ex.HResult) } catch { "n/a" }
            $inner = if ($ex.InnerException) { " inner=" + $ex.InnerException.GetType().Name } else { "" }
            $ownerText = Get-OwnerText -Hwnd ([NM.Clip]::GetOpenClipboardWindow())
            Write-Host ("  attempt {0}: WPF WRITE FAIL type={1} hresult={2}{3}" -f $i, $ex.GetType().Name, $hr, $inner) -ForegroundColor Red
            Write-Host ("     message: {0}" -f $ex.Message)
            Write-Host ("     owner now: {0}" -f $ownerText)
        }
        Start-Sleep -Milliseconds 100
    }
    Write-Host "  HRESULT cheat sheet:"
    Write-Host "    0x800401D0 CLIPBRD_E_CANT_OPEN   -> someone else held the clipboard"
    Write-Host "    0x80070005 E_ACCESSDENIED        -> blocked by policy / integrity level"
    Write-Host "    0x800401D1 CLIPBRD_E_CANT_EMPTY  -> EmptyClipboard was refused"
    Write-Host "    0x800401D2 CLIPBRD_E_CANT_SET    -> SetClipboardData was refused"
}

# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "=== STEP 4: write test through raw Win32 APIs (clipboard WILL be replaced) ===" -ForegroundColor Cyan
for ($i = 1; $i -le 5; $i++) {
    if (-not [NM.Clip]::OpenClipboard([IntPtr]::Zero)) {
        $err = [System.Runtime.InteropServices.Marshal]::GetLastWin32Error()
        Write-Host ("  attempt {0}: OPEN FAIL win32={1} owner={2}" -f $i, $err, (Get-OwnerText -Hwnd ([NM.Clip]::GetOpenClipboardWindow()))) -ForegroundColor Red
        Start-Sleep -Milliseconds 100
        continue
    }

    try {
        $emptyOk = [NM.Clip]::EmptyClipboard()
        $emptyErr = [System.Runtime.InteropServices.Marshal]::GetLastWin32Error()
        $bytes = [System.Text.Encoding]::Unicode.GetBytes($testText + [char]0)
        $memory = [NM.Clip]::GlobalAlloc(0x0002, [UIntPtr]([uint64]$bytes.Length))
        $pointer = [NM.Clip]::GlobalLock($memory)
        [System.Runtime.InteropServices.Marshal]::Copy($bytes, 0, $pointer, $bytes.Length)
        [void][NM.Clip]::GlobalUnlock($memory)
        $setResult = [NM.Clip]::SetClipboardData(13, $memory)
        $setErr = [System.Runtime.InteropServices.Marshal]::GetLastWin32Error()

        if ($emptyOk -and $setResult.ToInt64() -ne 0) {
            Write-Host ("  attempt {0}: WIN32 WRITE OK" -f $i) -ForegroundColor Green
        } else {
            Write-Host ("  attempt {0}: WIN32 WRITE FAIL empty={1}(err {2}) setData={3}(err {4})" -f $i, $emptyOk, $emptyErr, $setResult, $setErr) -ForegroundColor Red
            Write-Host ("     owner now: {0}" -f (Get-OwnerText -Hwnd ([NM.Clip]::GetOpenClipboardWindow())))
        }
    } finally {
        [void][NM.Clip]::CloseClipboard()
    }
    Start-Sleep -Milliseconds 100
}

# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "=== STEP 5: related clipboard / security / remote-desktop processes ===" -ForegroundColor Cyan
Get-Process -ErrorAction SilentlyContinue |
    Where-Object { $_.ProcessName -match 'rdpclip|nxplayer|nxclient|NMTools|VDP|ScrCap|TrustView|TVPI|dlp|clip|Sophos|Pulse|Avecto|PGSrv' } |
    Select-Object Id, ProcessName, SessionId |
    Sort-Object ProcessName |
    Format-Table -AutoSize |
    Out-String -Width 120 |
    Write-Host

# ---------------------------------------------------------------------------
Write-Host "=== STEP 6: NMTools process info ===" -ForegroundColor Cyan
$nm = Get-Process -Name NMTools.App -ErrorAction SilentlyContinue
if (-not $nm) {
    Write-Host "  NMTools.App is not running." -ForegroundColor Yellow
} else {
    foreach ($p in $nm) {
        Write-Host ("  pid={0} session={1} started={2}" -f $p.Id, $p.SessionId, $p.StartTime)
        Write-Host ("  path={0}" -f $p.Path)
        $modules = try { ($p.Modules | Where-Object { $_.ModuleName -match 'hook|clb|dlp|TV|PG' } | ForEach-Object { $_.ModuleName }) -join ', ' } catch { "unavailable" }
        Write-Host ("  injected hook-ish modules: {0}" -f $modules)
    }
}
Write-Host ""
Write-Host "  Is NMTools running as Administrator? If a UAC prompt appeared at launch, or if"
Write-Host "  Task Manager shows an elevated flag, that alone would explain access denied"
Write-Host "  (a higher integrity level cannot use a clipboard owned by a lower one)."

Write-Host ""
Write-Host "Done. Please copy the whole output above and send it back." -ForegroundColor Cyan
if ($transcriptStarted) {
    Write-Host ("Full log saved to: {0}" -f $logPath) -ForegroundColor Cyan
    try {
        Stop-Transcript | Out-Null
    } catch {
        # Not fatal: the log content is already on disk.
    }
}
