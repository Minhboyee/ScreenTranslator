namespace Translator.Providers.OpenAICompatible;

public enum OpenAICompatibleRequestProfile
{
    Standard,
    XiaomiMiMo
}

public sealed record OpenAICompatibleOptions
{
    public OpenAICompatibleOptions(
        string baseEndpoint,
        string model,
        string providerRevision,
        string? apiKey = null,
        OpenAICompatibleRequestProfile requestProfile = OpenAICompatibleRequestProfile.Standard)
        : this(ParseEndpoint(baseEndpoint), model, providerRevision, apiKey, requestProfile)
    {
    }

    public OpenAICompatibleOptions(
        Uri baseEndpoint,
        string model,
        string providerRevision,
        string? apiKey = null,
        OpenAICompatibleRequestProfile requestProfile = OpenAICompatibleRequestProfile.Standard)
    {
        BaseEndpoint = ValidateEndpoint(baseEndpoint);
        Model = ValidateRequired(model, nameof(model));
        ProviderRevision = ValidateRequired(providerRevision, nameof(providerRevision));
        ApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        if (!Enum.IsDefined(requestProfile))
        {
            throw new ArgumentOutOfRangeException(nameof(requestProfile));
        }

        RequestProfile = requestProfile;
    }

    public Uri BaseEndpoint { get; }

    public string Model { get; }

    public string ProviderRevision { get; }

    public string? ApiKey { get; }

    public OpenAICompatibleRequestProfile RequestProfile { get; }

    private static Uri ParseEndpoint(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var endpoint))
        {
            throw new ArgumentException("Base endpoint must be an absolute HTTP or HTTPS URI.", nameof(value));
        }

        return endpoint;
    }

    private static Uri ValidateEndpoint(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri ||
            (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new ArgumentException("Base endpoint must be an absolute HTTP or HTTPS URI without a query or fragment.", nameof(endpoint));
        }

        return endpoint;
    }

    private static string ValidateRequired(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value);
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("Value must contain a non-whitespace value.", parameterName);
        }

        return trimmed;
    }
}
