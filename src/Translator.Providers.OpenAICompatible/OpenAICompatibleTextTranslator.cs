using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Translator.Core;

namespace Translator.Providers.OpenAICompatible;

public sealed class OpenAICompatibleTextTranslator : ITextTranslator
{
    private const int MaxProviderErrorBodyBytes = 16 * 1024;
    private const int MaxProviderDiagnosticValueLength = 256;

    private const string TranslationSystemInstruction =
        "You are a faithful translation engine. Translate the user's source text from the specified source language to the specified target language. Preserve meaning, formatting, punctuation, and markup. Return only the translation, with no explanation or commentary.";

    private readonly HttpClient httpClient;
    private readonly OpenAICompatibleOptions options;
    private readonly Uri completionsEndpoint;

    public OpenAICompatibleTextTranslator(
        HttpClient httpClient,
        OpenAICompatibleOptions options)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        completionsEndpoint = CreateCompletionsEndpoint(options.BaseEndpoint);
    }

    public async Task<TranslationResult> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var payload = CreateRequestPayload(request);
        var json = JsonSerializer.Serialize(payload);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, completionsEndpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (options.ApiKey is not null)
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        }

        using var response = await httpClient.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var providerError = await ReadProviderErrorAsync(
                    response,
                    request.Text.Value,
                    options.ApiKey,
                    cancellationToken)
                .ConfigureAwait(false);
            var message = BuildFailureMessage(response, providerError);

            throw new HttpRequestException(
                message,
                inner: null,
                response.StatusCode);
        }

        var translatedText = await ReadTranslationAsync(response, cancellationToken).ConfigureAwait(false);
        return new TranslationResult(request, translatedText);
    }

    private ChatCompletionRequest CreateRequestPayload(TranslationRequest request)
    {
        var messages = new[]
        {
            new ChatMessage("system", TranslationSystemInstruction),
            new ChatMessage(
                "user",
                $"Source language: {request.LanguagePair.SourceLanguage}\nTarget language: {request.LanguagePair.TargetLanguage}\n\nSource text:\n{request.Text.Value}")
        };

        return options.RequestProfile switch
        {
            OpenAICompatibleRequestProfile.Standard => new ChatCompletionRequest(
                options.Model,
                messages),
            OpenAICompatibleRequestProfile.XiaomiMiMo => new ChatCompletionRequest(
                options.Model,
                messages,
                Stream: false,
                Thinking: new ThinkingOptions("disabled"),
                MaxCompletionTokens: 2048,
                Temperature: 0.1),
            _ => throw new InvalidOperationException("Unsupported OpenAI-compatible request profile.")
        };
    }

    private static Uri CreateCompletionsEndpoint(Uri baseEndpoint)
    {
        var builder = new UriBuilder(baseEndpoint);
        var path = builder.Path.TrimEnd('/');
        if (path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            builder.Path = path + "/chat/completions";
        }
        else
        {
            builder.Path = path + "/v1/chat/completions";
        }

        return builder.Uri;
    }

    private static async Task<string> ReadTranslationAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using var responseStream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            using var document = await JsonDocument.ParseAsync(
                    responseStream,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!document.RootElement.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0)
            {
                throw MissingTranslation();
            }

            var firstChoice = choices[0];
            if (!firstChoice.TryGetProperty("message", out var message) ||
                message.ValueKind != JsonValueKind.Object ||
                !message.TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.String)
            {
                throw MissingTranslation();
            }

            var translatedText = content.GetString();
            if (string.IsNullOrWhiteSpace(translatedText))
            {
                throw MissingTranslation();
            }

            return translatedText;
        }
        catch (JsonException)
        {
            throw new InvalidOperationException(
                "OpenAI-compatible response was not valid JSON.");
        }
    }

    private static InvalidOperationException MissingTranslation()
    {
        return new InvalidOperationException(
            "OpenAI-compatible response did not contain a non-blank translation.");
    }

    private static async Task<ProviderErrorDetails?> ReadProviderErrorAsync(
        HttpResponseMessage response,
        string sourceText,
        string? apiKey,
        CancellationToken cancellationToken)
    {
        string? responseBody;
        try
        {
            responseBody = await ReadBoundedResponseBodyAsync(response.Content, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var error = root;
            if (root.TryGetProperty("error", out var nestedError) &&
                nestedError.ValueKind == JsonValueKind.Object)
            {
                error = nestedError;
            }

            var code = ReadProviderDiagnosticValue(error, "code", sourceText, apiKey);
            var message = ReadProviderDiagnosticValue(error, "message", sourceText, apiKey);
            return code is null && message is null
                ? null
                : new ProviderErrorDetails(code, message);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<string?> ReadBoundedResponseBodyAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using var responseStream = await content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var body = new MemoryStream();
        var buffer = new byte[4096];

        while (body.Length < MaxProviderErrorBodyBytes)
        {
            var bytesToRead = (int)Math.Min(buffer.Length, MaxProviderErrorBodyBytes - body.Length);
            var bytesRead = await responseStream
                .ReadAsync(buffer.AsMemory(0, bytesToRead), cancellationToken)
                .ConfigureAwait(false);
            if (bytesRead == 0)
            {
                break;
            }

            body.Write(buffer, 0, bytesRead);
        }

        return body.Length == 0
            ? null
            : Encoding.UTF8.GetString(body.GetBuffer(), 0, (int)body.Length);
    }

    private static string? ReadProviderDiagnosticValue(
        JsonElement error,
        string propertyName,
        string sourceText,
        string? apiKey)
    {
        if (!error.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        var rawValue = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
            _ => null
        };

        return rawValue is null
            ? null
            : SanitizeProviderDiagnosticValue(rawValue, sourceText, apiKey);
    }

    private static string? SanitizeProviderDiagnosticValue(
        string value,
        string sourceText,
        string? apiKey)
    {
        var sanitized = new string(value.Select(character => char.IsControl(character) ? ' ' : character).ToArray()).Trim();
        foreach (var sensitiveValue in new[] { sourceText, apiKey })
        {
            if (!string.IsNullOrEmpty(sensitiveValue))
            {
                sanitized = sanitized.Replace(
                    sensitiveValue,
                    "[redacted]",
                    StringComparison.OrdinalIgnoreCase);
            }
        }

        if (sanitized.Length == 0)
        {
            return null;
        }

        return sanitized.Length <= MaxProviderDiagnosticValueLength
            ? sanitized
            : sanitized[..MaxProviderDiagnosticValueLength];
    }

    private static string BuildFailureMessage(
        HttpResponseMessage response,
        ProviderErrorDetails? providerError)
    {
        var message = new StringBuilder(
            $"OpenAI-compatible translation request failed with status code {(int)response.StatusCode}.");
        if (providerError?.Code is not null)
        {
            message.Append(" Provider error code: ").Append(providerError.Code).Append('.');
        }

        if (providerError?.Message is not null)
        {
            message.Append(" Provider error message: ").Append(providerError.Message).Append('.');
        }

        return message.ToString();
    }

    private sealed record ChatCompletionRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] ChatMessage[] Messages,
        [property: JsonPropertyName("stream")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        bool? Stream = null,
        [property: JsonPropertyName("thinking")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        ThinkingOptions? Thinking = null,
        [property: JsonPropertyName("max_completion_tokens")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        int? MaxCompletionTokens = null,
        [property: JsonPropertyName("temperature")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double? Temperature = null);

    private sealed record ThinkingOptions(
        [property: JsonPropertyName("type")] string Type);

    private sealed record ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record ProviderErrorDetails(string? Code, string? Message);
}
