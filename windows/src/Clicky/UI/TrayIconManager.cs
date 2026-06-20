using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Hardcodet.Wpf.TaskbarNotification;
using Clicky.Core;

namespace Clicky.UI;

/// <summary>
/// Creates the system-tray icon and toggles the companion panel on click. This is
/// the Windows replacement for the macOS NSStatusItem + MenuBarPanelManager. The app
/// has no main window — it lives entirely in the tray.
/// </summary>
public sealed class TrayIconManager : IDisposable
{
    private readonly CompanionManager _companionManager;
    private readonly TaskbarIcon _taskbarIcon;
    private PanelWindow? _panelWindow;
    private string? _updateUrl;

    public TrayIconManager(CompanionManager companionManager)
    {
        _companionManager = companionManager;

        _taskbarIcon = new TaskbarIcon
        {
            ToolTipText = "Clicky — hold Ctrl+Alt to talk",
            Icon = LoadTrayIcon()
        };

        // Left-click toggles the panel; right-click shows a minimal menu.
        _taskbarIcon.TrayLeftMouseUp += (_, _) => TogglePanel();
        _taskbarIcon.ContextMenu = BuildContextMenu();

        // Clicking the update balloon opens the releases page.
        _taskbarIcon.TrayBalloonTipClicked += (_, _) => OpenUpdatePage();
    }

    /// <summary>Shows a tray balloon when a newer version is available (informational only).</summary>
    public void NotifyUpdateAvailable(string version, string releaseUrl)
    {
        _updateUrl = releaseUrl;
        _taskbarIcon.ShowBalloonTip(
            "Clicky update available",
            $"Version {version} is out — click to view the release.",
            BalloonIcon.Info);
    }

    private void OpenUpdatePage()
    {
        if (string.IsNullOrEmpty(_updateUrl))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = _updateUrl, UseShellExecute = true });
        }
        catch
        {
            // Best-effort.
        }
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "clicky.ico");
        if (File.Exists(iconPath))
        {
            return new System.Drawing.Icon(iconPath);
        }

        // Fall back to the system application icon so the tray is never blank.
        return System.Drawing.SystemIcons.Application;
    }

    private ContextMenu BuildContextMenu()
    {
        var openItem = new MenuItem { Header = "Open Clicky" };
        openItem.Click += (_, _) => TogglePanel();

        var openLogsItem = new MenuItem { Header = "Open Logs & History…" };
        openLogsItem.Click += (_, _) => OpenDiagnosticsFolder();

        var quitItem = new MenuItem { Header = "Quit Clicky" };
        quitItem.Click += (_, _) => Application.Current.Shutdown();

        var menu = new ContextMenu();
        menu.Items.Add(openItem);
        menu.Items.Add(openLogsItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(quitItem);
        return menu;
    }

    private void TogglePanel()
    {
        _panelWindow ??= new PanelWindow(_companionManager);

        if (_panelWindow.IsVisible)
        {
            _panelWindow.Hide();
        }
        else
        {
            _panelWindow.ShowNearTray();
        }
    }

    /// <summary>Opens the per-user data folder (logs + interaction history) in Explorer.</summary>
    private static void OpenDiagnosticsFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = AppConfig.UserDataDirectory,
                UseShellExecute = true
            });
        }
        catch
        {
            // Best-effort — opening Explorer should never crash the tray.
        }
    }

    public void Dispose() => _taskbarIcon.Dispose();
}
