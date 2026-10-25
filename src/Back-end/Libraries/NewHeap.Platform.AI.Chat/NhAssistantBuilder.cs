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
    /// policy is not registered. With <see cref="NhAssistantSystemSubjectBuilder.Members{TMembers}"/> the
    /// application lists its people, so a thread nobody opened still reaches its approvers.
    /// </summary>
    public NhAssistantBuilder UseSystemSubject(Action<NhAssistantSystemSubjectBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(new NhAssistantSystemSubjectBuilder(_state, Services));
        return this;
    }

    /// <summary>
    /// Declares that every person of this host belongs to one tenant, <paramref name="tenantId"/>, so
    /// people of that tenant take part in the work of the whole application
    /// (<see cref="NhAiSubjectRef.SystemType"/>): its approval inbox, deciding its proposals, boards,
    /// live watches, its threads, run history and subject work, and running agents for it by hand.
    /// Without it, and for a caller of any other tenant, only callers without a tenant do. The whole
    /// application's threads, runs and proposals stay stored without a tenant, so switching the mode off
    /// leaves the data valid. The policies of <see cref="UseSystemSubject"/> and the agent's run and view
    /// policies still decide what such a person sees and decides. Startup fails when the AI ASP.NET
    /// integration projects another tenant or a tenant claim, which can carry more than one; a resolver
    /// of the application's own (<c>UseAuthenticatedInvocationContextResolver</c>) cannot be checked and
    /// must return this tenant or none. The mode is never derived from the projection. Calling it again
    /// with the same tenant keeps it; another tenant is refused.
    /// </summary>
    public NhAssistantBuilder UseSingleTenant(string tenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        if (tenantId.Length > NhAssistantRegistrationState.MaxTenantIdLength || tenantId.Trim() != tenantId)
        {
            throw new ArgumentException(
                $"A tenant id has at most {NhAssistantRegistrationState.MaxTenantIdLength} characters without surrounding whitespace.",
                nameof(tenantId));
        }
        if (_state.SingleTenantId is { } existing && !string.Equals(existing, tenantId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "UseSingleTenant was already called with another tenant. A host serves one tenant.");
        }

        _state.SingleTenantId = tenantId;
        return this;
    }

    /// <summary>
    /// Registers a group of agents, such as <c>project-management</c>, with its name per language and
    /// an optional description, for a page of the application that shows the group with the
    /// <c>nh-assistant-agent-board</c> component. Agents join it in code with
    /// <see cref="NhAssistantAgentRunBuilder.InGroup"/>; administrators add any agent to a group or
    /// remove it, and their choice wins over code. Groups exist only in code. The id is dash-case,
    /// at most 64 characters; ids that start with <c>nh-</c> belong to the platform, which records the
    /// approval inbox's visits under <c>nh-inbox</c>. A group needs a name. Registering the same group
    /// again, for example from every composition whose agents join it, keeps it; another group with
    /// the same id is refused.
    /// </summary>
    public NhAssistantBuilder AddAgentGroup(string groupId, Action<NhAssistantAgentGroupBuilder> configure)
    {
        if (!NhAssistantAgentGroupDefinition.IsValidId(groupId))
        {
            throw new ArgumentException(
                $"An agent group id is a dash-case identifier of at most {NhAssistantAgentGroupDefinition.MaxIdLength} characters.",
                nameof(groupId));
        }
        if (NhAssistantAgentGroupDefinition.IsReservedId(groupId))
        {
            throw new ArgumentException(
                $"Agent group ids starting with '{NhAssistantAgentGroupDefinition.ReservedIdPrefix}' are reserved for the platform. Rename the group.",
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
    /// Registers a named entry of agent metadata: the application's own typed data about an agent, such
    /// as a cost center or a team, which NewHeap stores per agent and name, checks on every save as
    /// startup checks the values in code, and never interprets; it never widens what an agent may do.
    /// <typeparamref name="TValue"/> is the value's JSON shape (camelCase). Agents in code set a value
    /// in <see cref="ConfigureAgent"/> with <see cref="NhAssistantAgentRunBuilder.Metadata{TValue}"/>,
    /// which administrators may override only when the entry is
    /// <see cref="NhAssistantAgentMetadataBuilder{TValue}.Editable"/>; agents created at runtime get
    /// values through <c>POST</c> and <c>PUT admin/agents</c>, <c>PUT admin/agents/{id}/metadata/{name}</c>
    /// and <c>INhAssistantAgentConfiguration</c>, versioned with the agent. Read them with
    /// <see cref="INhAssistantAgentMetadata"/>. The name is dash-case, at most 64 characters, outside
    /// the platform's <c>nh-</c> prefix; registering a name twice is refused.
    /// </summary>
    public NhAssistantBuilder AddAgentMetadata<TValue>(
        string name,
        Action<NhAssistantAgentMetadataBuilder<TValue>>? configure = null)
        where TValue : notnull
    {
        NhAssistantAgentMetadataEntry.ValidateName(name, nameof(name));
        if (name.StartsWith(NhAssistantAgentMetadataLimits.ReservedNamePrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Agent metadata names starting with '{NhAssistantAgentMetadataLimits.ReservedNamePrefix}' are reserved for the platform. Rename the entry.",
                nameof(name));
        }

        var entry = new NhAssistantAgentMetadataEntry(name, typeof(TValue));
        configure?.Invoke(new NhAssistantAgentMetadataBuilder<TValue>(entry));
        _state.AddAgentMetadata(entry);
        return this;
    }

    /// <summary>
    /// Lets administrators define consultations of agents as data, within this ceiling: which agent
    /// may be consulted about which object type, with which description and instructions, by whom,
    /// read-only or with proposals by the routes the ceiling names, and within its limits. Agents
    /// created in the administration with run settings get this ceiling; an agent in code gets
    /// consultations as data only with <see cref="NhAssistantAgentRunBuilder.ConsultableAsData"/>. Data
    /// never changes a consultation in code. The asking side stays the asking agent's tool selectors.
    /// Without this call consultations exist in code only.
    /// </summary>
    public NhAssistantBuilder UseConsultationsAsData(Action<NhAssistantConsultationCeilingBuilder>? configure = null)
    {
        var ceiling = new NhAssistantConsultationCeiling();
        configure?.Invoke(new NhAssistantConsultationCeilingBuilder(ceiling));
        _state.ConsultationsAsData = ceiling;
        return this;
    }

    /// <summary>
    /// Lets administrators define hand-offs between agents as data, within this ceiling: per subject
    /// type which agents may accept hand-offs as data (<c>To</c>, or every agent created in the
    /// administration) and from which agents (<c>From</c>, likewise), and the highest daily limit of
    /// one hand-off. A hand-off defined as data works as one in code (<c>AcceptHandOffs</c>): the same
    /// generated tool <c>assistant.handoff.{agentId}.{subjectType}</c>, depth, scope key, daily limit
    /// and shadow rules. It is checked as startup checks code on every save and every load; one that no
    /// longer fits the ceiling or the code is out of effect with a problem the administration shows.
    /// Data never changes a hand-off in code. Without this call hand-offs exist in code only.
    /// </summary>
    public NhAssistantBuilder UseHandOffsAsData(Action<NhAssistantHandOffCeilingBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var ceiling = new NhAssistantHandOffCeiling();
        configure(new NhAssistantHandOffCeilingBuilder(ceiling));
        if (ceiling.Rules.Count == 0)
        {
            throw new InvalidOperationException("UseHandOffsAsData allows no hand-off; name them with Allow.");
        }

        _state.HandOffsAsData = ceiling;
        return this;
    }

    /// <summary>
    /// Lets administrators create shadow variants of live agents as data, within this ceiling: which
    /// live agents may get them (<c>Of</c>, or every agent created in the administration) and how many
    /// each. A data variant is an agent created in the administration that names its live agent when it
    /// is created and never changes it: every trigger of the live agent also fires for it, it always
    /// works in shadow in threads only administrators see, notifies nobody, executes nothing and is
    /// compared with the live agent in the evaluation, as a variant in code (<c>ShadowOf</c>) is. One
    /// that no longer fits the ceiling or the code is out of effect with a problem. Without this call
    /// shadow variants exist in code only.
    /// </summary>
    public NhAssistantBuilder UseShadowVariantsAsData(Action<NhAssistantShadowVariantCeilingBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var ceiling = new NhAssistantShadowVariantCeiling();
        configure(new NhAssistantShadowVariantCeilingBuilder(ceiling));
        if (ceiling.LiveAgents.Count == 0 && !ceiling.OfAgentsCreatedInAdministration)
        {
            throw new InvalidOperationException(
                "UseShadowVariantsAsData allows no live agent; name them with Of or OfAgentsCreatedInAdministration.");
        }

        _state.ShadowVariantsAsData = ceiling;
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
    /// decision or the execution does. A guard with <see cref="NhAiProposalGuardStages.Proposal"/> also
    /// checks a proposal before it is recorded, so work that is already done or no longer applies is
    /// never proposed; pass it with the other stages, such as
    /// <c>NhAiProposalGuardStages.All | NhAiProposalGuardStages.Proposal</c>.
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
        if (stages == NhAiProposalGuardStages.None
            || (stages & ~(NhAiProposalGuardStages.All | NhAiProposalGuardStages.Proposal)) != 0)
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
    /// Declares that the application executes the approved deferred proposals of tool
    /// <paramref name="toolId"/> itself, outside the tool's function, and reports their outcome within
    /// <paramref name="reportWithin"/>. Once such a proposal is approved, by any path, the assistant hands
    /// it to the application's <see cref="INhAssistantProposalExecutor"/>
    /// (<see cref="UseProposalExecutor{TExecutor}"/>, which startup requires) instead of running the tool,
    /// and the proposal waits as <see cref="NhAssistantApprovalStatuses.AwaitingApplication"/> until the
    /// application reports through <see cref="INhAssistantProposalExecutionReports"/>. Without a report
    /// before the deadline, on the application's clock, the run maintenance makes it
    /// <see cref="NhAssistantApprovalStatuses.ExecutionUnknown"/> and tells the approvers, who hand it to
    /// the application again or mark it resolved.
    /// <para>
    /// The declaration is opt-in per tool: other tools keep executing in their function. A run or a
    /// consultation always leaves a proposal of such a tool for later (deferred), at its subject, because
    /// the application's result never reaches a waiting turn; only a person's own turn, a chat or a request
    /// in an agent thread, still approves it inline and runs the tool's function, unless the declaration
    /// says <see cref="NhAssistantApplicationExecutionBuilder.AlsoInline"/>
    /// (<see cref="ExecuteInApplication(string, TimeSpan, Action{NhAssistantApplicationExecutionBuilder})"/>).
    /// </para>
    /// </summary>
    /// <param name="toolId">The tool's exact id, such as <c>projects.order-hardware</c>; no selector.</param>
    /// <param name="reportWithin">How long the application may take to report after a hand-off: longer
    /// than zero and at most 90 days.</param>
    public NhAssistantBuilder ExecuteInApplication(string toolId, TimeSpan reportWithin)
    {
        return ExecuteInApplication(toolId, reportWithin, _ => { });
    }

    /// <summary>
    /// Declares that the application executes the approved proposals of tool <paramref name="toolId"/>
    /// itself, as <see cref="ExecuteInApplication(string, TimeSpan)"/> does, with the options of
    /// <paramref name="configure"/>, such as <see cref="NhAssistantApplicationExecutionBuilder.AlsoInline"/>,
    /// which hands a person's inline approval in their own conversation to the application too.
    /// </summary>
    /// <param name="toolId">The tool's exact id, such as <c>projects.order-hardware</c>; no selector.</param>
    /// <param name="reportWithin">How long the application may take to report after a hand-off: longer
    /// than zero and at most 90 days.</param>
    /// <param name="configure">The declaration's options.</param>
    public NhAssistantBuilder ExecuteInApplication(
        string toolId,
        TimeSpan reportWithin,
        Action<NhAssistantApplicationExecutionBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        if (!NhAssistantAgentDefinition.IsValidSelector(toolId) || toolId.Contains('*', StringComparison.Ordinal))
        {
            throw new ArgumentException("The application executes a tool named by its exact id, without a selector.", nameof(toolId));
        }
        if (reportWithin <= TimeSpan.Zero || reportWithin > NhAssistantApplicationExecution.MaxReportWithin)
        {
            throw new ArgumentOutOfRangeException(
                nameof(reportWithin),
                reportWithin,
                "The report deadline of a tool the application executes is longer than zero and at most 90 days.");
        }

        var options = new NhAssistantApplicationExecutionBuilder();
        configure(options);
        _state.AddApplicationExecution(new NhAssistantApplicationExecution(toolId, reportWithin)
        {
            AlsoInline = options.HandsOffInline
        });
        return this;
    }

    /// <summary>
    /// Registers the application's executor of the approved proposals of the tools declared with
    /// <see cref="ExecuteInApplication"/>. A host that declares such a tool without an executor does not
    /// start.
    /// </summary>
    public NhAssistantBuilder UseProposalExecutor<TExecutor>()
        where TExecutor : class, INhAssistantProposalExecutor
    {
        Services.Replace(ServiceDescriptor.Scoped<INhAssistantProposalExecutor, TExecutor>());
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
    /// notifications, and so does an application message that asks to notify
    /// (<see cref="NhAssistantApplicationMessage.Notify"/>), in chats and agent threads alike. Off by
    /// default, so existing hosts get no new messages.
    /// </summary>
    public NhAssistantBuilder UseInAppAgentNotifications()
    {
        _state.InAppNotifications = true;
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

    /// <summary>
    /// Lets turns read the application's files: a tool result that names files and, in a chat, a
    /// message with attachments. The source opens each file for the actor of the turn, so file
    /// access follows the application's authorization, and reports its classification. Without it
    /// turns take no files. The chat assistant accepts files at once; an agent configured with
    /// <see cref="ConfigureAgent"/> and its skills accept them only after <c>AcceptsFiles()</c> or the
    /// switch in the administration. <see cref="NhAssistantLimits"/> bounds every file and turn.
    /// </summary>
    public NhAssistantBuilder UseFileSource<TSource>()
        where TSource : class, INhAssistantFileSource
    {
        Services.Replace(ServiceDescriptor.Scoped<INhAssistantFileSource, TSource>());
        return this;
    }

    /// <summary>
    /// Lets a data skill version and an agent created in the administration take their instructions
    /// from the application's own text assets instead of text an administrator writes: they store the
    /// asset's id, version and content hash (<see cref="NhAssistantInstructionAssetReference"/>), and the
    /// source supplies the text. A save asks the source and refuses an asset that is missing or whose
    /// hash differs; every load of the run configuration asks again, and an asset that is missing, no
    /// longer has the saved hash or cannot be read is a problem the administration shows: the skill is
    /// out of effect and the agent does not run. A new version of an asset takes effect only when an
    /// administrator saves it as the next version of the skill or agent. Inline instructions stay
    /// possible next to it. The source is registered scoped, unless the application registered it
    /// already, for example as a singleton.
    /// </summary>
    public NhAssistantBuilder UseTextAssetSource<TSource>()
        where TSource : class, INhAiTextAssetSource
    {
        Services.TryAddScoped<TSource>();
        Services.Replace(ServiceDescriptor.Scoped<INhAiTextAssetSource>(provider => provider.GetRequiredService<TSource>()));
        return this;
    }

    /// <summary>
    /// Configures how people upload files to a conversation with
    /// <c>POST {prefix}/conversations/{id}/files</c>: the media types they may upload (PDF, PNG,
    /// JPEG, Word and Excel by default), the extensions it maps to its own types
    /// (<see cref="NhAssistantUploadOptions.MapExtension"/>), the classification an upload gets
    /// (<c>Confidential</c> by default) and an optional scanner
    /// (<see cref="NhAssistantUploadOptions.UseScanner{TScanner}"/>) that every upload passes before the
    /// file source stores it. Uploads need a file source
    /// (<see cref="UseFileSource{TSource}"/>) and an agent that accepts files;
    /// <see cref="NhAssistantLimits.MaxFileBytes"/> and the agent's lower limits bound every upload.
    /// </summary>
    public NhAssistantBuilder ConfigureUploads(Action<NhAssistantUploadOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var uploads = _state.Uploads.Clone();
        configure(uploads);
        uploads.Validate();
        if (uploads.ScannerType is { } scanner)
        {
            Services.TryAddScoped(scanner);
        }

        _state.Uploads = uploads;
        return this;
    }

    /// <summary>
    /// Registers a reading tool under <paramref name="readerId"/> that turns files of the media types it
    /// declares into text, for profiles that do not read those types themselves. The assistant asks the
    /// readers of the application first, in registration order, then the MCP servers an administrator
    /// set up as readers; the first that accepts the type and permits the file's classification reads
    /// it. The model reads the text as untrusted external data, and the file and turn limits count its
    /// tokens. The audit names the reader by <paramref name="readerId"/>, never the text.
    /// </summary>
    public NhAssistantBuilder AddFileReader<TReader>(string readerId, Action<NhAssistantFileReaderBuilder> configure)
        where TReader : class, INhAssistantFileReader
    {
        NhAssistantNames.ValidateSegment(readerId, nameof(readerId));
        ArgumentNullException.ThrowIfNull(configure);
        var reader = new NhAssistantFileReaderBuilder();
        configure(reader);

        _state.AddFileReader(reader.Build(readerId, typeof(TReader)));
        Services.TryAddScoped<TReader>();
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
    private readonly IServiceCollection _services;

    internal NhAssistantSystemSubjectBuilder(NhAssistantRegistrationState state, IServiceCollection services)
    {
        _state = state;
        _services = services;
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

    /// <summary>
    /// Registers the application's list of the people of the whole application, app-wide and per
    /// division (<see cref="INhAssistantSystemMembers"/>), scoped. The whole application keeps no
    /// member snapshot, so without it an agent event in a thread of the whole application reaches only
    /// the people of that thread: those who take part in it and those who decided its proposals. With
    /// it, notifications consider the listed people next to them, at most
    /// <see cref="INhAssistantSystemMembers.MaxMembers"/>. Roles still come from the read and approve
    /// policies and the agent's view policy, evaluated for each person now: a listed person without the
    /// approve policy hears nothing meant for approvers, and listing someone grants nothing.
    /// </summary>
    public NhAssistantSystemSubjectBuilder Members<TMembers>()
        where TMembers : class, INhAssistantSystemMembers
    {
        _services.Replace(ServiceDescriptor.Scoped<INhAssistantSystemMembers, TMembers>());
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
    private readonly Dictionary<string, NhAssistantAgentMetadataEntry> _agentMetadata = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, IReadOnlyDictionary<string, NhAssistantAgentMetadataCheck>>? _codeMetadata;
    private readonly Dictionary<Type, NhAssistantSubjectTypeOptions> _subjectTypes = [];
    private NhAssistantRunSnapshot? _snapshot;
    private readonly List<Runs.NhAssistantProposalGuardRegistration> _proposalGuards = [];
    private readonly List<NhAssistantRevalidationDefinition> _revalidations = [];
    private readonly List<NhAssistantFileReaderDefinition> _fileReaders = [];
    private readonly Dictionary<string, NhAssistantApplicationExecution> _applicationExecutions = new(StringComparer.Ordinal);

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

    /// <summary>
    /// The longest tenant id, as callers' tenants and stored tenants are.
    /// </summary>
    public const int MaxTenantIdLength = 256;

    /// <summary>
    /// The one tenant of a single-tenant host (<see cref="NhAssistantBuilder.UseSingleTenant"/>), whose
    /// people take part in the work of the whole application; <see langword="null"/> without the mode.
    /// </summary>
    public string? SingleTenantId { get; set; }

    public NhAiTextAsset? DefaultApplicationContext { get; set; }

    public NhAssistantLimits Limits { get; set; } = new();

    /// <summary>
    /// How people upload files to a conversation; see <see cref="NhAssistantBuilder.ConfigureUploads"/>.
    /// </summary>
    public NhAssistantUploadOptions Uploads { get; set; } = new();

    /// <summary>
    /// Reservations inside assistant turns also go to the application's budget manager; see
    /// <see cref="NhAssistantBuilder.UseApplicationBudgetInTurns"/>.
    /// </summary>
    public bool ApplicationBudgetInTurns { get; set; }

    /// <summary>
    /// Agent notifications and the notifications of application messages also reach the
    /// application's inbox; see <see cref="NhAssistantBuilder.UseInAppAgentNotifications"/>.
    /// </summary>
    public bool InAppNotifications { get; set; }

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

    /// <summary>
    /// The application's ceiling of consultations defined as data; <see langword="null"/> while
    /// consultations exist in code only.
    /// </summary>
    public NhAssistantConsultationCeiling? ConsultationsAsData { get; set; }

    /// <summary>
    /// The ceiling of consultations defined as data of an agent: the application's for an agent
    /// created in the administration, both ceilings together for an agent in code that is consultable
    /// as data, <see langword="null"/> when the agent may get none.
    /// </summary>
    public NhAssistantConsultationCeiling? ConsultationCeilingOf(string agentId)
    {
        if (ConsultationsAsData is not { } application)
        {
            return null;
        }
        if (_agentRuns.TryGetValue(agentId, out var code))
        {
            return code.ConsultationCeiling?.Intersect(application);
        }

        return _agents.ContainsKey(agentId) ? null : application;
    }

    /// <summary>
    /// The application's ceiling of hand-offs defined as data; <see langword="null"/> while hand-offs
    /// exist in code only.
    /// </summary>
    public NhAssistantHandOffCeiling? HandOffsAsData { get; set; }

    /// <summary>
    /// The application's ceiling of shadow variants created as data; <see langword="null"/> while
    /// shadow variants exist in code only.
    /// </summary>
    public NhAssistantShadowVariantCeiling? ShadowVariantsAsData { get; set; }

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
    /// The entries of agent metadata registered with <c>AddAgentMetadata</c>, by name.
    /// </summary>
    public IReadOnlyDictionary<string, NhAssistantAgentMetadataEntry> AgentMetadata => _agentMetadata;

    /// <summary>
    /// Registers an entry of agent metadata once; a name registered twice is refused, because two
    /// registrations cannot be compared.
    /// </summary>
    public void AddAgentMetadata(NhAssistantAgentMetadataEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (_agentMetadata.ContainsKey(entry.Name))
        {
            throw new InvalidOperationException($"Agent metadata '{entry.Name}' is already registered.");
        }
        if (_agentMetadata.Count >= NhAssistantAgentMetadataLimits.MaxEntries)
        {
            throw new InvalidOperationException(
                $"An application registers at most {NhAssistantAgentMetadataLimits.MaxEntries} agent metadata entries.");
        }

        _agentMetadata.Add(entry.Name, entry);
    }

    /// <summary>
    /// The checked metadata values that an agent in code sets in <c>ConfigureAgent</c>, by name; empty
    /// for an agent without any. Checked once, at startup or on first use.
    /// </summary>
    public IReadOnlyDictionary<string, NhAssistantAgentMetadataCheck> CodeMetadataOf(string agentId)
    {
        var all = Volatile.Read(ref _codeMetadata);
        if (all is null)
        {
            all = CheckCodeMetadata();
            Volatile.Write(ref _codeMetadata, all);
        }

        return all.TryGetValue(agentId, out var values)
            ? values
            : new Dictionary<string, NhAssistantAgentMetadataCheck>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Checks every metadata value in code as a save checks data: the entry is registered for the type
    /// the agent sets, fits its size limit and every validator accepts it. Fails startup otherwise.
    /// </summary>
    private Dictionary<string, IReadOnlyDictionary<string, NhAssistantAgentMetadataCheck>> CheckCodeMetadata()
    {
        var checkedValues = new Dictionary<string, IReadOnlyDictionary<string, NhAssistantAgentMetadataCheck>>(StringComparer.Ordinal);
        foreach (var (agentId, options) in _agentRuns)
        {
            var values = new Dictionary<string, NhAssistantAgentMetadataCheck>(StringComparer.Ordinal);
            foreach (var (name, value) in options.CodeMetadata)
            {
                if (!_agentMetadata.TryGetValue(name, out var entry))
                {
                    throw new InvalidOperationException(
                        $"Agent '{agentId}' sets metadata '{name}', which is not registered. Register it with AddAgentMetadata.");
                }
                if (entry.ValueType != value.Type)
                {
                    throw new InvalidOperationException(
                        $"Agent '{agentId}' sets metadata '{name}' as {value.Type.Name}, but the entry holds {entry.ValueType.Name}.");
                }

                var check = entry.CheckValue(value.Value);
                if (!check.Valid)
                {
                    throw new InvalidOperationException(
                        $"The metadata '{name}' of agent '{agentId}' is invalid: {check.Describe()}.");
                }

                values.Add(name, check);
            }

            if (values.Count > 0)
            {
                checkedValues.Add(agentId, values);
            }
        }

        return checkedValues;
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

    /// <summary>
    /// The reading tools of the application in registration order.
    /// </summary>
    public IReadOnlyList<NhAssistantFileReaderDefinition> FileReaders => _fileReaders;

    public void AddFileReader(NhAssistantFileReaderDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (_fileReaders.Any(item => string.Equals(item.Id, definition.Id, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"File reader '{definition.Id}' is already registered.");
        }

        _fileReaders.Add(definition);
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
    /// The candidate versions of data triggers in effect, by trigger id, from the last loaded snapshot.
    /// </summary>
    public IReadOnlyDictionary<string, NhAssistantTriggerDefinition> TriggerCandidates =>
        Volatile.Read(ref _snapshot)?.TriggerCandidates ?? NoTriggerCandidates;

    private static readonly IReadOnlyDictionary<string, NhAssistantTriggerDefinition> NoTriggerCandidates =
        new Dictionary<string, NhAssistantTriggerDefinition>(StringComparer.Ordinal);

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
    /// The tools whose approved proposals the application executes, by tool id.
    /// </summary>
    public IReadOnlyDictionary<string, NhAssistantApplicationExecution> ApplicationExecutions => _applicationExecutions;

    public void AddApplicationExecution(NhAssistantApplicationExecution execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        if (_applicationExecutions.TryGetValue(execution.ToolId, out var existing))
        {
            if (existing == execution)
            {
                // AddNewHeapAssistant may run more than once for the same service collection.
                return;
            }

            throw new InvalidOperationException(
                $"Tool '{execution.ToolId}' is already executed by the application with another report deadline or other options.");
        }

        _applicationExecutions.Add(execution.ToolId, execution);
    }

    /// <summary>
    /// Whether the application executes the approved proposals of <paramref name="toolId"/>, and how
    /// long it may take to report.
    /// </summary>
    public bool TryGetApplicationExecution(
        string toolId,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out NhAssistantApplicationExecution? execution)
    {
        return _applicationExecutions.TryGetValue(toolId, out execution);
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

            foreach (var consultation in options.Consultations)
            {
                ValidateConsultationProposals(options, consultation);
                foreach (var selector in consultation.ToolSelectors ?? [])
                {
                    if (!agent.ToolSelectors.Any(agentSelector => Covers(agentSelector, selector)))
                    {
                        throw new InvalidOperationException(
                            $"The consultation of agent '{agentId}' about '{consultation.SubjectType}' selects '{selector}', which the agent's own tool selectors do not cover.");
                    }
                }
            }

            if (options.ConsultationCeiling is { } consultationCeiling)
            {
                ValidateConsultationCeiling(options, consultationCeiling);
            }

            // WithLimits may run after ConfigureAgent: agent and skill file limits are checked now.
            options.FileLimits?.Validate(Limits, $"agent '{agentId}'");
            foreach (var skill in options.Skills.Values)
            {
                skill.FileLimits?.Validate(Limits, $"skill '{skill.Id}' of agent '{agentId}'");
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

        ValidateHandOffCeiling();
        ValidateShadowVariantCeiling();

        // The metadata values in code pass the checks a save applies to data.
        Volatile.Write(ref _codeMetadata, CheckCodeMetadata());
    }

    /// <summary>
    /// The static rules of a consultation's proposals: its own approval policy is registered, and a
    /// route that executes with the agent's own rights is refused for an agent that answers to the
    /// subject's responsible person, for the whole application and for a known policy that names no
    /// approver other than the person who caused the proposal.
    /// </summary>
    private void ValidateConsultationProposals(NhAssistantAgentRunOptions options, NhAssistantConsultationOptions consultation)
    {
        var owner = $"consultation of agent '{consultation.AgentId}' about '{consultation.SubjectType}'";
        if (consultation.ProposalPolicyId is { } proposalPolicy)
        {
            ValidatePolicyReference(proposalPolicy, owner);
        }
        if (consultation.AtSubjectRights != NhAssistantConsultationRights.Agent)
        {
            return;
        }

        if (options.Accountability == NhAiAgentAccountability.SubjectResponsible)
        {
            throw new InvalidOperationException(
                $"The {owner} executes proposals with the agent's own rights, but the agent answers to the subject's responsible person. Use a technical account, let the agent answer for itself, or use AtSubject(NhAssistantConsultationRights.Asker).");
        }
        if (string.Equals(consultation.SubjectType, NhAiSubjectRef.SystemType, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The {owner} executes proposals with the agent's own rights for the whole application, which is too broad. Use AskerDecides() there.");
        }

        var known = options.ToolApprovalPolicies.Values
            .Append(consultation.ProposalPolicyId)
            .Append(options.ApprovalPolicyId)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal);
        foreach (var policyId in known)
        {
            if (_approvalPolicies.TryGetValue(policyId, out var policy)
                && policy.Approvers.All(rule => rule is NhAiInitiatorApproverRule))
            {
                throw new InvalidOperationException(
                    $"The {owner} executes proposals with the agent's own rights after another approver decided them, but approval policy '{policyId}' names only the initiator. Add an approver rule.");
            }
        }
    }

    /// <summary>
    /// The ceiling of an agent in code that is consultable as data stays within the application's,
    /// and the route with the agent's own rights needs an agent with rights of its own: grants, or a
    /// technical account it acts for.
    /// </summary>
    private void ValidateConsultationCeiling(NhAssistantAgentRunOptions options, NhAssistantConsultationCeiling ceiling)
    {
        if (ConsultationsAsData is not { } application)
        {
            throw new InvalidOperationException(
                $"Agent '{options.AgentId}' is consultable as data, but the application does not call UseConsultationsAsData.");
        }
        if (ceiling.OutsideOf(application) is { } outside)
        {
            throw new InvalidOperationException(
                $"The ceiling of consultations as data of agent '{options.AgentId}' allows {outside} beyond UseConsultationsAsData. Keep it within the application's ceiling.");
        }
        if (!ceiling.Allows(NhAssistantConsultationRoutes.SubjectAgentRights))
        {
            return;
        }
        if (options.Accountability == NhAiAgentAccountability.SubjectResponsible)
        {
            throw new InvalidOperationException(
                $"Agent '{options.AgentId}' may be consulted as data with proposals that execute with its own rights, but it answers to the subject's responsible person. Leave subject-agent-rights out of ConsultableAsData, or use a technical account.");
        }
        if (options.Accountability == NhAiAgentAccountability.Agent && options.Grants.Count == 0)
        {
            throw new InvalidOperationException(
                $"Agent '{options.AgentId}' may be consulted as data with proposals that execute with its own rights, but it answers for itself without grants, so it has no rights of its own. Leave subject-agent-rights out of ConsultableAsData, or grant tools.");
        }
    }

    /// <summary>
    /// The agents in code that the ceiling of hand-offs as data names pass the checks of
    /// <c>AcceptHandOffs</c>: they are configured with <c>ConfigureAgent</c>, and a target works on the
    /// subject type.
    /// </summary>
    private void ValidateHandOffCeiling()
    {
        if (HandOffsAsData is not { } ceiling)
        {
            return;
        }

        foreach (var rule in ceiling.Rules)
        {
            foreach (var agentId in rule.Targets.Concat(rule.Sources).Where(agentId => !_agentRuns.ContainsKey(agentId)))
            {
                throw new InvalidOperationException(
                    $"UseHandOffsAsData connects agent '{agentId}' about '{rule.SubjectType}', which is not configured with ConfigureAgent.");
            }
            foreach (var target in rule.Targets.Where(target => !_agentRuns[target].WorksOn.Covers(rule.SubjectType)))
            {
                throw new InvalidOperationException(
                    $"UseHandOffsAsData lets agent '{target}' accept hand-offs about '{rule.SubjectType}', which it does not work on. Add the type to WorksOn.");
            }
        }
    }

    /// <summary>
    /// The live agents in code that the ceiling of shadow variants as data names pass the checks of
    /// <c>ShadowOf</c>: they are configured with <c>ConfigureAgent</c> and are live agents, neither in
    /// shadow by their code nor variants themselves.
    /// </summary>
    private void ValidateShadowVariantCeiling()
    {
        if (ShadowVariantsAsData is not { } ceiling)
        {
            return;
        }

        foreach (var agentId in ceiling.LiveAgents)
        {
            if (!_agentRuns.TryGetValue(agentId, out var live))
            {
                throw new InvalidOperationException(
                    $"UseShadowVariantsAsData allows variants of '{agentId}', which is not configured with ConfigureAgent.");
            }
            if (live.Shadow || live.ShadowOfAgentId is not null)
            {
                throw new InvalidOperationException(
                    $"UseShadowVariantsAsData allows variants of '{agentId}', which is not a live agent.");
            }
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
