using System.Windows;
using System.Windows.Threading;
using Clicky.Core;
using Clicky.Diagnostics;
using Clicky.Services;
using Clicky.UI;

namespace Clicky;

/// <summary>
/// Tray-only application entry point — there is no main window (the Windows analog
/// of LSUIElement on macOS). On first run it downloads the local vision model, then
/// it installs the tray icon and starts the companion pipeline.
/// </summary>
public partial class App : Application
{
    private AppConfig? _config;
    private CompanionManager? _companionManager;
    private TrayIconManager? _trayIconManager;

    protected override void OnStartup(StartupEventArgs eventArgs)
    {
        base.OnStartup(eventArgs);

        ClickyLog.Initialize();
        ClickyLog.Info("App", $"Logs at {ClickyLog.LogDirectory}");

        // Surface unexpected errors instead of silently dying with no window.
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        _config = AppConfig.Load();
        ClickyLog.Info("App",
            $"Config loaded. ManageServerProcess={_config.VisionLlm.ManageServerProcess}, " +
            $"models present={new FirstRunModelDownloader(_config.VisionLlm, _config.ModelDownloads).AreModelsPresent()}");

        // First-run: pull the large vision model with a progress UI. Small models
        // (Whisper, Piper) ship in the installer, so they aren't downloaded here.
        var downloader = new FirstRunModelDownloader(_config.VisionLlm, _config.ModelDownloads);
        if (_config.VisionLlm.ManageServerProcess && !downloader.AreModelsPresent())
        {
            var downloadWindow = new ModelDownloadWindow(downloader);
            downloadWindow.ShowDialog();
            if (!downloadWindow.DownloadSucceeded)
            {
                // Continue anyway — the panel will report the missing model and the
                // user can drop files in manually — but the LLM step will be inert.
            }
        }

        _companionManager = new CompanionManager(_config);
        _trayIconManager = new TrayIconManager(_companionManager);
        _companionManager.Start();

        // Best-effort, non-blocking update check (the only network call; fails silently).
        _ = CheckForUpdatesAsync();
    }

    private async Task CheckForUpdatesAsync()
    {
        var update = await UpdateChecker.CheckForUpdateAsync();
        if (update is { } updateInfo && _trayIconManager is not null)
        {
            Dispatcher.Invoke(() => _trayIconManager.NotifyUpdateAvailable(updateInfo.LatestVersion, updateInfo.ReleaseUrl));
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs args)
    {
        ClickyLog.Error("App", "Unhandled dispatcher exception", args.Exception);
        MessageBox.Show(args.Exception.Message, "Clicky", MessageBoxButton.OK, MessageBoxImage.Error);
        args.Handled = true;
    }

    protected override void OnExit(ExitEventArgs eventArgs)
    {
        _companionManager?.Dispose();
        _trayIconManager?.Dispose();
        base.OnExit(eventArgs);
    }
}
