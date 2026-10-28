using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// What the composition switch is asked in each channel, the default that reads configuration, the
/// ceiling in code and a switch that throws. No database: the turn state alone decides the channel.
/// </summary>
public sealed class AssistantCompositionChannelTests
{
    private const string Person = "person-1";
    private const string Asker = "asker-1";

    [Fact]
    public void A_persons_chat_is_the_chat_channel_for_that_person()
    {
        var state = State();

        var context = NhAssistantTurnRunner.CompositionContextOf(state, NhAiCompositionFeature.Scripts);

        Assert.Equal(NhAssistantCompositionChannel.Chat, context.Channel);
        Assert.Equal(NhAiCompositionFeature.Scripts, context.Feature);
        Assert.Equal(Person, context.PersonActorId);
        Assert.Equal("tenant-1", context.TenantId);
        Assert.Equal("project-assistant", context.AgentId);
    }

    [Fact]
    public void A_message_a_person_types_in_an_agents_thread_is_the_chat_channel()
    {
        var state = State(agentTurn: AgentTurn(NhAssistantTriggerIds.Manual, Person, isRequestTurn: true));

        var context = NhAssistantTurnRunner.CompositionContextOf(state, NhAiCompositionFeature.Compositions);

        Assert.Equal(NhAssistantCompositionChannel.Chat, context.Channel);
        Assert.Equal(Person, context.PersonActorId);
    }

    [Theory]
    [InlineData("project-changed", null)]
    [InlineData(NhAssistantTriggerIds.Manual, Person)]
    public void A_run_that_a_trigger_or_a_person_started_is_the_run_channel_for_its_initiator(string triggerId, string? initiator)
    {
        var state = State(agentTurn: AgentTurn(triggerId, initiator, isRequestTurn: false));

        var context = NhAssistantTurnRunner.CompositionContextOf(state, NhAiCompositionFeature.Compositions);

        Assert.Equal(NhAssistantCompositionChannel.Run, context.Channel);
        Assert.Equal(initiator, context.PersonActorId);
    }

    [Fact]
    public void A_run_another_agent_handed_work_to_is_the_hand_off_channel_for_nobody()
    {
        var state = State(agentTurn: AgentTurn(NhAssistantTriggerIds.HandOff, null, isRequestTurn: false));

        var context = NhAssistantTurnRunner.CompositionContextOf(state, NhAiCompositionFeature.Scripts);

        Assert.Equal(NhAssistantCompositionChannel.HandOff, context.Channel);
        Assert.Null(context.PersonActorId);
    }

    [Fact]
    public void A_consultation_is_the_consultation_channel_for_the_person_who_asked()
    {
        var consultation = new NhAssistantConsultationScope(
            Guid.NewGuid(),
            new NhAssistantConsultationOptions("project-assistant", "project"),
            [],
            1,
            false,
            "asking-agent",
            null!)
        {
            AskerActorId = Asker
        };
        var state = State(consultation: consultation);

        var context = NhAssistantTurnRunner.CompositionContextOf(state, NhAiCompositionFeature.Compositions);

        Assert.Equal(NhAssistantCompositionChannel.Consultation, context.Channel);
        Assert.Equal(Asker, context.PersonActorId);
    }

    [Theory]
    [InlineData(null, null, false, false)]
    [InlineData("true", null, true, false)]
    [InlineData(null, "true", false, true)]
    [InlineData("false", "true", false, true)]
    public async Task The_default_switch_reads_configuration_and_is_off_without_it(
        string? compositions,
        string? scripts,
        bool compositionsOn,
        bool scriptsOn)
    {
        var options = new NhAssistantOptions
        {
            Compositions = bool.TryParse(compositions, out var c) && c,
            Scripts = bool.TryParse(scripts, out var s) && s
        };
        var configured = new NhAssistantConfigurationCompositionSwitch(new FixedOptions(options));

        Assert.Equal(compositionsOn, await configured.IsOnAsync(Context(NhAiCompositionFeature.Compositions), CancellationToken.None));
        Assert.Equal(scriptsOn, await configured.IsOnAsync(Context(NhAiCompositionFeature.Scripts), CancellationToken.None));
        Assert.False(new NhAssistantOptions().Compositions);
        Assert.False(new NhAssistantOptions().Scripts);
    }

    [Fact]
    public async Task The_ceiling_in_code_wins_and_the_switch_is_not_asked_for_an_agent_that_does_not_allow_the_feature()
    {
        var registration = new NhAssistantRegistrationState();
        registration.GetOrAddAgentContext("allows-scripts").AllowScripts = true;
        var state = new TestCompositionSwitchState();
        var gate = new NhAssistantCompositionGate(new TestCompositionSwitch(state), registration, NullLogger<NhAssistantCompositionGate>.Instance);

        Assert.True(await gate.IsOfferedAsync("allows-scripts", NhAiCompositionFeature.Scripts, Context(NhAiCompositionFeature.Scripts), CancellationToken.None));
        Assert.False(await gate.IsOfferedAsync("allows-scripts", NhAiCompositionFeature.Compositions, Context(NhAiCompositionFeature.Compositions), CancellationToken.None));
        Assert.False(await gate.IsOfferedAsync("in-the-administration", NhAiCompositionFeature.Scripts, Context(NhAiCompositionFeature.Scripts), CancellationToken.None));

        var asked = Assert.Single(state.Asked);
        Assert.Equal(NhAiCompositionFeature.Scripts, asked.Feature);
    }

    [Fact]
    public async Task A_switch_that_throws_counts_as_off_and_the_log_names_only_the_exception_type()
    {
        var registration = new NhAssistantRegistrationState();
        registration.GetOrAddAgentContext("project-assistant").AllowCompositions = true;
        var state = new TestCompositionSwitchState { Failure = new InvalidOperationException("Tenant data must not reach the log.") };
        var logs = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(logging => logging.AddProvider(logs));
        var gate = new NhAssistantCompositionGate(new TestCompositionSwitch(state), registration, factory.CreateLogger<NhAssistantCompositionGate>());

        var on = await gate.IsOfferedAsync("project-assistant", NhAiCompositionFeature.Compositions, Context(NhAiCompositionFeature.Compositions), CancellationToken.None);

        Assert.False(on);
        Assert.Contains(logs.Messages, message => message.Contains(nameof(InvalidOperationException), StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Messages, message => message.Contains("must not reach", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_turn_decides_each_feature_once()
    {
        var decisions = new NhAssistantTurnCompositions();
        var asked = 0;

        Assert.Null(decisions.DecisionOf(NhAiCompositionFeature.Scripts));
        Assert.True(await decisions.DecideAsync(NhAiCompositionFeature.Scripts, () => ValueTask.FromResult(++asked == 1)));
        Assert.True(await decisions.DecideAsync(NhAiCompositionFeature.Scripts, () => ValueTask.FromResult(++asked == 1)));

        Assert.Equal(1, asked);
        Assert.True(decisions.DecisionOf(NhAiCompositionFeature.Scripts));
    }

    [Fact]
    public void The_refusal_codes_and_names_are_stable()
    {
        Assert.Equal("assistant-composition-off", NhAiCompositionFeatures.OffCodeOf(NhAiCompositionFeature.Compositions));
        Assert.Equal("assistant-scripting-off", NhAiCompositionFeatures.OffCodeOf(NhAiCompositionFeature.Scripts));
        Assert.Equal(NhAssistantErrorCodes.CompositionOff, NhAiCompositionFeatures.CompositionOffCode);
        Assert.Equal(NhAssistantErrorCodes.ScriptingOff, NhAiCompositionFeatures.ScriptingOffCode);
        Assert.Equal("compositions", NhAiCompositionFeatures.NameOf(NhAiCompositionFeature.Compositions));
        Assert.Equal("scripts", NhAiCompositionFeatures.NameOf(NhAiCompositionFeature.Scripts));
        Assert.Throws<ArgumentOutOfRangeException>(() => NhAiCompositionFeatures.OffCodeOf(NhAiCompositionFeature.None));
        Assert.Equal(NhAiCompositionFeature.None, TestProjectToolCatalog.Search.CompositionFeature);
    }

    private static NhAssistantCompositionSwitchContext Context(NhAiCompositionFeature feature)
    {
        return new NhAssistantCompositionSwitchContext(feature, NhAssistantCompositionChannel.Chat) { AgentId = "project-assistant" };
    }

    private static NhAssistantAgentTurnContext AgentTurn(string triggerId, string? initiator, bool isRequestTurn)
    {
        var thread = new AssistantThread
        {
            ConversationId = Guid.NewGuid(),
            AgentId = "project-assistant",
            SubjectType = "project",
            SubjectId = "project-1",
            AccountableOwnerId = "owner-1"
        };
        var subject = new NhAiSubjectInfo(new NhAiSubjectRef("project", "project-1"), NhAiSubjectInfo.CreateScopeKey(null, null));
        return new NhAssistantAgentTurnContext(
            thread,
            subject,
            new NhAssistantAgentRunOptions("project-assistant"),
            Guid.NewGuid(),
            triggerId,
            initiator,
            "owner-1",
            isRequestTurn);
    }

    private static NhAssistantTurnState State(
        NhAssistantAgentTurnContext? agentTurn = null,
        NhAssistantConsultationScope? consultation = null)
    {
        var agent = AssistantTestData.Agent();
        var scope = new NhAssistantTurnScope
        {
            ConversationId = Guid.NewGuid(),
            TurnId = Guid.NewGuid(),
            Agent = agent,
            OwnerActorId = Person,
            AccountableOwnerId = Person,
            ModelProfileName = agent.ProfileName,
            PromptVersion = "1",
            PromptHash = AssistantTestData.Instructions.Manifest.ContentHash,
            Deadline = DateTimeOffset.UtcNow.AddMinutes(2),
            Limits = new NhAssistantLimits(),
            Consultation = consultation,
            TenantId = "tenant-1"
        };
        return new NhAssistantTurnState
        {
            Scope = scope,
            Conversation = new AssistantConversation { Id = scope.ConversationId, OwnerActorId = Person, AgentId = agent.Id },
            EffectiveAgent = new NhAssistantAgent(agent, [], true, "code", false, DateTimeOffset.UtcNow),
            AgentTurn = agentTurn,
            UserMessageId = Guid.NewGuid(),
            AssistantMessageId = Guid.NewGuid(),
            Events = Channel.CreateUnbounded<NhAssistantTurnEvent>().Writer
        };
    }

    private sealed class FixedOptions(NhAssistantOptions value) : IOptionsMonitor<NhAssistantOptions>
    {
        public NhAssistantOptions CurrentValue => value;

        public NhAssistantOptions Get(string? name)
        {
            return value;
        }

        public IDisposable? OnChange(Action<NhAssistantOptions, string?> listener)
        {
            return null;
        }
    }
}
