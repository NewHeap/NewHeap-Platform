using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.Chat.Persistence;

namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// Configures the assistant registered by <c>AddNewHeapAssistant</c>. Choose one storage provider
/// with <c>UseSqlServer</c> or <c>UsePostgreSql</c>.
/// </summary>
public sealed class NhAssistantBuilder
{
    private readonly NhAssistantRegistrationState _state;

    internal NhAssistantBuilder(IServiceCollection services, NhAssistantRegistrationState state)
    {
        Services = services;
        _state = state;
    }

    /// <summary>
    /// The service collection the assistant is registered in.
    /// </summary>
    public IServiceCollection Services { get; }

    /// <summary>
    /// Sets the authorization policy every assistant endpoint requires. Overrides
    /// <c>NewHeap:AI:Assistant:AccessPolicy</c>; the default is <c>app.assistant.access</c>.
    /// </summary>
    public NhAssistantBuilder UseAccessPolicy(string policyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);
        if (policyName.Length > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(policyName));
        }
        _state.AccessPolicy = policyName;
        return this;
    }

    /// <summary>
    /// Declares the chat profile the assistant requires. Startup fails when the profile is not
    /// registered or cannot call functions and stream.
    /// </summary>
    public NhAssistantBuilder UseChatProfile(string profileName)
    {
        NhAssistantNames.ValidateSegment(profileName, nameof(profileName));
        _state.ChatProfileName = profileName;
        return this;
    }

    /// <summary>
    /// Adds an agent. Adding the same definition twice is idempotent; a different definition
    /// with the same id and version is rejected.
    /// </summary>
    public NhAssistantBuilder AddAgent(NhAssistantAgentDefinition agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        _state.AddAgent(agent);
        return this;
    }

    /// <summary>
    /// Adds a consumer audit sink that receives content-free assistant events.
    /// </summary>
    public NhAssistantBuilder AddBusinessAuditSink<TSink>()
        where TSink : class, INhAssistantBusinessAuditSink
    {
        Services.TryAddEnumerable(
            ServiceDescriptor.Scoped<INhAssistantBusinessAuditSink, TSink>());
        return this;
    }

    /// <summary>
    /// Adds an optional presenter for localized, user-facing tool names and approval explanations.
    /// Presenters cannot alter authorization, proposal evidence or approval decisions.
    /// </summary>
    public NhAssistantBuilder AddToolPresenter<TPresenter>()
        where TPresenter : class, INhAssistantToolPresenter
    {
        Services.TryAddEnumerable(
            ServiceDescriptor.Scoped<INhAssistantToolPresenter, TPresenter>());
        return this;
    }

    /// <summary>
    /// Replaces the default title generator, which uses the first line of the first message.
    /// </summary>
    public NhAssistantBuilder UseTitleGenerator<TGenerator>()
        where TGenerator : class, INhAssistantTitleGenerator
    {
        Services.Replace(ServiceDescriptor.Scoped<INhAssistantTitleGenerator, TGenerator>());
        return this;
    }

    /// <summary>
    /// Configures turn limits, approval lifetime and durable daily budgets.
    /// </summary>
    public NhAssistantBuilder WithLimits(Action<NhAssistantLimits> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var limits = _state.Limits.Clone();
        configure(limits);
        limits.Validate();
        _state.Limits = limits;
        return this;
    }

    /// <summary>
    /// Sets the authorization policy the administration endpoints require in addition to the
    /// access policy. Overrides <c>NewHeap:AI:Assistant:AdminPolicy</c> (default <c>app.assistant.admin</c>).
    /// </summary>
    public NhAssistantBuilder UseAdminPolicy(string policyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);
        if (policyName.Length > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(policyName));
        }
        _state.AdminPolicy = policyName;
        return this;
    }

    /// <summary>
    /// Seeds the application context as version 1 when no context is stored yet. A changed seed never
    /// replaces a context that already exists, including one an administrator edited.
    /// </summary>
    public NhAssistantBuilder UseDefaultApplicationContext(NhAiTextAsset context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(context.Content)
            || context.Content.Length > NhAssistantApplicationContexts.MaxTextLength)
        {
            throw new ArgumentException(
                "The default application context must contain at most 20,000 characters.",
                nameof(context));
        }
        _state.DefaultApplicationContext = context;
        return this;
    }

    /// <summary>
    /// Configures the connection rules for administrator-connected MCP servers. Applied after the
    /// <c>NewHeap:AI:Assistant:Mcp</c> configuration.
    /// </summary>
    public NhAssistantBuilder ConfigureMcp(Action<NhAssistantMcpOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        Services.PostConfigure<NhAssistantOptions>(options => configure(options.Mcp));
        return this;
    }

    /// <summary>
    /// Resolves MCP secret references with the prefix <paramref name="prefix"/>
    /// (<c>&lt;prefix&gt;:&lt;name&gt;</c>) through <typeparamref name="TResolver"/>, for example from a
    /// vault. The prefix is dash-case and <c>config</c> is reserved for the configuration section
    /// <see cref="NhAssistantMcpSecretReferences.ConfigurationSection"/>.
    /// </summary>
    public NhAssistantBuilder AddMcpSecretResolver<TResolver>(string prefix)
        where TResolver : class, INhAssistantMcpSecretResolver
    {
        if (!NhAssistantMcpSecretReferences.IsValidPrefix(prefix))
        {
            throw new ArgumentException("A secret reference prefix is dash-case, starts with a letter and has at most 32 characters.", nameof(prefix));
        }
        if (prefix == NhAssistantMcpSecretReferences.ConfigurationPrefix)
        {
            throw new ArgumentException("The prefix 'config' is reserved for the configuration section.", nameof(prefix));
        }
        if (Services.Any(descriptor => descriptor.ImplementationInstance is NhAssistantMcpSecretResolverRegistration registration
            && registration.Prefix == prefix))
        {
            throw new InvalidOperationException($"An MCP secret resolver for the prefix '{prefix}' is already registered.");
        }

        Services.TryAddSingleton<TResolver>();
        Services.AddSingleton(new NhAssistantMcpSecretResolverRegistration(prefix, typeof(TResolver)));
        return this;
    }

    /// <summary>
    /// Configures Web Push notifications. Applied after the <c>NewHeap:AI:Assistant:Push</c>
    /// configuration. Without VAPID keys push notifications stay off.
    /// </summary>
    public NhAssistantBuilder ConfigurePush(Action<NhAssistantPushOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        Services.PostConfigure<NhAssistantOptions>(options => configure(options.Push));
        return this;
    }

    /// <summary>
    /// Lets owners invite colleagues directly, next to sharing an invitation link. The directory
    /// decides who the caller may invite.
    /// </summary>
    public NhAssistantBuilder UseParticipantDirectory<TDirectory>()
        where TDirectory : class, INhAssistantParticipantDirectory
    {
        Services.Replace(ServiceDescriptor.Scoped<INhAssistantParticipantDirectory, TDirectory>());
        return this;
    }

    /// <summary>
    /// Replaces the claims-based display name other people see in a shared conversation.
    /// </summary>
    public NhAssistantBuilder UseDisplayNameResolver<TResolver>()
        where TResolver : class, INhAssistantDisplayNameResolver
    {
        Services.Replace(ServiceDescriptor.Scoped<INhAssistantDisplayNameResolver, TResolver>());
        return this;
    }

    /// <summary>
    /// Sets the execution region every agent model call must be permitted to run in. Defaults to
    /// <c>local</c>, the NewHeap agent default; the chat profile must permit the region.
    /// </summary>
    public NhAssistantBuilder UseExecutionRegion(string region)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(region);
        if (region.Length > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(region));
        }
        _state.ExecutionRegion = region;
        return this;
    }

    /// <summary>
    /// Adds a provider of situational facts (such as the user's name, roles or active division) for
    /// the "Situation" block of every turn. Several providers are allowed; they run in registration
    /// order. A failing provider is skipped for the turn.
    /// </summary>
    public NhAssistantBuilder UseTurnContextProvider<TProvider>()
        where TProvider : class, INhAssistantTurnContextProvider
    {
        Services.TryAddEnumerable(ServiceDescriptor.Scoped<INhAssistantTurnContextProvider, TProvider>());
        return this;
    }

    /// <summary>
    /// Sets the time zone of the date, weekday and time the library adds to every turn, such as
    /// <c>Europe/Amsterdam</c>. The default is UTC.
    /// </summary>
    public NhAssistantBuilder UseTimeZone(string timeZoneId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(timeZoneId);
        _state.TimeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        return this;
    }

    /// <summary>
    /// Configures how an agent runs on subjects: who answers for its runs, which tools it may use
    /// beyond a person's rights, how its proposals are approved and how runs are retried. The agent
    /// must be added with <see cref="AddAgent"/>.
    /// </summary>
    public NhAssistantBuilder ConfigureAgent(string agentId, Action<NhAssistantAgentRunBuilder> configure)
    {
        NhAssistantNames.ValidateSegment(agentId, nameof(agentId));
        ArgumentNullException.ThrowIfNull(configure);
        var options = _state.GetOrAddAgentRun(agentId);
        configure(new NhAssistantAgentRunBuilder(options));
        if (options.RuleHandlerType is { } ruleHandler)
        {
            Services.TryAddScoped(ruleHandler);
        }
        foreach (var skillHandler in options.Skills.Values.Select(skill => skill.RuleHandlerType).OfType<Type>())
        {
            Services.TryAddScoped(skillHandler);
        }

        return this;
    }

    /// <summary>
    /// Configures what every turn of a code agent starts with beyond its instructions, such as the
    /// application map. Unlike <see cref="ConfigureAgent"/> it does not make the agent run on its own,
    /// so it applies to chat agents as well as to agents with runs.
    /// </summary>
    public NhAssistantBuilder ConfigureAgentContext(string agentId, Action<NhAssistantAgentContextBuilder> configure)
    {
        NhAssistantNames.ValidateSegment(agentId, nameof(agentId));
        ArgumentNullException.ThrowIfNull(configure);
        configure(new NhAssistantAgentContextBuilder(_state.GetOrAddAgentContext(agentId)));
        return this;
    }

    /// <summary>
    /// Configures the whole application as a subject (<see cref="NhAiSubjectRef.SystemType"/>), which
    /// the platform registers itself. People who pass its read policy (by default the assistant's
    /// access policy) read its threads; people who pass its approve policy (by default the
    /// administration policy) also take part in them and decide their proposals. Startup fails when a
    /// policy is not registered.
    /// </summary>
    public NhAssistantBuilder UseSystemSubject(Action<NhAssistantSystemSubjectBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(new NhAssistantSystemSubjectBuilder(_state));
        return this;
    }

    /// <summary>
    /// Registers a group of agents, such as <c>project-management</c>, with its name per language and
    /// an optional description, for a page of the application that shows the group with the
    /// <c>nh-assistant-agent-board</c> component. Agents join it in code with
    /// <see cref="NhAssistantAgentRunBuilder.InGroup"/>; administrators add any agent to a group or
    /// remove it, and their choice wins over code. Groups exist only in code. The id is dash-case,
    /// at most 64 characters; a group needs a name. Registering the same group again, for example from
    /// every composition whose agents join it, keeps it; another group with the same id is refused.
    /// </summary>
    public NhAssistantBuilder AddAgentGroup(string groupId, Action<NhAssistantAgentGroupBuilder> configure)
    {
        if (!NhAssistantAgentGroupDefinition.IsValidId(groupId))
        {
            throw new ArgumentException(
                $"An agent group id is a dash-case identifier of at most {NhAssistantAgentGroupDefinition.MaxIdLength} characters.",
                nameof(groupId));
        }

        ArgumentNullException.ThrowIfNull(configure);
        var group = new NhAssistantAgentGroupDefinition(groupId);
        configure(new NhAssistantAgentGroupBuilder(group));
        group.Validate();
        _state.AddAgentGroup(group);
        return this;
    }

    /// <summary>
    /// Registers a named approval policy that agents, tools and triggers can reference.
    /// </summary>
    public NhAssistantBuilder AddApprovalPolicy(string policyId, Action<NhAiApprovalPolicyBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new NhAiApprovalPolicyBuilder(policyId);
        configure(builder);
        _state.AddApprovalPolicy(builder.Build());
        return this;
    }

    /// <summary>
    /// Registers the access resolver of one subject type. Agent threads of that type follow the
    /// resolver: it decides who sees a thread, who types in it and who decides its proposals. People
    /// see the type id; the overload with a configuration names the type and links to its subjects.
    /// </summary>
    public NhAssistantBuilder AddSubjectType<TResolver>()
        where TResolver : class, INhAiSubjectAccessResolver
    {
        _state.AddSubjectType(new NhAssistantSubjectTypeOptions(typeof(TResolver)));
        Services.TryAddEnumerable(ServiceDescriptor.Scoped<INhAiSubjectAccessResolver, TResolver>());
        return this;
    }

    /// <summary>
    /// Registers the access resolver of one subject type and describes the type for people: its
    /// name per language, the entity it stands for and where the application shows one subject. A
    /// resolver that also implements <see cref="INhAiSubjectSearch"/> lets people find subjects by
    /// name. Startup fails for a type starting with <c>nh-</c>, which the platform reserves, and for
    /// two resolvers of the same type.
    /// </summary>
    public NhAssistantBuilder AddSubjectType<TResolver>(Action<NhAssistantSubjectTypeBuilder> configure)
        where TResolver : class, INhAiSubjectAccessResolver
    {
        ArgumentNullException.ThrowIfNull(configure);
        var options = new NhAssistantSubjectTypeOptions(typeof(TResolver));
        configure(new NhAssistantSubjectTypeBuilder(options));
        options.Validate();
        _state.AddSubjectType(options);
        Services.TryAddEnumerable(ServiceDescriptor.Scoped<INhAiSubjectAccessResolver, TResolver>());
        return this;
    }

    /// <summary>
    /// Registers the briefing of a subject type: a bounded sketch of the subject that every model
    /// turn of an agent about it reads after its instructions. One provider per subject type; a
    /// later registration for the same type replaces an earlier one.
    /// </summary>
    public NhAssistantBuilder AddSubjectBriefing<TProvider>()
        where TProvider : class, INhAssistantSubjectBriefingProvider
    {
        Services.TryAddEnumerable(ServiceDescriptor.Scoped<INhAssistantSubjectBriefingProvider, TProvider>());
        return this;
    }

    /// <summary>
    /// Registers a trigger that starts an agent when the application publishes
    /// <typeparamref name="TEvent"/> through <c>INhAssistantAgentTriggers.PublishAsync</c>.
    /// </summary>
    public NhAssistantBuilder AddTrigger<TEvent>(string triggerId, Action<NhAssistantTriggerBuilder<TEvent>> configure)
        where TEvent : class
    {
        if (!NhAiSubjectRef.IsValidType(triggerId))
        {
            throw new ArgumentException("A trigger id is a dash-case identifier.", nameof(triggerId));
        }

        ArgumentNullException.ThrowIfNull(configure);
        var builder = new NhAssistantTriggerBuilder<TEvent>();
        configure(builder);
        _state.AddTrigger(builder.Build(triggerId));
        return this;
    }

    /// <summary>
    /// Registers a trigger that starts an agent on a schedule for every subject that
    /// <c>Subjects</c> returns, at most <c>MaxSubjectsPerTick</c> per tick. The schedule runs as a
    /// Hangfire recurring job, so the host needs <c>WithHangfire</c>.
    /// </summary>
    public NhAssistantBuilder AddScheduledTrigger(string triggerId, Action<NhAssistantScheduleBuilder> configure)
    {
        ValidateTriggerId(triggerId);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new NhAssistantScheduleBuilder();
        configure(builder);
        _state.AddTrigger(builder.Build(triggerId));
        return this;
    }

    /// <summary>
    /// Registers a trigger that an external system fires with
    /// <c>POST {prefix}/triggers/{triggerId}</c>, signed with a shared secret.
    /// </summary>
    public NhAssistantBuilder AddWebhookTrigger(string triggerId, Action<NhAssistantWebhookBuilder> configure)
    {
        ValidateTriggerId(triggerId);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new NhAssistantWebhookBuilder();
        configure(builder);
        _state.AddTrigger(builder.Build(triggerId));
        return this;
    }

    /// <summary>
    /// Registers an application event that data-defined triggers can start a skill of an agent on,
    /// under a name such as <c>project.task-completed</c>. Publish it with
    /// <c>INhAssistantAgentTriggers.PublishAsync</c>, as for a trigger in code; one event may serve
    /// both.
    /// </summary>
    public NhAssistantBuilder AddTriggerEvent<TEvent>(string name, Action<NhAssistantTriggerEventBuilder<TEvent>> configure)
        where TEvent : class
    {
        NhAssistantBlockNames.Validate(name, nameof(name));
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new NhAssistantTriggerEventBuilder<TEvent>();
        configure(builder);
        _state.AddTriggerEvent(builder.Build(name));
        return this;
    }

    /// <summary>
    /// Registers the subjects a data-defined schedule can start a skill for on every tick, under a
    /// name such as <c>active-projects</c>. The query runs in a scope of its own.
    /// </summary>
    public NhAssistantBuilder AddSubjectSet(
        string name,
        string description,
        Func<IServiceProvider, CancellationToken, IAsyncEnumerable<NhAiSubjectRef>> subjects)
    {
        NhAssistantBlockNames.Validate(name, nameof(name));
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentNullException.ThrowIfNull(subjects);
        _state.AddSubjectSet(new NhAssistantSubjectSetDefinition(name, description.Trim(), subjects));
        return this;
    }

    /// <summary>
    /// Registers a rule handler under a name, so a data-defined skill can work by its rules.
    /// </summary>
    public NhAssistantBuilder AddRuleHandler<THandler>(string name, string description)
        where THandler : class, INhAssistantAgentRuleHandler
    {
        NhAssistantBlockNames.Validate(name, nameof(name));
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        Services.TryAddScoped<THandler>();
        _state.AddRuleHandler(new NhAssistantRuleHandlerDefinition(name, description.Trim(), typeof(THandler)));
        return this;
    }

    /// <summary>
    /// Lists an authorization policy that administrators may choose as the run policy (who may run an
    /// agent for the whole application) or the view policy (who sees its results for the whole
    /// application) of an agent created in the administration, with what it means in at most 300
    /// characters. Agents in code name any policy with <c>RunPolicy</c> and <c>ViewPolicy</c>.
    /// Startup fails when the policy is not registered.
    /// </summary>
    public NhAssistantBuilder AddAgentPolicy(string policyName, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        if (policyName.Length > NhAssistantAgentWorksOn.MaxPolicyLength || policyName.Trim() != policyName)
        {
            throw new ArgumentException(
                $"A policy name has at most {NhAssistantAgentWorksOn.MaxPolicyLength} characters without surrounding whitespace.",
                nameof(policyName));
        }
        if (description.Length > NhAssistantSkillBuilder.MaxDescriptionLength)
        {
            throw new ArgumentOutOfRangeException(nameof(description));
        }

        _state.AddAgentPolicy(new NhAssistantAgentPolicyDefinition(policyName, description.Trim()));
        return this;
    }

    /// <summary>
    /// Registers an application guard for the proposals of the tools that
    /// <paramref name="toolSelectors"/> select (an exact tool id or a <c>prefix.*</c> selector). The
    /// guard names the objects a proposal changes and checks whether it still fits when the
    /// application revalidates open proposals, when a person approves and before execution. Several
    /// guards may apply to one tool; they run in registration order and the first stale result wins.
    /// </summary>
    public NhAssistantBuilder AddProposalGuard<TGuard>(params string[] toolSelectors)
        where TGuard : class, INhAiProposalGuard
    {
        return AddProposalGuard<TGuard>(toolSelectors, NhAiProposalGuardStages.All);
    }

    /// <summary>
    /// Registers an application guard that runs only at <paramref name="stages"/>. A guard without
    /// <see cref="NhAiProposalGuardStages.Revalidation"/> never closes open proposals by itself; the
    /// decision or the execution does.
    /// </summary>
    public NhAssistantBuilder AddProposalGuard<TGuard>(
        IReadOnlyCollection<string> toolSelectors,
        NhAiProposalGuardStages stages)
        where TGuard : class, INhAiProposalGuard
    {
        ArgumentNullException.ThrowIfNull(toolSelectors);
        if (toolSelectors.Count == 0 || toolSelectors.Any(selector => !NhAssistantAgentDefinition.IsValidSelector(selector)))
        {
            throw new ArgumentException(
                "A proposal guard needs one or more tool ids or prefix.* selectors.",
                nameof(toolSelectors));
        }
        if (stages == NhAiProposalGuardStages.None || (stages & ~NhAiProposalGuardStages.All) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stages));
        }

        Services.TryAddScoped<TGuard>();
        _state.AddProposalGuard(new Runs.NhAssistantProposalGuardRegistration(
            typeof(TGuard),
            toolSelectors.Distinct(StringComparer.Ordinal).ToArray(),
            stages));
        return this;
    }

    /// <summary>
    /// Revalidates open agent proposals whenever the application publishes
    /// <typeparamref name="TEvent"/> through <c>INhAssistantAgentTriggers.PublishAsync</c>: the
    /// proposals about the event's subject, those that change the event's objects, or both. Publish
    /// after the change committed, as for triggers.
    /// </summary>
    public NhAssistantBuilder AddProposalRevalidation<TEvent>(Action<NhAssistantRevalidationBuilder<TEvent>> configure)
        where TEvent : class
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new NhAssistantRevalidationBuilder<TEvent>();
        configure(builder);
        _state.AddRevalidation(builder.Build());
        return this;
    }

    /// <summary>
    /// Replaces who is told about agent events. The default notifies the subject's approvers.
    /// </summary>
    public NhAssistantBuilder UseAgentNotificationPolicy<TPolicy>()
        where TPolicy : class, INhAssistantAgentNotificationPolicy
    {
        Services.Replace(ServiceDescriptor.Scoped<INhAssistantAgentNotificationPolicy, TPolicy>());
        return this;
    }

    /// <summary>
    /// Adds a channel, such as e-mail or an in-app inbox, that delivers agent notifications next to
    /// Web Push.
    /// </summary>
    public NhAssistantBuilder AddAgentNotificationChannel<TChannel>()
        where TChannel : class, INhAssistantAgentNotificationChannel
    {
        Services.TryAddEnumerable(ServiceDescriptor.Scoped<INhAssistantAgentNotificationChannel, TChannel>());
        return this;
    }

    /// <summary>
    /// Also reserves the model and tool calls of assistant turns (a person's chat, agent runs,
    /// consultations and approved proposals that execute) with the budget manager the application
    /// registered before <c>AddNewHeapAssistant</c>, once the assistant's own daily budget accepted
    /// them. The request names the agent, skill, run and purpose (<see cref="NhAiBudgetPurposes"/>);
    /// a refusal ends the call as an exhausted budget. Off by default: the application's manager then
    /// sees only calls outside assistant turns.
    /// </summary>
    public NhAssistantBuilder UseApplicationBudgetInTurns()
    {
        _state.ApplicationBudgetInTurns = true;
        return this;
    }

    /// <summary>
    /// Also delivers agent notifications as in-app messages through the platform's
    /// <see cref="NewHeap.Platform.Common.Actors.INhUserNotificationSink"/>, such as NewHeap user
    /// notifications. Off by default, so existing hosts get no new messages.
    /// </summary>
    public NhAssistantBuilder UseInAppAgentNotifications()
    {
        return AddAgentNotificationChannel<Notifications.NhAssistantInAppAgentNotificationChannel>();
    }

    /// <summary>
    /// Adds a sink that receives every feedback people give on closed agent proposals, for example
    /// to forward it to the application's analytics. Several sinks may be added.
    /// </summary>
    public NhAssistantBuilder AddProposalFeedbackSink<TSink>()
        where TSink : class, INhAssistantProposalFeedbackSink
    {
        Services.TryAddEnumerable(ServiceDescriptor.Scoped<INhAssistantProposalFeedbackSink, TSink>());
        return this;
    }

    /// <summary>
    /// Converts stored working memory when an agent's memory schema version changes. Without a
    /// migrator, memory that no longer fits the new schema is cleared.
    /// </summary>
    public NhAssistantBuilder UseAgentMemoryMigrator<TMigrator>()
        where TMigrator : class, INhAssistantAgentMemoryMigrator
    {
        Services.Replace(ServiceDescriptor.Scoped<INhAssistantAgentMemoryMigrator, TMigrator>());
        return this;
    }

    private static void ValidateTriggerId(string triggerId)
    {
        if (!NhAiSubjectRef.IsValidType(triggerId))
        {
            throw new ArgumentException("A trigger id is a dash-case identifier.", nameof(triggerId));
        }
    }

    internal NhAssistantBuilder UseStorage(NhAssistantStorageRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        NhAssistantStorage.Register(Services, registration);
        _state.StorageProvider = registration.ProviderName;
        return this;
    }
}

/// <summary>
/// Configures the whole application as a subject; see <see cref="NhAssistantBuilder.UseSystemSubject"/>.
/// </summary>
public sealed class NhAssistantSystemSubjectBuilder
{
    private readonly NhAssistantRegistrationState _state;

    internal NhAssistantSystemSubjectBuilder(NhAssistantRegistrationState state)
    {
        _state = state;
    }

    /// <summary>
    /// The authorization policy that makes a person a reader of the whole application: who sees its
    /// threads, runs and proposals. The default is the assistant's access policy. Name an app-wide
    /// policy here when the access policy needs an active division, because the whole application
    /// app-wide belongs to no division; in a division it is evaluated for that division. An agent's
    /// own view policy applies on top.
    /// </summary>
    public NhAssistantSystemSubjectBuilder ReadPolicy(string policyName)
    {
        _state.SystemReadPolicy = Validate(policyName);
        return this;
    }

    /// <summary>
    /// The authorization policy that makes a person a participant and approver of the whole
    /// application: who types in its threads and decides their proposals. The default is the
    /// assistant's administration policy. A participant reads as well; an agent's own run policy
    /// applies on top.
    /// </summary>
    public NhAssistantSystemSubjectBuilder ApprovePolicy(string policyName)
    {
        _state.SystemApprovePolicy = Validate(policyName);
        return this;
    }

    private static string Validate(string policyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);
        if (policyName.Length > NhAssistantAgentWorksOn.MaxPolicyLength || policyName.Trim() != policyName)
        {
            throw new ArgumentException(
                $"A policy name has at most {NhAssistantAgentWorksOn.MaxPolicyLength} characters without surrounding whitespace.",
                nameof(policyName));
        }

        return policyName;
    }
}

/// <summary>
/// Registration state shared by every <c>AddNewHeapAssistant</c> call in one service collection.
/// </summary>
internal sealed class NhAssistantRegistrationState
{
    private readonly Dictionary<string, NhAssistantAgentDefinition> _agents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NhAssistantAgentRunOptions> _agentRuns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NhAssistantAgentContextOptions> _agentContexts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NhAiApprovalPolicy> _approvalPolicies = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NhAssistantTriggerDefinition> _triggers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NhAssistantTriggerEventDefinition> _triggerEvents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NhAssistantSubjectSetDefinition> _subjectSets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NhAssistantRuleHandlerDefinition> _ruleHandlers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NhAssistantAgentPolicyDefinition> _agentPolicies = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NhAssistantAgentGroupDefinition> _agentGroups = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, NhAssistantSubjectTypeOptions> _subjectTypes = [];
    private NhAssistantRunSnapshot? _snapshot;
    private readonly List<Runs.NhAssistantProposalGuardRegistration> _proposalGuards = [];
    private readonly List<NhAssistantRevalidationDefinition> _revalidations = [];

    public string? AccessPolicy { get; set; }

    public string? ChatProfileName { get; set; }

    public string? StorageProvider { get; set; }

    public string ExecutionRegion { get; set; } = "local";

    public string? AdminPolicy { get; set; }

    /// <summary>
    /// The policy that makes a person a reader of the whole application; <see langword="null"/> uses
    /// the access policy.
    /// </summary>
    public string? SystemReadPolicy { get; set; }

    /// <summary>
    /// The policy that makes a person a participant and approver of the whole application;
    /// <see langword="null"/> uses the administration policy.
    /// </summary>
    public string? SystemApprovePolicy { get; set; }

    public NhAiTextAsset? DefaultApplicationContext { get; set; }

    public NhAssistantLimits Limits { get; set; } = new();

    /// <summary>
    /// Reservations inside assistant turns also go to the application's budget manager; see
    /// <see cref="NhAssistantBuilder.UseApplicationBudgetInTurns"/>.
    /// </summary>
    public bool ApplicationBudgetInTurns { get; set; }

    public TimeZoneInfo TimeZone { get; set; } = TimeZoneInfo.Utc;

    public IReadOnlyCollection<NhAssistantAgentDefinition> Agents => _agents.Values;

    public bool TryGetAgent(string agentId, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out NhAssistantAgentDefinition? agent)
    {
        return _agents.TryGetValue(agentId, out agent);
    }

    /// <summary>
    /// The run configuration in effect: the code configuration with the data-defined skills and
    /// triggers of the last loaded snapshot.
    /// </summary>
    public IReadOnlyDictionary<string, NhAssistantAgentRunOptions> AgentRuns => Volatile.Read(ref _snapshot)?.AgentRuns ?? _agentRuns;

    /// <summary>
    /// The run configuration of code only, as composition registered it.
    /// </summary>
    public IReadOnlyDictionary<string, NhAssistantAgentRunOptions> CodeAgentRuns => _agentRuns;

    /// <summary>
    /// The snapshot in effect, or <see langword="null"/> before data was loaded.
    /// </summary>
    public NhAssistantRunSnapshot? Snapshot => Volatile.Read(ref _snapshot);

    public IReadOnlyDictionary<string, NhAssistantTriggerEventDefinition> TriggerEvents => _triggerEvents;

    /// <summary>
    /// The host runs agents whose run settings are data, as the ASP.NET integration does; without it
    /// only a host with agent runs in code loads the run configuration defined as data.
    /// </summary>
    public bool DataAgentRunsEnabled { get; set; }

    public IReadOnlyDictionary<string, NhAssistantSubjectSetDefinition> SubjectSets => _subjectSets;

    public IReadOnlyDictionary<string, NhAssistantRuleHandlerDefinition> RuleHandlers => _ruleHandlers;

    /// <summary>
    /// The policies administrators may choose as run and view policies of agents created in the
    /// administration, by name.
    /// </summary>
    public IReadOnlyDictionary<string, NhAssistantAgentPolicyDefinition> AgentPolicies => _agentPolicies;

    /// <summary>
    /// Lists a policy once; the same policy with another description is refused.
    /// </summary>
    public void AddAgentPolicy(NhAssistantAgentPolicyDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (_agentPolicies.TryGetValue(definition.Name, out var existing))
        {
            if (!string.Equals(existing.Description, definition.Description, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Agent policy '{definition.Name}' is already listed with another description.");
            }
            return;
        }

        _agentPolicies.Add(definition.Name, definition);
    }

    /// <summary>
    /// The groups of agents in code, by id.
    /// </summary>
    public IReadOnlyDictionary<string, NhAssistantAgentGroupDefinition> AgentGroups => _agentGroups;

    /// <summary>
    /// Registers a group once: the same group registered again is kept, another group with the same
    /// id is refused.
    /// </summary>
    public void AddAgentGroup(NhAssistantAgentGroupDefinition group)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (_agentGroups.TryGetValue(group.Id, out var existing))
        {
            if (!existing.IsEquivalentTo(group))
            {
                throw new InvalidOperationException($"Agent group '{group.Id}' is already registered with other names or descriptions.");
            }
            return;
        }
        if (_agentGroups.Count >= NhAssistantAgentGroupDefinition.MaxGroups)
        {
            throw new InvalidOperationException(
                $"An application registers at most {NhAssistantAgentGroupDefinition.MaxGroups} agent groups.");
        }

        _agentGroups.Add(group.Id, group);
    }

    /// <summary>
    /// The subject type configurations by resolver class, as <c>AddSubjectType</c> registered them.
    /// </summary>
    public IReadOnlyDictionary<Type, NhAssistantSubjectTypeOptions> SubjectTypes => _subjectTypes;

    /// <summary>
    /// Records the configuration of a resolver class. A repeated registration without configuration
    /// keeps the configuration; two different configurations of one resolver are refused.
    /// </summary>
    public void AddSubjectType(NhAssistantSubjectTypeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!_subjectTypes.TryGetValue(options.ResolverType, out var existing) || !existing.IsConfigured)
        {
            _subjectTypes[options.ResolverType] = options;
            return;
        }
        if (!options.IsConfigured || existing.IsEquivalentTo(options))
        {
            return;
        }

        throw new InvalidOperationException(
            $"The subject type of {options.ResolverType.Name} is already configured differently.");
    }

    /// <summary>
    /// Puts a snapshot of code and data into effect for every reader at once.
    /// </summary>
    public void ApplySnapshot(NhAssistantRunSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Volatile.Write(ref _snapshot, snapshot);
    }

    public void AddTriggerEvent(NhAssistantTriggerEventDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (_triggerEvents.TryGetValue(definition.Name, out var existing))
        {
            if (existing.EventType != definition.EventType)
            {
                throw new InvalidOperationException($"Trigger event '{definition.Name}' is already registered for another event type.");
            }
            return;
        }

        _triggerEvents.Add(definition.Name, definition);
    }

    public void AddSubjectSet(NhAssistantSubjectSetDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!_subjectSets.TryAdd(definition.Name, definition))
        {
            throw new InvalidOperationException($"Subject set '{definition.Name}' is already registered.");
        }
    }

    public void AddRuleHandler(NhAssistantRuleHandlerDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (_ruleHandlers.TryGetValue(definition.Name, out var existing))
        {
            if (existing.HandlerType != definition.HandlerType)
            {
                throw new InvalidOperationException($"Rule handler '{definition.Name}' is already registered for another type.");
            }
            return;
        }

        _ruleHandlers.Add(definition.Name, definition);
    }

    public IReadOnlyDictionary<string, NhAssistantAgentContextOptions> AgentContexts => _agentContexts;

    public NhAssistantAgentContextOptions GetOrAddAgentContext(string agentId)
    {
        if (!_agentContexts.TryGetValue(agentId, out var options))
        {
            options = new NhAssistantAgentContextOptions(agentId);
            _agentContexts.Add(agentId, options);
        }

        return options;
    }

    public IReadOnlyDictionary<string, NhAiApprovalPolicy> ApprovalPolicies => _approvalPolicies;

    /// <summary>
    /// The triggers in effect: those in code and the data-defined ones of the last loaded snapshot.
    /// </summary>
    public IReadOnlyDictionary<string, NhAssistantTriggerDefinition> Triggers => Volatile.Read(ref _snapshot)?.Triggers ?? _triggers;

    /// <summary>
    /// The triggers registered in code.
    /// </summary>
    public IReadOnlyDictionary<string, NhAssistantTriggerDefinition> CodeTriggers => _triggers;

    /// <summary>
    /// Proposal guards in registration order.
    /// </summary>
    public IReadOnlyList<Runs.NhAssistantProposalGuardRegistration> ProposalGuards => _proposalGuards;

    public IReadOnlyList<NhAssistantRevalidationDefinition> Revalidations => _revalidations;

    public void AddProposalGuard(Runs.NhAssistantProposalGuardRegistration guard)
    {
        ArgumentNullException.ThrowIfNull(guard);
        if (_proposalGuards.Any(existing => existing.GuardType == guard.GuardType
                && existing.Stages == guard.Stages
                && existing.ToolSelectors.SequenceEqual(guard.ToolSelectors, StringComparer.Ordinal)))
        {
            // AddNewHeapAssistant may run more than once for the same service collection.
            return;
        }

        _proposalGuards.Add(guard);
    }

    public void AddRevalidation(NhAssistantRevalidationDefinition revalidation)
    {
        ArgumentNullException.ThrowIfNull(revalidation);
        _revalidations.Add(revalidation);
    }

    /// <summary>
    /// The run configuration in effect for an agent, or an empty one for an agent without; never
    /// changes the registration, so it is safe at runtime.
    /// </summary>
    public NhAssistantAgentRunOptions RunOptionsOf(string agentId)
    {
        return AgentRuns.TryGetValue(agentId, out var options) ? options : new NhAssistantAgentRunOptions(agentId);
    }

    public NhAssistantAgentRunOptions GetOrAddAgentRun(string agentId)
    {
        if (!_agentRuns.TryGetValue(agentId, out var options))
        {
            options = new NhAssistantAgentRunOptions(agentId);
            _agentRuns.Add(agentId, options);
        }

        return options;
    }

    public void AddApprovalPolicy(NhAiApprovalPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var validation = policy.Validate(NhAssistantLimits.MaxApprovalLifetime);
        if (!validation.Success)
        {
            throw new ArgumentException(
                $"Approval policy '{policy.Id}' is invalid: {string.Join("; ", validation.AllErrorMessages)}",
                nameof(policy));
        }

        if (!_approvalPolicies.TryAdd(policy.Id, policy))
        {
            throw new InvalidOperationException($"Approval policy '{policy.Id}' is already registered.");
        }
    }

    public void AddTrigger(NhAssistantTriggerDefinition trigger)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        if (NhAssistantTriggerIds.IsReserved(trigger.Id))
        {
            throw new ArgumentException($"Trigger id '{trigger.Id}' is reserved.", nameof(trigger));
        }

        if (!_triggers.TryAdd(trigger.Id, trigger))
        {
            throw new InvalidOperationException($"Trigger '{trigger.Id}' is already registered.");
        }
    }

    /// <summary>
    /// Checks the agent run configuration once every registration has run: configured agents and
    /// triggers reference known agents, and every referenced policy is registered.
    /// </summary>
    public void ValidateAgentRuns()
    {
        foreach (var (agentId, options) in _agentRuns)
        {
            if (!_agents.ContainsKey(agentId))
            {
                throw new InvalidOperationException(
                    $"ConfigureAgent references agent '{agentId}', which is not added with AddAgent.");
            }

            var policies = options.ToolApprovalPolicies.Values.Append(options.ApprovalPolicyId);
            foreach (var policyId in policies.OfType<string>())
            {
                ValidatePolicyReference(policyId, $"agent '{agentId}'");
            }

            foreach (var groupId in options.Groups.Where(groupId => !_agentGroups.ContainsKey(groupId)))
            {
                throw new InvalidOperationException(
                    $"Agent '{agentId}' joins group '{groupId}', which is not registered. Register it with AddAgentGroup.");
            }

            var agent = _agents[agentId];
            if (options.ShadowOfAgentId is { } liveAgentId)
            {
                if (!options.Shadow)
                {
                    throw new InvalidOperationException(
                        $"Agent '{agentId}' is a shadow variant of '{liveAgentId}' and must stay in shadow mode.");
                }
                if (!_agentRuns.TryGetValue(liveAgentId, out var live))
                {
                    throw new InvalidOperationException(
                        $"Agent '{agentId}' is a shadow variant of '{liveAgentId}', which is not configured with ConfigureAgent.");
                }
                if (live.Shadow || live.ShadowOfAgentId is not null)
                {
                    throw new InvalidOperationException(
                        $"Agent '{agentId}' is a shadow variant of '{liveAgentId}', which is not a live agent.");
                }
                if (options.Groups.Count > 0)
                {
                    throw new InvalidOperationException(
                        $"Agent '{agentId}' is a shadow variant; boards show live agents only, so it joins no group.");
                }
            }

            foreach (var handOff in options.HandOffs)
            {
                foreach (var source in handOff.Sources.Where(source => !_agentRuns.ContainsKey(source)))
                {
                    throw new InvalidOperationException(
                        $"Agent '{agentId}' accepts hand-offs from '{source}', which is not configured with ConfigureAgent.");
                }
                if (!options.WorksOn.Covers(handOff.SubjectType))
                {
                    throw new InvalidOperationException(
                        $"Agent '{agentId}' accepts hand-offs about '{handOff.SubjectType}', which it does not work on. Add the type to WorksOn.");
                }
            }

            foreach (var consultation in options.Consultations.Where(consultation => !options.WorksOn.Covers(consultation.SubjectType)))
            {
                throw new InvalidOperationException(
                    $"Agent '{agentId}' allows consultation about '{consultation.SubjectType}', which it does not work on. Add the type to WorksOn.");
            }

            foreach (var skill in options.Skills.Values)
            {
                if (skill.ApprovalPolicyId is { } skillPolicy)
                {
                    ValidatePolicyReference(skillPolicy, $"skill '{skill.Id}' of agent '{agentId}'");
                }
                foreach (var selector in skill.ToolSelectors)
                {
                    if (!agent.ToolSelectors.Any(agentSelector => Covers(agentSelector, selector)))
                    {
                        throw new InvalidOperationException(
                            $"Skill '{skill.Id}' of agent '{agentId}' selects '{selector}', which the agent's own tool selectors do not cover.");
                    }
                }
            }
        }

        foreach (var agentId in _agentContexts.Keys.Where(agentId => !_agents.ContainsKey(agentId)))
        {
            throw new InvalidOperationException(
                $"ConfigureAgentContext references agent '{agentId}', which is not added with AddAgent.");
        }

        foreach (var trigger in _triggers.Values)
        {
            if (!_agentRuns.ContainsKey(trigger.AgentId))
            {
                throw new InvalidOperationException(
                    $"Trigger '{trigger.Id}' starts agent '{trigger.AgentId}', which is not configured with ConfigureAgent.");
            }

            if (trigger.ApprovalPolicyId is not null)
            {
                ValidatePolicyReference(trigger.ApprovalPolicyId, $"trigger '{trigger.Id}'");
            }
            if (trigger.SkillId is { } skillId && !_agentRuns[trigger.AgentId].Skills.ContainsKey(skillId))
            {
                throw new InvalidOperationException(
                    $"Trigger '{trigger.Id}' runs skill '{skillId}', which agent '{trigger.AgentId}' does not declare.");
            }
            // A trigger that names its subject type up front is checked now; others when they fire.
            if (trigger.IsSystemSchedule && !_agentRuns[trigger.AgentId].WorksOn.Application)
            {
                throw new InvalidOperationException(
                    $"Scheduled trigger '{trigger.Id}' has no subjects, so it runs for the whole application, which agent '{trigger.AgentId}' does not work on. Add WorksOnApplication or name the subjects with Subjects.");
            }
            if (trigger.SubjectType is { } subjectType && !_agentRuns[trigger.AgentId].WorksOn.Covers(subjectType))
            {
                throw new InvalidOperationException(
                    $"Trigger '{trigger.Id}' starts agent '{trigger.AgentId}' on '{subjectType}', which the agent does not work on. Add the type to WorksOn.");
            }
        }

        foreach (var policy in _approvalPolicies.Values.Where(policy => policy.EscalationPolicyId is not null))
        {
            ValidatePolicyReference(policy.EscalationPolicyId!, $"approval policy '{policy.Id}'");
        }
    }

    /// <summary>
    /// Whether the agent's selector covers a skill's selector: the same tool, or a prefix selector
    /// that includes it.
    /// </summary>
    internal static bool Covers(string agentSelector, string skillSelector)
    {
        if (string.Equals(agentSelector, skillSelector, StringComparison.Ordinal))
        {
            return true;
        }

        return agentSelector.EndsWith(".*", StringComparison.Ordinal)
            && skillSelector.StartsWith(agentSelector[..^1], StringComparison.Ordinal);
    }

    private void ValidatePolicyReference(string policyId, string owner)
    {
        if (!_approvalPolicies.ContainsKey(policyId))
        {
            throw new InvalidOperationException(
                $"The {owner} references approval policy '{policyId}', which is not registered with AddApprovalPolicy.");
        }
    }

    public void AddAgent(NhAssistantAgentDefinition agent)
    {
        NhAssistantAgentDefinition.ValidateShape(agent);
        if (_agents.TryGetValue(agent.Id, out var existing))
        {
            if (existing.IsEquivalentTo(agent))
            {
                return;
            }
            throw new InvalidOperationException(
                $"Assistant agent '{agent.Id}' is already registered with a different definition.");
        }
        _agents.Add(agent.Id, agent);
    }
}
