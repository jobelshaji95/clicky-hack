using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Clicky.Core;

namespace Clicky.Ai;

/// <summary>One labeled screenshot to send to the vision model.</summary>
public readonly record struct LabeledImage(byte[] JpegData, string Label);

/// <summary>
/// Talks to the local llama.cpp server's OpenAI-compatible /v1/chat/completions
/// endpoint with image content and SSE streaming. This is the offline replacement
/// for ClaudeAPI — same job (vision + chat + streamed text), same response contract
/// including the trailing [POINT:...] tag, but pointed at localhost.
/// </summary>
public sealed class LocalVisionLlmClient
{
    private readonly VisionLlmConfig _config;

    // Long timeout: local inference on CPU/GPU can be slower than a cloud call.
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromMinutes(5) };

    public LocalVisionLlmClient(VisionLlmConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// Sends the screenshots + conversation history + transcript and streams the
    /// response. <paramref name="onTextChunk"/> fires as text arrives; the full
    /// accumulated text is returned.
    /// </summary>
    public async Task<string> AnalyzeImagesStreamingAsync(
        IReadOnlyList<LabeledImage> images,
        string systemPrompt,
        IReadOnlyList<ConversationExchange> conversationHistory,
        string userPrompt,
        Action<string> onTextChunk,
        CancellationToken cancellationToken = default)
    {
        var requestBody = BuildRequestBody(images, systemPrompt, conversationHistory, userPrompt);

        using var request = new HttpRequestMessage(HttpMethod.Post, _config.ChatCompletionsUrl)
        {
            Content = new StringContent(requestBody.ToJsonString(), Encoding.UTF8, "application/json")
        };

        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var streamReader = new StreamReader(responseStream);

        var accumulatedText = new StringBuilder();

        // Parse server-sent events: each line is "data: {json}" terminated by [DONE].
        while (!streamReader.EndOfStream)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var line = await streamReader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var payload = line["data:".Length..].Trim();
            if (payload == "[DONE]")
            {
                break;
            }

            var textChunk = ExtractDeltaText(payload);
            if (!string.IsNullOrEmpty(textChunk))
            {
                accumulatedText.Append(textChunk);
                onTextChunk(textChunk);
            }
        }

        return accumulatedText.ToString();
    }

    private JsonObject BuildRequestBody(
        IReadOnlyList<LabeledImage> images,
        string systemPrompt,
        IReadOnlyList<ConversationExchange> conversationHistory,
        string userPrompt)
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = systemPrompt }
        };

        // Replay prior exchanges as plain text so the model keeps short-term memory.
        foreach (var exchange in conversationHistory)
        {
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = exchange.UserTranscript });
            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = exchange.AssistantResponse });
        }

        // Current turn: each screenshot as an image_url data URI followed by its
        // label (which carries the pixel dimensions the model uses as its
        // coordinate space), then the spoken transcript.
        var userContent = new JsonArray();
        foreach (var image in images)
        {
            var base64 = Convert.ToBase64String(image.JpegData);
            userContent.Add(new JsonObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JsonObject { ["url"] = $"data:image/jpeg;base64,{base64}" }
            });
            userContent.Add(new JsonObject { ["type"] = "text", ["text"] = image.Label });
        }
        userContent.Add(new JsonObject { ["type"] = "text", ["text"] = userPrompt });

        messages.Add(new JsonObject { ["role"] = "user", ["content"] = userContent });

        return new JsonObject
        {
            ["model"] = _config.ModelName,
            ["max_tokens"] = _config.MaxTokens,
            ["stream"] = true,
            ["messages"] = messages
        };
    }

    private static string? ExtractDeltaText(string ssePayload)
    {
        try
        {
            using var document = JsonDocument.Parse(ssePayload);
            if (!document.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            {
                return null;
            }

            var firstChoice = choices[0];
            if (firstChoice.TryGetProperty("delta", out var delta) &&
                delta.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.String)
            {
                return content.GetString();
            }
        }
        catch (JsonException)
        {
            // Ignore malformed keep-alive lines.
        }

        return null;
    }
}
