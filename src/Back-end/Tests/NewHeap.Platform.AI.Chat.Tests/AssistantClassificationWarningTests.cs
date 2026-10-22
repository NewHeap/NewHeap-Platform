using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// The warnings about a data classification that no model profile of the chain permits: an agent
/// or skill in code is logged at startup, which goes on, and a warning names the agent or skill.
/// </summary>
public sealed class AssistantClassificationWarningTests
{
    private const string ChatProfile = "project-chat";
    private const string ConfidentialProfile = "confidential-chat";

    [Fact]
    public void An_agent_or_skill_in_code_whose_profile_chain_does_not_permit_its_classification_is_logged_at_startup()
    {
        var (state, profiles) = Register(assistant => assistant
            .AddAgent(AssistantTestData.Agent() with { ProfileName = ChatProfile })
            .AddAgent(AssistantTestData.Agent("project-auditor") with { ProfileName = ChatProfile })
            .ConfigureAgent("project-assistant", agent => agent
                .Grant("projects.*")
                .DataClassification(NhAiDataClassification.Confidential)
                // Moves to the confidential profile: no warning.
                .Skill("figures", skill => skill
                    .Description("Reviews the figures.")
                    .UseProfile(ConfidentialProfile))
                // Restricted is permitted by no profile of the chain.
                .Skill("vault", skill => skill
                    .Description("Reads the vault.")
                    .UseProfile(ConfidentialProfile)
                    .DataClassification(NhAiDataClassification.Restricted)))
            .ConfigureAgent("project-auditor", agent => agent
                .Grant("projects.*")
                .Skill("notes", skill => skill
                    .Description("Writes notes.")
                    .DataClassification(NhAiDataClassification.Internal))));
        var logs = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(logs));

        NhAssistantClassificationChecks.LogCodeWarnings(state, profiles, factory.CreateLogger("startup"));

        var warnings = logs.Entries.Where(entry => entry.Level == LogLevel.Warning).Select(entry => entry.Message).ToArray();
        Assert.Equal(2, warnings.Length);
        // The agent's own Confidential on the chat profile, which permits only Internal.
        Assert.Contains(warnings, message => message.StartsWith("Agent project-assistant classifies its model calls as Confidential", StringComparison.Ordinal)
            && message.Contains(ChatProfile, StringComparison.Ordinal));
        Assert.Contains(warnings, message => message.StartsWith("Skill vault of agent project-assistant classifies its model calls as Restricted", StringComparison.Ordinal));
        // An agent without a classification of its own and a skill its profile permits are fine.
        Assert.DoesNotContain(warnings, message => message.Contains("project-auditor", StringComparison.Ordinal));
        Assert.DoesNotContain(warnings, message => message.Contains("figures", StringComparison.Ordinal));
    }

    [Fact]
    public void A_profile_chain_permits_a_classification_through_a_fallback_and_warns_otherwise()
    {
        var (_, profiles) = Register(assistant => assistant.AddAgent(AssistantTestData.Agent() with { ProfileName = ChatProfile }));

        Assert.Empty(NhAssistantClassificationChecks.WarningsOf(profiles, "guarded-chat", NhAiDataClassification.Confidential));
        Assert.Empty(NhAssistantClassificationChecks.WarningsOf(profiles, ChatProfile, NhAiDataClassification.Internal));
        // Without a classification of its own nothing is checked, nor for a profile that does not exist.
        Assert.Empty(NhAssistantClassificationChecks.WarningsOf(profiles, ChatProfile, null));
        Assert.Empty(NhAssistantClassificationChecks.WarningsOf(profiles, "missing-chat", NhAiDataClassification.Restricted));
        Assert.Equal(
            [NhAssistantConfigurationWarnings.ClassificationNotPermitted],
            NhAssistantClassificationChecks.WarningsOf(profiles, ChatProfile, NhAiDataClassification.Confidential));
        Assert.Equal(
            [NhAssistantConfigurationWarnings.ClassificationNotPermitted],
            NhAssistantClassificationChecks.WarningsOf(profiles, "guarded-chat", NhAiDataClassification.Restricted));
    }

    /// <summary>
    /// The assistant's registration and the model profiles: a chat profile that permits Internal, a
    /// confidential profile and a guarded profile whose fallback is the confidential one.
    /// </summary>
    private static (NhAssistantRegistrationState State, INhAiModelProfileRegistry Profiles) Register(Action<NhAssistantBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddNewHeapPlatformAI(ai => ai
            .AddChatProfile(ChatProfile, profile => Configure(profile, "chat-model")
                .PermitDataClassifications(NhAiDataClassification.Internal))
            .AddChatProfile(ConfidentialProfile, profile => Configure(profile, "confidential-model")
                .PermitDataClassifications(NhAiDataClassification.Internal, NhAiDataClassification.Confidential))
            .AddChatProfile("guarded-chat", profile => Configure(profile, "chat-model")
                .PermitDataClassifications(NhAiDataClassification.Internal)
                .WithFallbackProfiles(ConfidentialProfile)));
        services.AddNewHeapAssistant(configure);
        var state = (NhAssistantRegistrationState)services
            .Single(descriptor => descriptor.ServiceType == typeof(NhAssistantRegistrationState))
            .ImplementationInstance!;
        return (state, services.BuildServiceProvider().GetRequiredService<INhAiModelProfileRegistry>());
    }

    private static NhAiModelProfileBuilder Configure(NhAiModelProfileBuilder profile, string modelKey)
    {
        return profile
            .UseKeyedClient(modelKey)
            .RequireCapabilities(NhAiModelCapability.FunctionCalling)
            .WithStreaming(NhAiStreamingPolicy.Allowed)
            .PermitExecutionRegions("local")
            .WithBudget(maxInputTokens: 8_192, maxOutputTokens: 1_024, maxCalls: 16);
    }
}
