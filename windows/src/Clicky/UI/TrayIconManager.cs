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

        var quitItem = new MenuItem { Header = "Quit Clicky" };
        quitItem.Click += (_, _) => Application.Current.Shutdown();

        var menu = new ContextMenu();
        menu.Items.Add(openItem);
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

    public void Dispose() => _taskbarIcon.Dispose();
}
