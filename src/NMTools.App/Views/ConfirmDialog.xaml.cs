using System.Windows;

namespace NMTools.App.Views;

public partial class ConfirmDialog : Window
{
    public enum CustomDialogResult
    {
        Yes,
        No,
        Cancel,
    }

    public CustomDialogResult Result { get; private set; } = CustomDialogResult.Cancel;

    public ConfirmDialog()
    {
        InitializeComponent();
    }

    public static CustomDialogResult Show(Window owner)
    {
        var dlg = new ConfirmDialog
        {
            Owner = owner is { IsVisible: true } ? owner : null,
            WindowStartupLocation = owner is { IsVisible: true }
                ? WindowStartupLocation.CenterOwner
                : WindowStartupLocation.CenterScreen,
        };
        dlg.ShowDialog();
        return dlg.Result;
    }

    public static void ShowAlert(
        Window? owner,
        string message,
        string title,
        bool isWarning = false)
    {
        var header = title.Contains(" - ", StringComparison.Ordinal)
            ? title[(title.IndexOf(" - ", StringComparison.Ordinal) + 3)..]
            : title;
        var dlg = new ConfirmDialog
        {
            Title = title,
            Width = 420,
            Owner = owner is { IsVisible: true } ? owner : null,
            WindowStartupLocation = owner is { IsVisible: true }
                ? WindowStartupLocation.CenterOwner
                : WindowStartupLocation.CenterScreen,
            Topmost = owner is not { IsVisible: true },
        };
        dlg.HeaderText.Text = header;
        dlg.MessageText.Text = message;
        dlg.ConfirmButtonsPanel.Visibility = Visibility.Collapsed;
        dlg.AlertButtonsPanel.Visibility = Visibility.Visible;
        if (isWarning)
        {
            dlg.IconBadge.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(254, 242, 242));
            dlg.IconText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 38, 38));
            dlg.IconText.Text = "!";
        }
        else
        {
            dlg.IconBadge.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 246, 255));
            dlg.IconText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 99, 235));
            dlg.IconText.Text = "i";
        }

        dlg.ShowDialog();
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        Result = CustomDialogResult.Yes;
        DialogResult = true;
    }

    private void OnYesClick(object sender, RoutedEventArgs e)
    {
        Result = CustomDialogResult.Yes;
        DialogResult = true;
    }

    private void OnNoClick(object sender, RoutedEventArgs e)
    {
        Result = CustomDialogResult.No;
        DialogResult = false;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        Result = CustomDialogResult.Cancel;
        DialogResult = false;
    }
}
