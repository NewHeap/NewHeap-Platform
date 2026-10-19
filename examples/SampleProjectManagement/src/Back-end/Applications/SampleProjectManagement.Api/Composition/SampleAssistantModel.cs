using System.ClientModel;
using System.Globalization;
using Microsoft.Extensions.AI;
using OpenAI;

namespace SampleProjectManagement.Api.Composition;

/// <summary>
/// The optional real chat model of the sample assistant, read from
/// <c>SampleProjectManagement:Assistant:Model</c>. Without a <c>Provider</c> the deterministic
/// <see cref="SampleAssistantChatClient"/> drives the agents, which is what the runnable sample and
/// every test use. With <c>Provider</c> <c>openai</c> an OpenAI-compatible endpoint (OpenAI, an Azure
/// OpenAI v1 endpoint or a local server) answers instead, so agents and skills whose model decides can
/// be tried with free instructions. The model then runs outside the machine's <c>local</c> region,
/// so the assistant sends its configured region and the profiles on
/// <see cref="SampleAssistantComposition.ModelKey"/> permit it.
/// </summary>
/// <remarks>
/// The settings are validated once at startup and an invalid combination stops the API with a
/// message that names the setting. The API key is never part of a message, a log or
/// <see cref="ToString"/>.
/// </remarks>
public sealed class SampleAssistantModel
{
    public const string SectionName = "SampleProjectManagement:Assistant:Model";
    public const string OpenAiProvider = "openai";
    public const string DefaultRegion = "external";

    /// <summary>
    /// NewHeap configuration loads the sample's <c>secrets.json</c> under this prefix.
    /// </summary>
    private const string SecretsPrefix = "Secrets";

    private readonly string _apiKey;

    private SampleAssistantModel(
        string model,
        string apiKey,
        Uri? endpoint,
        string region,
        int maxInputTokens,
        int maxOutputTokens,
        TimeSpan timeout)
    {
        Model = model;
        _apiKey = apiKey;
        Endpoint = endpoint;
        Region = region;
        MaxInputTokens = maxInputTokens;
        MaxOutputTokens = maxOutputTokens;
        Timeout = timeout;
    }

    /// <summary>The model or deployment name the endpoint serves.</summary>
    public string Model { get; }

    /// <summary>The OpenAI-compatible endpoint, or <see langword="null"/> for OpenAI itself.</summary>
    public Uri? Endpoint { get; }

    /// <summary>The execution region the assistant sends with every model call.</summary>
    public string Region { get; }

    /// <summary>
    /// The input budget per call. The assistant checks its estimate of the instructions, history and
    /// message against it before each call, so it must leave room for the agent's instructions.
    /// </summary>
    public int MaxInputTokens { get; }

    public int MaxOutputTokens { get; }

    public TimeSpan Timeout { get; }

    /// <summary>
    /// Reads and validates the real model. Returns <see langword="null"/> when no provider is
    /// configured, so the deterministic model stays in place.
    /// </summary>
    /// <exception cref="InvalidOperationException">The provider is set but the settings are incomplete or invalid.</exception>
    public static SampleAssistantModel? FromConfiguration(IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var provider = Read(configuration, "Provider");
        if (provider is null)
        {
            return null;
        }

        if (!string.Equals(provider, OpenAiProvider, StringComparison.OrdinalIgnoreCase))
        {
            // The value is not repeated: a key pasted into the wrong setting must not reach a log.
            throw Invalid($"{SectionName}:Provider is not supported. Leave it empty for the deterministic sample model or use '{OpenAiProvider}' for an OpenAI-compatible endpoint.");
        }

        var apiKey = Read(configuration, "ApiKey")
            ?? throw Invalid($"{SectionName}:ApiKey is required when {SectionName}:Provider is '{OpenAiProvider}'. Put it in the sample's secrets.json.");
        var model = Read(configuration, "Model")
            ?? throw Invalid($"{SectionName}:Model is required when {SectionName}:Provider is '{OpenAiProvider}', for example the model or deployment name.");
        var endpoint = ReadEndpoint(configuration, environment);

        var region = Read(configuration, "Region") ?? DefaultRegion;
        if (region.Length > 64 || region.Any(char.IsWhiteSpace))
        {
            throw Invalid($"{SectionName}:Region must be one word of at most 64 characters, such as '{DefaultRegion}'.");
        }

        var maxInputTokens = ReadNumber(configuration, "MaxInputTokens", 32_000, 1, 1_000_000);
        var maxOutputTokens = ReadNumber(configuration, "MaxOutputTokens", 2_000, 1, 100_000);
        var timeoutSeconds = ReadNumber(configuration, "TimeoutSeconds", 60, 1, 900);

        return new SampleAssistantModel(
            model,
            apiKey,
            endpoint,
            region,
            maxInputTokens,
            maxOutputTokens,
            TimeSpan.FromSeconds(timeoutSeconds));
    }

    /// <summary>
    /// Creates the chat client. Creating it opens no connection; the first model call does.
    /// </summary>
    public IChatClient CreateChatClient()
    {
        var options = new OpenAIClientOptions();
        if (Endpoint is not null)
        {
            options.Endpoint = Endpoint;
        }

        return new OpenAI.Chat.ChatClient(Model, new ApiKeyCredential(_apiKey), options).AsIChatClient();
    }

    public override string ToString()
    {
        return $"{OpenAiProvider}:{Model}@{Region}";
    }

    /// <summary>
    /// Appsettings, environment variables and command-line arguments use the key itself and win over
    /// the secrets file.
    /// </summary>
    private static string? Read(IConfiguration configuration, string name)
    {
        var value = configuration[$"{SectionName}:{name}"];
        if (string.IsNullOrWhiteSpace(value))
        {
            value = configuration[$"{SecretsPrefix}:{SectionName}:{name}"];
        }

        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>
    /// The endpoint receives the key and the conversation, so it must be https. Plain http is only
    /// accepted for a local server on loopback in Development.
    /// </summary>
    private static Uri? ReadEndpoint(IConfiguration configuration, IHostEnvironment environment)
    {
        var value = Read(configuration, "Endpoint");
        if (value is null)
        {
            return null;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var endpoint)
            || !string.IsNullOrEmpty(endpoint.UserInfo))
        {
            throw Invalid($"{SectionName}:Endpoint must be an absolute https URL without credentials.");
        }

        var secure = endpoint.Scheme == Uri.UriSchemeHttps;
        var localDevelopment = endpoint.Scheme == Uri.UriSchemeHttp
            && endpoint.IsLoopback
            && environment.IsDevelopment();
        if (!secure && !localDevelopment)
        {
            throw Invalid($"{SectionName}:Endpoint must use https; plain http is only accepted for a loopback address in Development.");
        }

        return endpoint;
    }

    private static int ReadNumber(IConfiguration configuration, string name, int defaultValue, int minimum, int maximum)
    {
        var value = Read(configuration, name);
        if (value is null)
        {
            return defaultValue;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            || number < minimum
            || number > maximum)
        {
            throw Invalid($"{SectionName}:{name} must be a whole number from {minimum} to {maximum}.");
        }

        return number;
    }

    private static InvalidOperationException Invalid(string message)
    {
        return new InvalidOperationException(message);
    }
}
