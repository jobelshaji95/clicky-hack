using System.Text.RegularExpressions;

namespace Clicky.Core;

/// <summary>
/// Result of parsing a [POINT:...] tag from the LLM's response, mirroring the
/// macOS PointingParseResult struct.
/// </summary>
public readonly record struct PointingParseResult(
    string SpokenText,
    double? PointX,
    double? PointY,
    string? ElementLabel,
    int? ScreenNumber)
{
    public bool HasCoordinate => PointX.HasValue && PointY.HasValue;
}

/// <summary>
/// Parses the trailing coordinate tag the LLM appends to its responses. The tag
/// drives the blue cursor's flight. This is a direct port of the macOS regex so
/// behavior matches exactly: [POINT:none] or [POINT:x,y:label] or [POINT:x,y:label:screenN].
/// </summary>
public static partial class PointTagParser
{
    // Same pattern as CompanionManager.parsePointingCoordinates in Swift.
    [GeneratedRegex(@"\[POINT:(?:none|(\d+)\s*,\s*(\d+)(?::([^\]:\s][^\]:]*?))?(?::screen(\d+))?)\]\s*$",
        RegexOptions.Singleline)]
    private static partial Regex PointTagRegex();

    public static PointingParseResult Parse(string responseText)
    {
        var match = PointTagRegex().Match(responseText);
        if (!match.Success)
        {
            // No tag at all — speak the whole response, point at nothing.
            return new PointingParseResult(responseText, null, null, null, null);
        }

        // Everything before the tag is the spoken text.
        var spokenText = responseText[..match.Index].Trim();

        // [POINT:none] — coordinate groups did not capture.
        if (!match.Groups[1].Success || !match.Groups[2].Success)
        {
            return new PointingParseResult(spokenText, null, null, "none", null);
        }

        if (!double.TryParse(match.Groups[1].Value, out var pointX) ||
            !double.TryParse(match.Groups[2].Value, out var pointY))
        {
            return new PointingParseResult(spokenText, null, null, "none", null);
        }

        string? elementLabel = match.Groups[3].Success ? match.Groups[3].Value.Trim() : null;
        int? screenNumber = match.Groups[4].Success && int.TryParse(match.Groups[4].Value, out var parsedScreen)
            ? parsedScreen
            : null;

        return new PointingParseResult(spokenText, pointX, pointY, elementLabel, screenNumber);
    }
}
