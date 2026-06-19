using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
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
        EngineInfoLine.Text = _companionManager.EngineSummary;
        RefreshVoiceState();

        _companionManager.PropertyChanged += OnCompanionPropertyChanged;
        Deactivated += (_, _) => Hide();
    }

    private void OnCompanionPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(CompanionManager.StatusText))
        {
            Dispatcher.Invoke(() => StatusLine.Text = _companionManager.StatusText);
        }
        else if (args.PropertyName == nameof(CompanionManager.VoiceState))
        {
            Dispatcher.Invoke(RefreshVoiceState);
        }
    }

    /// <summary>Mirrors the live voice state into the header's status dot + label.</summary>
    private void RefreshVoiceState()
    {
        var (hexColor, label) = _companionManager.VoiceState switch
        {
            CompanionVoiceState.Listening => ("#60A5FA", "Listening"),
            CompanionVoiceState.Processing => ("#60A5FA", "Thinking"),
            CompanionVoiceState.Responding => ("#3380FF", "Responding"),
            _ => ("#34D399", "Active"),
        };

        var stateColor = (Color)System.Windows.Media.ColorConverter.ConvertFromString(hexColor);
        StatusDot.Fill = new SolidColorBrush(stateColor);
        StatusDotGlow.Color = stateColor;
        StatusDotGlow.Opacity = 0.75;
        StateText.Text = label;
        StateText.Foreground = new SolidColorBrush(stateColor);
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

    private void OnReportIssueClicked(object sender, RoutedEventArgs args)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://github.com/jobelshaji95/clicky-hack/issues",
                UseShellExecute = true
            });
        }
        catch
        {
            // Opening the browser should never crash the panel.
        }
    }

    private void OnQuitClicked(object sender, RoutedEventArgs args) =>
        Application.Current.Shutdown();
}
