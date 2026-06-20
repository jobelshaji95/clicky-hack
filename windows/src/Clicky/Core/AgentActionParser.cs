using System.Text.RegularExpressions;

namespace Clicky.Core;

/// <summary>The kind of action the agent wants to perform next.</summary>
public enum AgentActionKind
{
    Click,
    Type,
    Key,
    Done,
    Unknown,
}

/// <summary>
/// One parsed agent action plus the model's spoken narration (the text before the tag).
/// </summary>
public readonly record struct AgentAction(
    AgentActionKind Kind,
    string Narration,
    double? X,
    double? Y,
    string? Label,
    string? Text)
{
    public string Summary => Kind switch
    {
        AgentActionKind.Click => $"click {Label ?? "element"} ({X:0},{Y:0})",
        AgentActionKind.Type => $"type \"{Text}\"",
        AgentActionKind.Key => $"press {Text}",
        AgentActionKind.Done => $"done: {Text}",
        _ => "unknown",
    };
}

/// <summary>
/// Parses the single [ACTION:...] tag the model appends in Agent Mode. Mirrors the
/// strict-tag approach of <see cref="PointTagParser"/>.
/// </summary>
public static partial class AgentActionParser
{
    [GeneratedRegex(@"\[ACTION:([^\]]*)\]\s*$", RegexOptions.Singleline)]
    private static partial Regex ActionTagRegex();

    public static AgentAction Parse(string responseText)
    {
        var match = ActionTagRegex().Match(responseText);
        if (!match.Success)
        {
            return new AgentAction(AgentActionKind.Unknown, responseText.Trim(), null, null, null, null);
        }

        var narration = responseText[..match.Index].Trim();
        var body = match.Groups[1].Value.Trim();

        // The verb is the first colon-separated token; the rest is verb-specific.
        var firstColon = body.IndexOf(':');
        var verb = (firstColon >= 0 ? body[..firstColon] : body).Trim().ToLowerInvariant();
        var remainder = firstColon >= 0 ? body[(firstColon + 1)..] : string.Empty;

        switch (verb)
        {
            case "click":
            {
                // remainder = "x,y" or "x,y:label"
                var labelSplit = remainder.IndexOf(':');
                var coordinatePart = labelSplit >= 0 ? remainder[..labelSplit] : remainder;
                var label = labelSplit >= 0 ? remainder[(labelSplit + 1)..].Trim() : null;

                var coordinates = coordinatePart.Split(',', 2);
                if (coordinates.Length == 2 &&
                    double.TryParse(coordinates[0].Trim(), out var x) &&
                    double.TryParse(coordinates[1].Trim(), out var y))
                {
                    return new AgentAction(AgentActionKind.Click, narration, x, y, label, null);
                }
                return new AgentAction(AgentActionKind.Unknown, narration, null, null, null, null);
            }

            case "type":
                return new AgentAction(AgentActionKind.Type, narration, null, null, null, remainder);

            case "key":
                return new AgentAction(AgentActionKind.Key, narration, null, null, null, remainder.Trim());

            case "done":
                return new AgentAction(AgentActionKind.Done, narration, null, null, null, remainder.Trim());

            default:
                return new AgentAction(AgentActionKind.Unknown, narration, null, null, null, null);
        }
    }
}
