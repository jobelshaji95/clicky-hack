using System.Windows;
using Clicky.Services;

namespace Clicky.UI;

/// <summary>
/// First-run window that downloads the large vision model with a progress bar.
/// Closes itself once the models are present so the app can continue to the tray.
/// </summary>
public partial class ModelDownloadWindow : Window
{
    private readonly FirstRunModelDownloader _downloader;

    public bool DownloadSucceeded { get; private set; }

    public ModelDownloadWindow(FirstRunModelDownloader downloader)
    {
        InitializeComponent();
        _downloader = downloader;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        var progress = new Progress<ModelDownloadProgress>(report =>
        {
            DownloadProgressBar.Value = report.Fraction;
            var receivedMegabytes = report.BytesReceived / (1024.0 * 1024.0);
            ProgressDetail.Text = report.TotalBytes is { } total
                ? $"{report.FileName} — {receivedMegabytes:F0} MB of {total / (1024.0 * 1024.0):F0} MB"
                : $"{report.FileName} — {receivedMegabytes:F0} MB";
        });

        try
        {
            await _downloader.EnsureModelsDownloadedAsync(progress);
            DownloadSucceeded = true;
        }
        catch (Exception exception)
        {
            DownloadSucceeded = false;
            MessageBox.Show(
                $"Couldn't download the vision model:\n\n{exception.Message}\n\n" +
                "Check your connection and relaunch Clicky, or place the model files in the app's models folder manually.",
                "Clicky setup", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        Close();
    }
}
