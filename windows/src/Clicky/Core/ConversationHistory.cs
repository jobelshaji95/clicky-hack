namespace Clicky.Core;

/// <summary>One user/assistant exchange kept for short-term conversational memory.</summary>
public readonly record struct ConversationExchange(string UserTranscript, string AssistantResponse);

/// <summary>
/// Bounded conversation memory so the local LLM remembers prior exchanges within
/// a session. Mirrors the macOS behavior of keeping the last 10 exchanges.
/// </summary>
public sealed class ConversationHistory
{
    private const int MaximumRetainedExchanges = 10;
    private readonly List<ConversationExchange> _exchanges = new();

    public IReadOnlyList<ConversationExchange> Exchanges => _exchanges;

    public void Append(string userTranscript, string assistantResponse)
    {
        _exchanges.Add(new ConversationExchange(userTranscript, assistantResponse));

        // Trim from the front so context never grows without bound.
        if (_exchanges.Count > MaximumRetainedExchanges)
        {
            _exchanges.RemoveRange(0, _exchanges.Count - MaximumRetainedExchanges);
        }
    }

    public void Clear() => _exchanges.Clear();
}
