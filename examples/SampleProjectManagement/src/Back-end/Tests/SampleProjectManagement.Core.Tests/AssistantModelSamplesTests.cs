using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using NewHeap.Platform.AI;
using NewHeap.Platform.Common.Models;
using SampleProjectManagement.Api.Composition;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// Executable evidence for the optional real model of the assistant (SPM-245): without settings the
/// deterministic sample model stays in place, an OpenAI-compatible model from the sample's secrets
/// replaces it under the same keyed client and runs in its own execution region, and incomplete
/// settings stop the startup with a message that names the setting. No test calls a model.
/// </summary>
public sealed class AssistantModelSamplesTests
{
    private const string Section = SampleAssistantModel.SectionName;

    /// <summary>A generated test value; it is no key of any service.</summary>
    private static readonly string TestApiKey = "sample-test-key-" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task SPM_245_without_model_settings_the_deterministic_model_drives_the_assistant()
    {
        using var provider = Compose(new Dictionary<string, string?>(), Environments.Development);

        var client = provider.GetRequiredKeyedService<IChatClient>(SampleAssistantComposition.ModelKey);
        var profile = GetProfile(provider, SampleAssistantComposition.ProfileName);

        Assert.IsType<SampleAssistantChatClient>(client);
        Assert.Null(provider.GetService<SampleAssistantModel>());
        Assert.Equal(["local"], profile.PermittedExecutionRegions);
        Assert.Equal(8_192, profile.Budget.MaxInputTokens);
        Assert.Equal(1_024, profile.Budget.MaxOutputTokens);
        Assert.Equal(TimeSpan.FromSeconds(30), profile.Timeout);
        Assert.Equal(["local"], GetProfile(provider, SampleProjectPlannerComposition.TriageProfileName).PermittedExecutionRegions);

        var resolution = await ResolveAsync(provider, "local");
        Assert.True(resolution.Success);
        Assert.Same(client, resolution.Data.Client);
    }

    [Fact]
    public async Task SPM_245_an_openai_compatible_model_from_the_secrets_replaces_the_deterministic_model_in_its_own_region()
    {
        // NewHeap configuration loads the sample's secrets.json under the Secrets prefix.
        using var provider = Compose(
            new Dictionary<string, string?>
            {
                [$"Secrets:{Section}:Provider"] = "openai",
                [$"Secrets:{Section}:ApiKey"] = TestApiKey,
                [$"Secrets:{Section}:Model"] = "sample-test-model",
                [$"Secrets:{Section}:Endpoint"] = "https://models.example.test/v1"
            },
            Environments.Development);

        var client = provider.GetRequiredKeyedService<IChatClient>(SampleAssistantComposition.ModelKey);
        var profile = GetProfile(provider, SampleAssistantComposition.ProfileName);

        Assert.IsNotType<SampleAssistantChatClient>(client);
        var metadata = client.GetService<ChatClientMetadata>();
        Assert.NotNull(metadata);
        Assert.Equal("sample-test-model", metadata.DefaultModelId);
        Assert.Equal(new Uri("https://models.example.test/v1"), metadata.ProviderUri);
        Assert.Equal("external", provider.GetRequiredService<SampleAssistantModel>().Region);
        Assert.Equal(["external", "local"], profile.PermittedExecutionRegions.Order(StringComparer.Ordinal));
        Assert.Equal(32_000, profile.Budget.MaxInputTokens);
        Assert.Equal(2_000, profile.Budget.MaxOutputTokens);
        Assert.Equal(TimeSpan.FromSeconds(60), profile.Timeout);
        // Triage of the planner and its variant calls the same model, so their profiles permit it too.
        Assert.Contains("external", GetProfile(provider, SampleProjectPlannerComposition.TriageProfileName).PermittedExecutionRegions);
        Assert.Contains("external", GetProfile(provider, SampleProjectPlannerVariantComposition.TriageProfileName).PermittedExecutionRegions);

        var resolution = await ResolveAsync(provider, "external");
        Assert.True(resolution.Success);
        Assert.Same(client, resolution.Data.Client);
    }

    [Fact]
    public void SPM_245_a_model_without_an_api_key_stops_the_startup_with_a_clear_message()
    {
        var failure = Assert.Throws<InvalidOperationException>(() => Compose(
            new Dictionary<string, string?>
            {
                [$"{Section}:Provider"] = "openai",
                [$"{Section}:Model"] = "sample-test-model"
            },
            Environments.Development));

        Assert.Equal(
            "SampleProjectManagement:Assistant:Model:ApiKey is required when SampleProjectManagement:Assistant:Model:Provider is 'openai'. Put it in the sample's secrets.json.",
            failure.Message);
    }

    [Theory]
    [InlineData("http://models.example.test/v1", "Development")]
    [InlineData("http://localhost:11434/v1", "Production")]
    // Split so the public-source check does not read the fixture as a leaked credential.
    [InlineData("https://user" + ":password@models.example.test/v1", "Development")]
    [InlineData("models.example.test", "Development")]
    public void SPM_245_an_endpoint_is_https_or_loopback_http_in_development(string endpoint, string environmentName)
    {
        var failure = Assert.Throws<InvalidOperationException>(() => Compose(
            ModelSettings(endpoint),
            environmentName));

        Assert.StartsWith("SampleProjectManagement:Assistant:Model:Endpoint must", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(TestApiKey, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("password", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SPM_245_a_local_server_on_loopback_http_serves_in_development()
    {
        using var provider = Compose(ModelSettings("http://localhost:11434/v1"), Environments.Development);

        var model = provider.GetRequiredService<SampleAssistantModel>();

        Assert.Equal(new Uri("http://localhost:11434/v1"), model.Endpoint);
        Assert.DoesNotContain(TestApiKey, model.ToString(), StringComparison.Ordinal);
    }

    private static Dictionary<string, string?> ModelSettings(string endpoint)
    {
        return new Dictionary<string, string?>
        {
            [$"{Section}:Provider"] = "openai",
            [$"{Section}:ApiKey"] = TestApiKey,
            [$"{Section}:Model"] = "sample-test-model",
            [$"{Section}:Endpoint"] = endpoint
        };
    }

    /// <summary>
    /// The model registrations of Program.cs: the assistant, then the planner and its variant, whose
    /// triage profiles share the assistant's keyed model.
    /// </summary>
    private static ServiceProvider Compose(Dictionary<string, string?> settings, string environmentName)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var environment = new HostingEnvironment
        {
            EnvironmentName = environmentName,
            ApplicationName = "SampleProjectManagement.Api"
        };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSampleAssistant(environment, configuration);
        services.AddSampleProjectPlanner();
        services.AddSampleProjectPlannerVariant();
        return services.BuildServiceProvider();
    }

    private static NhAiModelProfile GetProfile(IServiceProvider provider, string name)
    {
        Assert.True(provider.GetRequiredService<INhAiModelProfileRegistry>().TryGet(name, out var profile));
        return profile;
    }

    private static async Task<TaskResult<NhAiResolvedChatProfile>> ResolveAsync(
        ServiceProvider provider,
        string region)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<INhAiModelProfileResolver>()
            .ResolveChatAsync(
                new NhAiModelResolutionRequest(
                    SampleAssistantComposition.ProfileName,
                    NhAiModelCapability.FunctionCalling | NhAiModelCapability.Streaming,
                    NhAiDataClassification.Internal,
                    "project-assistance",
                    region),
                TestContext.Current.CancellationToken);
    }
}
