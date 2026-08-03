using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Translator.Core;
using Translator.Providers.OpenAICompatible;

namespace Translator.Providers.OpenAICompatible.Tests;

public sealed class OpenAICompatibleTextTranslatorTests
{
    [Fact]
    public async Task Sends_expected_path_body_and_omits_auth_without_an_api_key()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;
        using var handler = new FakeHttpMessageHandler(async (request, _) =>
        {
            capturedRequest = request;
            capturedBody = await request.Content!.ReadAsStringAsync();
            return JsonResponse("translated");
        });
        using var client = new HttpClient(handler);
        var translator = CreateTranslator(client);

        await translator.TranslateAsync(Request("Hello"));

        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest!.Method);
        Assert.Equal("https://provider.test/v1/chat/completions", capturedRequest.RequestUri!.AbsoluteUri);
        Assert.Null(capturedRequest.Headers.Authorization);

        using var body = JsonDocument.Parse(capturedBody!);
        Assert.Equal("model-a", body.RootElement.GetProperty("model").GetString());
        var messages = body.RootElement.GetProperty("messages");
        Assert.Equal(JsonValueKind.Array, messages.ValueKind);
        Assert.Equal(2, messages.GetArrayLength());
        Assert.False(body.RootElement.TryGetProperty("stream", out _));
        Assert.False(body.RootElement.TryGetProperty("thinking", out _));
        Assert.False(body.RootElement.TryGetProperty("max_completion_tokens", out _));
        Assert.False(body.RootElement.TryGetProperty("temperature", out _));
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Contains("faithful translation", messages[0].GetProperty("content").GetString(), StringComparison.Ordinal);
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        var userContent = messages[1].GetProperty("content").GetString();
        Assert.Equal("Source language: en\nTarget language: fr\n\nSource text:\nHello", userContent);
        Assert.DoesNotContain("provider-revision", userContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sends_bearer_auth_when_an_api_key_is_configured()
    {
        HttpRequestHeaders? headers = null;
        using var handler = new FakeHttpMessageHandler(request =>
        {
            headers = request.Headers;
            return JsonResponse("translated");
        });
        using var client = new HttpClient(handler);
        var translator = new OpenAICompatibleTextTranslator(
            client,
            new OpenAICompatibleOptions("https://provider.test", "model-a", "provider-revision", "secret-key"));

        await translator.TranslateAsync(Request("Hello"));

        Assert.NotNull(headers);
        Assert.Equal(new AuthenticationHeaderValue("Bearer", "secret-key"), headers!.Authorization);
    }

    [Fact]
    public async Task Sends_documented_fields_for_the_xiaomi_mimo_profile()
    {
        Uri? capturedUri = null;
        string? capturedBody = null;
        using var handler = new FakeHttpMessageHandler(async (request, _) =>
        {
            capturedUri = request.RequestUri;
            capturedBody = await request.Content!.ReadAsStringAsync();
            return JsonResponse("translated");
        });
        using var client = new HttpClient(handler);
        var translator = new OpenAICompatibleTextTranslator(
            client,
            new OpenAICompatibleOptions(
                "https://api.xiaomimimo.com/v1",
                "mimo-model",
                "provider-revision",
                requestProfile: OpenAICompatibleRequestProfile.XiaomiMiMo));

        await translator.TranslateAsync(Request("Hello"));

        Assert.Equal("https://api.xiaomimimo.com/v1/chat/completions", capturedUri!.AbsoluteUri);
        using var body = JsonDocument.Parse(capturedBody!);
        var propertyNames = body.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
        Assert.Equal(
            ["model", "messages", "stream", "thinking", "max_completion_tokens", "temperature"],
            propertyNames);
        Assert.Equal("mimo-model", body.RootElement.GetProperty("model").GetString());
        Assert.False(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal("disabled", body.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal(2048, body.RootElement.GetProperty("max_completion_tokens").GetInt32());
        Assert.Equal(0.1, body.RootElement.GetProperty("temperature").GetDouble());
    }

    [Fact]
    public async Task Parses_the_first_message_content()
    {
        using var handler = new FakeHttpMessageHandler(_ => JsonResponse("Bonjour"));
        using var client = new HttpClient(handler);
        var result = await CreateTranslator(client).TranslateAsync(Request("Hello"));

        Assert.Equal("Bonjour", result.TranslatedText);
        Assert.Equal("Hello", result.Request.Text.Value);
    }

    [Fact]
    public async Task Parses_final_content_from_a_xiaomi_mimo_response_with_reasoning()
    {
        using var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"choices\":[{\"message\":{\"reasoning_content\":\"internal\",\"content\":\"Bonjour\"}}]}",
                Encoding.UTF8,
                "application/json")
        });
        using var client = new HttpClient(handler);
        var translator = new OpenAICompatibleTextTranslator(
            client,
            new OpenAICompatibleOptions(
                "https://api.xiaomimimo.com/v1",
                "mimo-model",
                "provider-revision",
                requestProfile: OpenAICompatibleRequestProfile.XiaomiMiMo));

        var result = await translator.TranslateAsync(Request("Hello"));

        Assert.Equal("Bonjour", result.TranslatedText);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"choices\":[]}")]
    [InlineData("{\"choices\":[{\"message\":{\"content\":\" \"}}]}")]
    [InlineData("{\"choices\":[{\"message\":{\"reasoning_content\":\"internal only\"}}]}")]
    [InlineData("not-json")]
    public async Task Rejects_malformed_or_blank_responses(string responseBody)
    {
        using var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
        });
        using var client = new HttpClient(handler);
        var exception = await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => CreateTranslator(client).TranslateAsync(Request("Hello")));

        Assert.DoesNotContain("Hello", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-key", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(responseBody, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "invalid_api_key", "The supplied credential is invalid.")]
    [InlineData(HttpStatusCode.Forbidden, "access_denied", "The model is not available to this account.")]
    [InlineData(HttpStatusCode.TooManyRequests, "rate_limit_exceeded", "Too many requests.")]
    public async Task Exposes_structured_provider_error_without_exposing_raw_body(
        HttpStatusCode statusCode,
        string providerCode,
        string providerMessage)
    {
        const string sourceText = "private source text";
        const string apiKey = "secret-key";
        var responseBody = JsonSerializer.Serialize(new
        {
            error = new
            {
                code = providerCode,
                message = providerMessage
            }
        });
        using var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
        });
        using var client = new HttpClient(handler);
        var translator = new OpenAICompatibleTextTranslator(
            client,
            new OpenAICompatibleOptions("https://provider.test", "model-a", "provider-revision", apiKey));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => translator.TranslateAsync(Request(sourceText)));

        Assert.Equal(statusCode, exception.StatusCode);
        Assert.Contains(((int)statusCode).ToString(), exception.Message, StringComparison.Ordinal);
        Assert.Contains(providerCode, exception.Message, StringComparison.Ordinal);
        Assert.Contains(providerMessage, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(responseBody, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(sourceText, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(apiKey, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Honors_cancellation_during_http_send()
    {
        using var requestStarted = new ManualResetEventSlim();
        using var handler = new FakeHttpMessageHandler(async (_, cancellationToken) =>
        {
            requestStarted.Set();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return JsonResponse("unreachable");
        });
        using var client = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var task = CreateTranslator(client).TranslateAsync(Request("Hello"), cancellation.Token);
        requestStarted.Wait(TimeSpan.FromSeconds(2));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public void Validates_endpoint_and_model_before_sending()
    {
        Assert.Throws<ArgumentException>(() => new OpenAICompatibleOptions("not-an-endpoint", "model-a", "revision"));
        Assert.Throws<ArgumentException>(() => new OpenAICompatibleOptions("https://provider.test", " ", "revision"));
    }

    private static OpenAICompatibleTextTranslator CreateTranslator(HttpClient client)
    {
        return new OpenAICompatibleTextTranslator(
            client,
            new OpenAICompatibleOptions("https://provider.test", "model-a", "provider-revision"));
    }

    private static TranslationRequest Request(string text)
    {
        return new TranslationRequest(text, "en", "fr", "provider-revision");
    }

    private static HttpResponseMessage JsonResponse(string translatedText)
    {
        var payload = new
        {
            choices = new[]
            {
                new
                {
                    message = new
                    {
                        content = translatedText
                    }
                }
            }
        };

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json")
        };
    }

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder;

        public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
            : this((request, _) => Task.FromResult(responder(request)))
        {
        }

        public FakeHttpMessageHandler(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        {
            this.responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return responder(request, cancellationToken);
        }
    }
}
