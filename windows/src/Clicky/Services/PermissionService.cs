using System.Diagnostics;
using NAudio.Wave;

namespace Clicky.Services;

/// <summary>
/// Windows is far simpler than macOS here: there is no Screen Recording or
/// Accessibility TCC gate, and global keyboard hooks / GDI screen capture work
/// without a prompt. The only user-facing permission is the microphone privacy
/// setting, so this service just reports mic availability and can open the
/// relevant Windows Settings page.
/// </summary>
public sealed class PermissionService
{
    /// <summary>True if at least one capture device is present and openable.</summary>
    public bool HasMicrophone()
    {
        try
        {
            return WaveInEvent.DeviceCount > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Opens Windows Settings → Privacy → Microphone so the user can grant access.</summary>
    public void OpenMicrophonePrivacySettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "ms-settings:privacy-microphone",
                UseShellExecute = true
            });
        }
        catch
        {
            // Non-fatal — the user can open Settings manually.
        }
    }
}
