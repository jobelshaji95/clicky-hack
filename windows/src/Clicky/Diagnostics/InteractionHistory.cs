using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Clicky.Core;

namespace Clicky.Diagnostics;

/// <summary>
/// One push-to-talk interaction, captured for debugging and tuning. Serialized as a
/// single JSON line so the whole history is a greppable .jsonl file the user (or we)
/// can replay to see exactly what Clicky heard, said, where it pointed, and how long
/// each stage took.
/// </summary>
public sealed class InteractionRecord
{
    [JsonPropertyName("timestamp")]
    public string TimestampUtc { get; set; } = DateTime.UtcNow.ToString("o");

    [JsonPropertyName("transcript")]
    public string? Transcript { get; set; }

    [JsonPropertyName("response")]
    public string? Response { get; set; }

    [JsonPropertyName("pointed")]
    public bool Pointed { get; set; }

    [JsonPropertyName("pointLabel")]
    public string? PointLabel { get; set; }

    [JsonPropertyName("pointX")]
    public int? PointX { get; set; }

    [JsonPropertyName("pointY")]
    public int? PointY { get; set; }

    [JsonPropertyName("screenNumber")]
    public int? ScreenNumber { get; set; }

    [JsonPropertyName("transcriptionMs")]
    public long TranscriptionMs { get; set; }

    [JsonPropertyName("visionMs")]
    public long VisionMs { get; set; }

    [JsonPropertyName("totalMs")]
    public long TotalMs { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

/// <summary>
/// Appends <see cref="InteractionRecord"/>s to %LOCALAPPDATA%\Clicky\history\interactions.jsonl.
/// </summary>
public static class InteractionHistory
{
    private static readonly object WriteLock = new();
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>The folder the history file lives in. Safe to open in Explorer.</summary>
    public static string HistoryDirectory { get; } = Path.Combine(AppConfig.UserDataDirectory, "history");

    /// <summary>The single append-only history file.</summary>
    public static string HistoryFilePath { get; } = Path.Combine(HistoryDirectory, "interactions.jsonl");

    public static void Append(InteractionRecord record)
    {
        try
        {
            var json = JsonSerializer.Serialize(record, SerializerOptions);
            lock (WriteLock)
            {
                Directory.CreateDirectory(HistoryDirectory);
                File.AppendAllText(HistoryFilePath, json + Environment.NewLine, Encoding.UTF8);
            }

            ClickyLog.Info("History",
                $"transcript=\"{Truncate(record.Transcript)}\" response=\"{Truncate(record.Response)}\" " +
                $"pointed={record.Pointed} transcriptionMs={record.TranscriptionMs} " +
                $"visionMs={record.VisionMs} totalMs={record.TotalMs}" +
                (record.Error is null ? "" : $" error=\"{record.Error}\""));
        }
        catch (Exception exception)
        {
            ClickyLog.Error("History", "Failed to append interaction record", exception);
        }
    }

    private static string Truncate(string? text, int max = 120)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }
        var oneLine = text.Replace('\n', ' ').Replace('\r', ' ');
        return oneLine.Length <= max ? oneLine : oneLine[..max] + "…";
    }
}
