using System.ComponentModel;
using System.Windows;
using Clicky.Core;

namespace Clicky.UI;

/// <summary>
/// The floating companion panel shown when the user clicks the tray icon. Shows
/// status, push-to-talk instructions, the cursor toggle, mic settings, and quit —
/// the Windows port of CompanionPanelView. It auto-dismisses when it loses focus
/// (the click-outside behavior from the macOS global monitor).
/// </summary>
public partial class PanelWindow : Window
{
    private readonly CompanionManager _companionManager;

    public PanelWindow(CompanionManager companionManager)
    {
        InitializeComponent();
        _companionManager = companionManager;

        ShowCursorCheckBox.IsChecked = _companionManager.IsCursorEnabled;
        StatusLine.Text = _companionManager.StatusText;

        _companionManager.PropertyChanged += OnCompanionPropertyChanged;
        Deactivated += (_, _) => Hide();
    }

    private void OnCompanionPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(CompanionManager.StatusText))
        {
            Dispatcher.Invoke(() => StatusLine.Text = _companionManager.StatusText);
        }
    }

    /// <summary>Positions the panel near the tray (bottom-right of the primary work area) and shows it.</summary>
    public void ShowNearTray()
    {
        var workArea = SystemParameters.WorkArea;

        // Measure first so we can anchor the bottom-right corner just above the tray.
        Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Left = workArea.Right - Width - 12;
        Top = workArea.Bottom - ActualHeight - 12;

        Show();
        Activate();

        // Re-anchor once the final height is known.
        Top = workArea.Bottom - ActualHeight - 12;
    }

    private void OnShowCursorChanged(object sender, RoutedEventArgs args) =>
        _companionManager.SetCursorEnabled(ShowCursorCheckBox.IsChecked == true);

    private void OnMicrophoneSettingsClicked(object sender, RoutedEventArgs args) =>
        _companionManager.OpenMicrophoneSettings();

    private void OnQuitClicked(object sender, RoutedEventArgs args) =>
        Application.Current.Shutdown();
}
