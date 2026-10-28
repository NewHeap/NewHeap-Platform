using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Governance;
using NewHeap.Platform.AI.Chat.Live;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// Records proposals of agents for people, for the application's own code (FB21), such as a page where a
/// person asks for a change that needs someone else's approval. It is a server API; there is no endpoint
/// for it. The application vouches for the person, as for <see cref="INhAssistantProposalDecisionService"/>:
/// it authenticated them and passes their principal, actor id and tenant, which the application's context
/// resolver must confirm. The call runs without the caller's request, in services of its own.
/// </summary>
public interface INhAssistantProposals
{
    /// <summary>
    /// Records a deferred proposal of the agent for <see cref="NhAssistantProposalRequest.Person"/> without a
    /// run. It waits in the agent's thread at the subject, created when it is new, for the approvers of its
    /// policy, and executes after their approval as every deferred proposal does.
    /// <para>
    /// The proposal takes the governed path of a run's proposal: the person may ask the agent to work on the
    /// subject (the participant role, the agent's policy, what it works on, as a start by hand checks it);
    /// the tool is one the agent proposes with (its selectors, an effect, propose autonomy, no composition,
    /// script or MCP tool); the tool's contract binds the arguments and the person's own rights allow the
    /// call (never the agent's grants or the account it answers to); the policy the application names, or
    /// else the one code resolves for the agent and the tool, then the application's approval policy
    /// selector (<see cref="NhAssistantApprovalPolicySource.Application"/>), kept in the proposal's policy
    /// copy; the proposal guards at proposal time and their keys; an open duplicate and the policy's
    /// rejection cooldown; a newer proposal for the same objects that replaces a pending one. It is always
    /// deferred, whatever the policy's mode, and the person is its initiator, so four eyes keep that person
    /// out. Once approved it executes with the person's rights only.
    /// </para>
    /// <para>
    /// Succeeds with <see cref="NhAssistantProposeOutcome.Created"/>, or without recording anything with
    /// <see cref="NhAssistantProposeOutcome.AlreadyOpen"/> and <see cref="NhAssistantProposeOutcome.RecentlyRejected"/>
    /// (naming the existing proposal) and <see cref="NhAssistantProposeOutcome.NotNeeded"/> (with the guard's
    /// reason and explanations). A repeat with the same idempotency key returns the proposal it recorded with
    /// <see cref="NhAssistantProposalResult.Repeated"/>, without a second audit.
    /// </para>
    /// <para>
    /// Fails, in this order, with <c>assistant-disabled</c>; <c>assistant-validation</c> for an idempotency key
    /// that is not usable; <c>assistant-proposal-presentation-invalid</c> for a prepared presentation that breaks
    /// a limit (its field errors name the culture and the rule); <c>assistant-context-unavailable</c> when the
    /// resolver refuses the principal or names another actor or tenant; <c>assistant-agent-forbidden</c>
    /// without the assistant's access policy; <c>assistant-agent-not-configured</c>,
    /// <c>assistant-agent-configured-off</c>, <c>assistant-agent-not-found</c> and <c>assistant-agent-stopped</c>;
    /// <c>assistant-subject-not-found</c> for an unknown subject or one of another tenant;
    /// <c>assistant-subject-access-denied</c>, <c>assistant-agent-forbidden</c> and
    /// <c>assistant-agent-does-not-work-on</c> as a start by hand, and <c>assistant-agent-forbidden</c> for an
    /// agent in shadow; <c>assistant-proposal-refused</c> for a tool the agent does not propose with;
    /// <c>assistant-proposal-presentation-invalid</c> for a presentation of a tool that is never presented;
    /// <c>assistant-no-accountable-owner</c> when nobody answers for the agent at the subject; the governed
    /// call's own code when the arguments do not fit the tool's contract or the person's rights do not allow
    /// it, such as <c>ai-tool-input-invalid</c> or <c>ai-tool-agent-not-granted</c>;
    /// <c>assistant-proposal-idempotency-conflict</c> when the key recorded another proposal; and
    /// <c>assistant-approval-policy-unknown</c>, <c>assistant-approval-policy-selector-failed</c> and
    /// <c>assistant-approval-policy-selector-timed-out</c>. Nothing is recorded then.
    /// </para>
    /// <para>
    /// The content-free audit records <see cref="NhAssistantAuditEventKind.ApprovalRequested"/> and
    /// <see cref="NhAssistantAuditEventKind.AgentProposalRecordedForPerson"/> with the person, the subject, the
    /// policy and the idempotency key; never the arguments or the presentations.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentNullException">The request, its person, arguments or subject is missing.</exception>
    /// <exception cref="ArgumentException">The agent or tool id is empty, or the arguments are no object.</exception>
    Task<TaskResult<NhAssistantProposalResult>> ProposeForAsync(
        NhAssistantProposalRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A deferred proposal that <see cref="INhAssistantProposals.ProposeForAsync"/> records for a person.
/// </summary>
/// <param name="Person">The person the application authenticated, as for the decision service: their principal,
/// which the tool's policies are evaluated against, their actor id, who becomes the proposal's initiator, and
/// their tenant. <see cref="NhAssistantDecidingPerson.Culture"/> is the UI culture the proposal is made in when
/// the application supports it; otherwise the application's default request culture, as a run.</param>
/// <param name="AgentId">The agent whose proposal it is.</param>
/// <param name="ToolId">The governed tool that changes something, one of the agent's tools.</param>
/// <param name="Arguments">The tool's input: an object whose properties are the input's properties, or a
/// <see cref="JsonElement"/> object, as a rule's <c>ProposeAsync</c> takes it. The tool's contract binds and
/// checks it, as for any call.</param>
/// <param name="Subject">The subject the proposal is about and waits at, such as a project.</param>
public sealed record NhAssistantProposalRequest(
    NhAssistantDecidingPerson Person,
    string AgentId,
    string ToolId,
    object Arguments,
    NhAiSubjectRef Subject)
{
    /// <summary>
    /// The registered approval policy the proposal waits under before the application's selector chooses;
    /// <see langword="null"/> resolves it as code does for the agent and the tool (the tool's policy within
    /// the agent, the agent's, then <c>trigger-default</c>). An unknown id fails with
    /// <c>assistant-approval-policy-unknown</c>.
    /// </summary>
    public string? PolicyId { get; init; }

    /// <summary>
    /// The presentations the application prepared, by culture name such as <c>en-US</c> and <c>nl-NL</c>: what
    /// approvers read in their own language, in the culture they read, a parent culture or another culture of
    /// their language, otherwise the presentation of the culture the proposal was made in (or the first). Each
    /// must keep within <see cref="NhAssistantPresentationLimits"/>, at most
    /// <see cref="NhAssistantPresentationLimits.MaxCultures"/> cultures; one that does not is refused with
    /// <c>assistant-proposal-presentation-invalid</c>, never dropped. <see langword="null"/> or empty lets the
    /// application's tool presenters present it, as for a run's proposal. A tool classified
    /// <see cref="NhAiDataClassification.Confidential"/> or above is never presented.
    /// </summary>
    public IReadOnlyDictionary<string, NhAssistantApprovalPresentation>? Presentations { get; init; }

    /// <summary>
    /// The application's key of the request, such as the id of the person's form submission: at most
    /// <see cref="NhAssistantDecisionChannels.MaxIdempotencyKeyLength"/> printable characters without white space.
    /// The same key of the same person for the same agent names one proposal: a repeat returns it, also after it
    /// was decided, and the key with another subject, tool or arguments fails with
    /// <c>assistant-proposal-idempotency-conflict</c>. <see langword="null"/> records a new proposal each time,
    /// unless the same one is open. The audit records it.
    /// </summary>
    public string? IdempotencyKey { get; init; }
}

/// <summary>
/// What <see cref="INhAssistantProposals.ProposeForAsync"/> did.
/// </summary>
/// <param name="Outcome">Whether the proposal was recorded (<see cref="NhAssistantProposeOutcome.Created"/>), the
/// same one is open or was rejected recently, or a guard found it not needed; never
/// <see cref="NhAssistantProposeOutcome.Refused"/>, because a refusal fails the result.</param>
/// <param name="ApprovalId">The recorded proposal, or the open or rejected one that made it unnecessary;
/// <see langword="null"/> when it is not needed.</param>
public sealed record NhAssistantProposalResult(
    NhAssistantProposeOutcome Outcome,
    Guid? ApprovalId)
{
    private static readonly IReadOnlyDictionary<string, string> NoExplanations =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The agent's thread at the subject the proposal waits in.
    /// </summary>
    public Guid? ConversationId { get; init; }

    /// <summary>
    /// The proposal's status, one of <see cref="NhAssistantApprovalStatuses"/>: <c>pending</c> when it was
    /// recorded now, the current status of the one an outcome or a repeat names.
    /// </summary>
    public string? Status { get; init; }

    /// <summary>
    /// The proposal's hash, which a decision through <see cref="INhAssistantProposalDecisionService"/> passes back.
    /// </summary>
    public string? ProposalHash { get; init; }

    /// <summary>
    /// The policy the proposal waits under, as its policy copy keeps it: the one the application's selector chose.
    /// </summary>
    public string? PolicyId { get; init; }

    /// <summary>
    /// Whether the policy keeps the person, the initiator, from approving it.
    /// </summary>
    public bool FourEyes { get; init; }

    /// <summary>
    /// When the proposal expires, on the application's clock.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>
    /// When people rejected the same proposal, for <see cref="NhAssistantProposeOutcome.RecentlyRejected"/>.
    /// </summary>
    public DateTimeOffset? RejectedAt { get; init; }

    /// <summary>
    /// The pending proposals for the same objects that the new one replaced, by the policy's rule.
    /// </summary>
    public IReadOnlyList<Guid> Superseded { get; init; } = [];

    /// <summary>
    /// For <see cref="NhAssistantProposeOutcome.NotNeeded"/>, the reason code of the guard that found the
    /// proposal not needed.
    /// </summary>
    public string? NotNeededReason { get; init; }

    /// <summary>
    /// For <see cref="NhAssistantProposeOutcome.NotNeeded"/>, why: already done, not applicable or changed.
    /// </summary>
    public NhAiStaleKind? NotNeededKind { get; init; }

    /// <summary>
    /// For <see cref="NhAssistantProposeOutcome.NotNeeded"/>, the guard's explanation in the culture the
    /// proposal was made in and in every supported UI culture, by culture name; empty otherwise.
    /// </summary>
    public IReadOnlyDictionary<string, string> Explanations { get; init; } = NoExplanations;

    /// <summary>
    /// The idempotency key recorded this proposal before; nothing changed and nothing was audited again.
    /// </summary>
    public bool Repeated { get; init; }
}

/// <summary>
/// Records a deferred proposal for a person without a run (FB21): the person, agent and subject checks of a
/// start by hand, the governed call that binds the tool's contract and the person's rights, the policy and
/// the application's selector, the guards, keys and duplicates, then the proposal and its waiting call in
/// one save in the agent's thread at the subject.
/// </summary>
internal sealed class NhAssistantProposals(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<NhAssistantOptions> options) : INhAssistantProposals
{
    private const string Summary = "Approval is required before this tool can run.";

    /// <summary>
    /// The arguments as <see cref="NhAiProposalFactory"/> records them in the proposal.
    /// </summary>
    private static readonly JsonSerializerOptions ProposalJson = new(JsonSerializerDefaults.Web);

    public async Task<TaskResult<NhAssistantProposalResult>> ProposeForAsync(
        NhAssistantProposalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        NhAssistantPersonCalls.ValidatePerson(request.Person);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AgentId, nameof(request));
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ToolId, nameof(request));
        ArgumentNullException.ThrowIfNull(request.Arguments, nameof(request));
        ArgumentNullException.ThrowIfNull(request.Subject, nameof(request));
        var values = ArgumentsOf(request.Arguments);

        if (!options.CurrentValue.Enabled)
        {
            return Failed(NhAssistantErrorCodes.Disabled, "The assistant is disabled.");
        }
        // The key often comes from the application's own request, so an unusable one is an expected
        // refusal rather than a programming error.
        if (!NhAssistantDecisionChannels.IsValidIdempotencyKey(request.IdempotencyKey))
        {
            return TaskResult<NhAssistantProposalResult>.Failed(
                NhAssistantValidationErrors.Failed("idempotencyKey", NhAssistantFieldErrors.Invalid));
        }

        var presentations = PresentationsOf(request.Presentations);
        if (!presentations.Success)
        {
            return TaskResult<NhAssistantProposalResult>.Failed(presentations);
        }

        return await NhAssistantPersonCalls.RunWithoutCallerAsync(
            scopeFactory,
            null,
            async (services, _) =>
            {
                // The proposal is made in the person's UI culture when the application supports it, as
                // a run makes it in the default request culture otherwise.
                using var culture = NhAssistantRunCulture.Use(
                    services.GetRequiredService<IOptions<RequestLocalizationOptions>>().Value,
                    request.Person.Culture);
                var proposing = new Proposing(services, request, values, presentations.Data!);
                return await proposing.RunAsync(cancellationToken);
            },
            cancellationToken);
    }

    /// <summary>
    /// The input's properties at the top level, as a rule's proposal passes them; a governed tool with an
    /// input envelope wraps them.
    /// </summary>
    private static Dictionary<string, object?> ArgumentsOf(object arguments)
    {
        var json = arguments is JsonElement element
            ? element
            : JsonSerializer.SerializeToElement(arguments, arguments.GetType(), JsonSerializerOptions.Web);
        if (json.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("The arguments of a proposal are an object with the tool's input properties.", nameof(arguments));
        }

        return json.EnumerateObject().ToDictionary(
            property => property.Name,
            property => (object?)property.Value.Clone(),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// The prepared presentations by culture name, each within the limits; empty without any. A
    /// presentation over a limit, a culture that is not valid and too many cultures are refused, never
    /// dropped, with a field error per culture that names the broken rule.
    /// </summary>
    private static TaskResult<IReadOnlyDictionary<string, NhAssistantApprovalPresentation>> PresentationsOf(
        IReadOnlyDictionary<string, NhAssistantApprovalPresentation>? presentations)
    {
        var normalized = new Dictionary<string, NhAssistantApprovalPresentation>(StringComparer.OrdinalIgnoreCase);
        if (presentations is null || presentations.Count == 0)
        {
            return new TaskResult<IReadOnlyDictionary<string, NhAssistantApprovalPresentation>> { Data = normalized };
        }

        var errors = new NhAssistantValidationErrors();
        if (presentations.Count > NhAssistantPresentationLimits.MaxCultures)
        {
            errors.Add("presentations", NhAssistantPresentationRules.TooManyCultures);
        }

        foreach (var (name, presentation) in presentations)
        {
            if (CultureOf(name) is not { } culture || normalized.ContainsKey(culture.Name))
            {
                errors.Add("presentations", NhAssistantPresentationRules.CultureInvalid);
                continue;
            }
            if (presentation is null)
            {
                errors.Add("presentations." + culture.Name, NhAssistantFieldErrors.Required);
                continue;
            }
            if (NhAssistantPresentationLimits.FindBrokenRule(presentation) is { } rule)
            {
                errors.Add("presentations." + culture.Name, rule);
                continue;
            }

            normalized[culture.Name] = presentation;
        }

        if (!errors.IsEmpty)
        {
            return TaskResult<IReadOnlyDictionary<string, NhAssistantApprovalPresentation>>.Failed(
                errors.ToResult(NhAssistantErrorCodes.ProposalPresentationInvalid, "A prepared presentation of the proposal is invalid."));
        }

        return new TaskResult<IReadOnlyDictionary<string, NhAssistantApprovalPresentation>> { Data = normalized };
    }

    private static CultureInfo? CultureOf(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        try
        {
            var culture = CultureInfo.GetCultureInfo(name, predefinedOnly: true);
            return string.IsNullOrEmpty(culture.Name) ? null : culture;
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }

    private static TaskResult<NhAssistantProposalResult> Failed(string code, string message)
    {
        return TaskResult<NhAssistantProposalResult>.Failed(code, message);
    }

    /// <summary>
    /// One proposal for one person, in the services of the call.
    /// </summary>
    private sealed class Proposing(
        IServiceProvider services,
        NhAssistantProposalRequest request,
        IReadOnlyDictionary<string, object?> values,
        IReadOnlyDictionary<string, NhAssistantApprovalPresentation> presentations)
    {
        private readonly NhAssistantDecidingPerson _person = request.Person;
        private readonly NhAssistantRegistrationState _registration = services.GetRequiredService<NhAssistantRegistrationState>();
        private readonly INhAssistantStore _store = services.GetRequiredService<INhAssistantStore>();
        private readonly NhAssistantRunStore _runs = services.GetRequiredService<NhAssistantRunStore>();
        private readonly TimeProvider _applicationClock = services.GetService<TimeProvider>() ?? TimeProvider.System;

        public async Task<TaskResult<NhAssistantProposalResult>> RunAsync(CancellationToken cancellationToken)
        {
            var admitted = await AdmitAsync(cancellationToken);
            if (!admitted.Success)
            {
                return TaskResult<NhAssistantProposalResult>.Failed(admitted);
            }

            var admission = admitted.Data!;
            var captured = await CaptureAsync(admission, cancellationToken);
            if (!captured.Success)
            {
                return TaskResult<NhAssistantProposalResult>.Failed(captured);
            }

            return await RecordAsync(admission, captured.Data!, cancellationToken);
        }

        /// <summary>
        /// The person, the agent, the subject, the tool and the thread, checked as a start by hand checks them.
        /// </summary>
        private async Task<TaskResult<Admission>> AdmitAsync(CancellationToken cancellationToken)
        {
            var resolved = await NhAssistantPersonCalls.ResolveAsync(services, _person, null, cancellationToken);
            if (!resolved.Success)
            {
                return TaskResult<Admission>.Failed(resolved);
            }

            var personInvocation = resolved.Data!;
            if (!await NhAssistantPersonCalls.PassesAccessPolicyAsync(services, _person, personInvocation))
            {
                return Refused(NhAssistantErrorCodes.AgentForbidden, "The person may not use the assistant.");
            }

            await services.GetRequiredService<NhAssistantRunConfigurations>().EnsureCurrentAsync(cancellationToken);
            // A paused agent's proposals wait and execute as before; a stopped one takes none.
            if (await services.GetRequiredService<NhAssistantAgentTriggers>().RefusalOfAsync(request.AgentId, refusePaused: false, cancellationToken) is { } stateRefusal)
            {
                return Refused(stateRefusal, "The agent takes no proposals now.");
            }

            var agent = await services.GetRequiredService<NhAssistantAgentCatalog>().FindAsync(request.AgentId, includeDisabled: false, cancellationToken);
            if (agent is null || !_registration.AgentRuns.TryGetValue(agent.Id, out var runOptions))
            {
                return Refused(NhAssistantErrorCodes.AgentNotFound, "The assistant agent was not found.");
            }

            var subjects = services.GetRequiredService<NhAssistantSubjectAccessService>();
            var subject = await subjects.DescribeAsync(request.Subject, cancellationToken);
            if (subject is null || !services.GetRequiredService<NhAssistantTenancy>().Reaches(subject, _person.TenantId))
            {
                return Refused(NhAssistantErrorCodes.SubjectNotFound, "The subject was not found.");
            }

            // The person may ask the agent to work on the subject, as a start by hand (K9).
            var access = await subjects.ResolveForAgentAsync(request.Subject, agent.Id, _person.ActorId, cancellationToken);
            var start = await services.GetRequiredService<NhAssistantManualStarts>()
                .CheckStartAsync(agent, request.Subject, access, _person.ActorId, _person.User, cancellationToken);
            if (!start.Success)
            {
                return TaskResult<Admission>.Failed(start);
            }
            // An agent in shadow works in shadow threads, which reach no approver.
            if (NhAssistantShadowModes.IsAgentShadow(runOptions, agent.ShadowOverride))
            {
                return Refused(NhAssistantErrorCodes.AgentForbidden, "The agent works in shadow and takes no proposals for people.");
            }

            if (FindTool(agent.Definition) is not { } tool)
            {
                return Refused(NhAssistantErrorCodes.ProposalRefused, "The agent does not propose with this tool.");
            }
            if (presentations.Count > 0 && tool.Descriptor.DataClassification >= NhAiDataClassification.Confidential)
            {
                return TaskResult<Admission>.Failed(new NhAssistantValidationErrors()
                    .Add("presentations", NhAssistantPresentationRules.NotPresented)
                    .ToResult(NhAssistantErrorCodes.ProposalPresentationInvalid, "The tool is never presented."));
            }

            // The agent's thread at the subject, or the one that is created once every check passed.
            var thread = await services.GetRequiredService<NhAssistantProposalPlacement>()
                .PrepareThreadAtAsync(agent, subject, shadow: false, cancellationToken);
            if (!thread.Success)
            {
                return TaskResult<Admission>.Failed(thread);
            }

            return TaskResult<Admission>.Succeeded(new Admission(
                personInvocation,
                agent,
                runOptions,
                subject,
                thread.Data!,
                tool.Function,
                tool.Descriptor));
        }

        /// <summary>
        /// The governed tool the agent proposes with: one of its selectors names it, it changes something,
        /// the agent may propose, and it is no composition, script or MCP tool.
        /// </summary>
        private (AIFunction Function, NhAiToolDescriptor Descriptor)? FindTool(NhAssistantAgentDefinition agent)
        {
            if (!NhAssistantTurnRunner.OffersEffectsToRules(agent)
                || request.ToolId.StartsWith("mcp.", StringComparison.Ordinal)
                || !agent.ToolSelectors.Any(selector => NhAssistantTurnRunner.IsSelected(selector, request.ToolId)))
            {
                return null;
            }

            foreach (var catalog in services.GetServices<INhAiToolCatalog>().Select(NhAiToolCatalogViews.Of))
            {
                var descriptor = catalog.Descriptors
                    .Where(item => string.Equals(item.Id, request.ToolId, StringComparison.Ordinal))
                    .OrderByDescending(item => item.Version)
                    .FirstOrDefault();
                if (descriptor is null
                    || descriptor.Effect == NhAiToolEffect.ReadOnly
                    || descriptor.CompositionFeature != NhAiCompositionFeature.None)
                {
                    continue;
                }

                foreach (var function in catalog.CreateFunctions(services))
                {
                    if (function is INhAiGovernedAIFunction governed
                        && string.Equals(governed.Descriptor.Id, descriptor.Id, StringComparison.Ordinal)
                        && governed.Descriptor.Version == descriptor.Version)
                    {
                        return (function, governed.Descriptor);
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Runs the governed call as the agent for the person, with approval required for every effect: the
        /// tool's contract binds the arguments, the person's own rights authorize it, and the invoker hands
        /// over the exact input and context it evaluated instead of running the tool.
        /// </summary>
        private async Task<TaskResult<Capture>> CaptureAsync(Admission admission, CancellationToken cancellationToken)
        {
            var (agent, thread, descriptor) = (admission.Agent, admission.Thread, admission.Descriptor);
            var turnId = Guid.NewGuid();
            var deadline = DateTimeOffset.UtcNow.Add(_registration.Limits.TurnTimeout);
            var call = new AssistantToolInvocation
            {
                Id = Guid.NewGuid(),
                ConversationId = thread.ConversationId,
                TurnId = turnId,
                // The call waits outside the thread's messages, as a proposal placed at another object.
                MessageId = Guid.Empty,
                CallId = "proposed-for-" + Guid.NewGuid().ToString("N"),
                FunctionName = NhAssistantContent.Bound(admission.Function.Name, 256),
                ToolId = descriptor.Id,
                ToolVersion = descriptor.Version,
                ContractHash = NhAssistantContent.Bound(descriptor.ContractHash, 128),
                DisplayName = descriptor.Id,
                Status = NhAssistantToolCallStatuses.AwaitingApproval,
                ArgumentsJson = NhAssistantContent.SerializeArguments(values),
                DataClassification = descriptor.DataClassification,
                RetentionCategory = descriptor.RetentionCategory,
                StartedAt = DateTimeOffset.UtcNow
            };
            var turn = new NhAssistantTurnScope
            {
                ConversationId = thread.ConversationId,
                TurnId = turnId,
                Agent = agent.Definition,
                OwnerActorId = _person.ActorId,
                AccountableOwnerId = thread.AccountableOwnerId,
                TenantId = admission.Subject.TenantId,
                ModelProfileName = agent.Definition.ProfileName,
                PromptVersion = string.Empty,
                PromptHash = string.Empty,
                Deadline = deadline,
                Limits = _registration.Limits
            };
            // The person's own rights only: the person delegates, as in their own turn in the thread, and
            // neither the agent's grants nor the account it answers to lend anything. Every effect needs an
            // approval in this scope, so the call becomes a proposal and never runs here.
            var scope = new NhAiAgentInvocationScope(
                agent.Id,
                agent.Definition.ActorId,
                agent.Definition.Version,
                admission.Subject,
                thread.AccountableOwnerId,
                turnId.ToString(),
                NhAssistantInvocationGate.Purpose)
            {
                Accountability = admission.RunOptions.Accountability,
                InitiatorActorId = _person.ActorId,
                DelegateFromInitiator = true,
                IsRequestTurn = true,
                Grants = [],
                ElevatedReads = admission.RunOptions.ElevatedReads,
                RequireApprovalForEffects = true,
                CorrelationId = turnId.ToString(),
                Deadline = deadline,
                ModelProfileName = agent.Definition.ProfileName
            };
            var arguments = new AIFunctionArguments(values.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal))
            {
                Services = services
            };

            NhAssistantToolExecution execution;
            using (NhAssistantExecutionScope.EnterTurn(turn))
            using (NhAiPersonInvocationScopes.Begin(admission.Person))
            using (NhAiAgentInvocationScopes.Begin(scope))
            {
                execution = await NhAssistantToolCallInterceptor.ExecuteAsync(
                    descriptor,
                    call,
                    null,
                    null,
                    token => admission.Function.InvokeAsync(arguments, token),
                    cancellationToken,
                    services.GetRequiredService<ILogger<NhAssistantProposals>>());
            }

            if (execution.Outcome.Kind != NhAssistantToolOutcomeKind.ApprovalMissing
                || execution.Call.CapturedArguments is not { } input
                || execution.Call.CapturedContext is not { RunId: not null, AccountableOwnerId: not null } context)
            {
                // The arguments do not fit the tool's contract, the person's rights do not allow the call,
                // or another evidence provider answered: nothing is proposed.
                var code = execution.Outcome.Kind == NhAssistantToolOutcomeKind.Failed
                    ? execution.Outcome.Code
                    : NhAssistantErrorCodes.ProposalRefused;
                return TaskResult<Capture>.Failed(code, "The call cannot be proposed.");
            }

            // A thread that does not exist yet has no conversation the call could name.
            context = context with { ConversationId = thread.ConversationId == Guid.Empty ? null : thread.ConversationId };
            var canonical = NhAiProposalFactory.Canonicalize(JsonSerializer.SerializeToElement(input, input.GetType(), ProposalJson));
            return TaskResult<Capture>.Succeeded(new Capture(call, input, context, canonical));
        }

        /// <summary>
        /// The policy, the guards, the duplicates and the presentations, then the proposal with its waiting
        /// call, its audit, the live update and the notification of its approvers.
        /// </summary>
        private async Task<TaskResult<NhAssistantProposalResult>> RecordAsync(
            Admission admission,
            Capture capture,
            CancellationToken cancellationToken)
        {
            var (agent, subject, descriptor) = (admission.Agent, admission.Subject, admission.Descriptor);
            var thread = admission.Thread;
            var key = request.IdempotencyKey;
            var argumentsHash = NhAssistantProposalDuplicates.IdentityOf(descriptor.Id, capture.Arguments);
            var approvalId = key is null
                ? Guid.NewGuid()
                : NhAssistantPersonProposals.ApprovalIdOf(_person.TenantId, agent.Id, _person.ActorId, key);
            if (key is not null && await _store.FindApprovalAsync(approvalId, cancellationToken) is { } earlier)
            {
                return await RepeatAsync(earlier, admission, argumentsHash, cancellationToken);
            }

            var chosen = await ChoosePolicyAsync(admission, capture, cancellationToken);
            if (!chosen.Success)
            {
                return TaskResult<NhAssistantProposalResult>.Failed(chosen);
            }

            var policy = chosen.Data!;
            // The proposal lives by the application's clock; when it was recorded stays on the system clock.
            var now = DateTimeOffset.UtcNow;
            var proposedAt = _applicationClock.GetUtcNow();
            var expiresAt = proposedAt.Add(policy.Lifetime);
            var context = capture.Context;
            // No prompt made it, so it binds to the tool's contract only.
            var proposal = services.GetRequiredService<INhAiProposalFactory>().Create(new NhAiProposalCreateRequest(
                Guid.NewGuid(),
                context.RunId!,
                context.ActorKind,
                context.ActorId,
                context.AccountableOwnerId!,
                descriptor,
                capture.Input,
                context.ExecutionScopes.Take(32).Select(item => new NhAiProposalTarget(item.Type, item.Id)).ToArray(),
                NhAssistantContent.Bound(string.IsNullOrWhiteSpace(descriptor.Description) ? descriptor.Id : descriptor.Description, 512),
                [NhAssistantToolCallInterceptor.EffectCode(descriptor.Effect)],
                new Dictionary<string, string>(StringComparer.Ordinal),
                new NhAiActionBudget(1),
                proposedAt,
                expiresAt));

            // The guards see the call that proposes, without a turn or channel (FB26).
            var guards = services.GetRequiredService<NhAssistantProposalGuards>();
            var guardContext = new NhAiProposalGuardContext(
                proposal.ProposalId,
                descriptor.Id,
                proposal.Arguments,
                subject.Subject,
                thread.ScopeKey,
                now)
            {
                Invocation = context,
                ConversationId = context.ConversationId
            };
            if (guards.HasGuards(descriptor.Id, NhAiProposalGuardStages.Proposal))
            {
                var check = await guards.CheckAsync(guardContext, NhAiProposalGuardStages.Proposal, cancellationToken);
                if (check.Stale is { } stale)
                {
                    return NotNeeded(stale);
                }
            }

            var keys = await guards.GetKeysAsync(guardContext, cancellationToken);
            var duplicate = await services.GetRequiredService<NhAssistantProposalDuplicates>()
                .CheckAsync(thread, descriptor.Id, argumentsHash, keys, policy, cancellationToken);
            if (duplicate is { Outcome: not NhAssistantProposeOutcome.Created, Existing: { } existing })
            {
                return await SkipDuplicateAsync(admission, duplicate.Outcome, existing, cancellationToken);
            }

            var presented = await PresentAsync(admission, capture, cancellationToken);
            if (presented.Presentation is not null)
            {
                capture.Call.DisplayName = NhAssistantContent.Bound(presented.Presentation.ToolDisplayName, NhAssistantPresentationLimits.MaxDisplayNameLength);
            }
            else
            {
                capture.Call.DisplayName = await services.GetRequiredService<NhAssistantToolPresentationResolver>()
                    .ResolveDisplayNameAsync(descriptor, CultureInfo.CurrentUICulture, cancellationToken);
            }

            if (thread.ConversationId == Guid.Empty)
            {
                // Every check passed: the agent's thread at the subject is created now.
                var created = await services.GetRequiredService<NhAssistantThreadService>().GetOrCreateAsync(agent, subject, cancellationToken);
                if (!created.Success)
                {
                    return TaskResult<NhAssistantProposalResult>.Failed(created);
                }

                thread = created.Data!;
            }

            var parent = subject.Parent;
            var approval = new AssistantApproval
            {
                Id = approvalId,
                ConversationId = thread.ConversationId,
                TurnId = capture.Call.TurnId,
                ToolInvocationId = capture.Call.Id,
                ProposalId = proposal.ProposalId,
                ProposalHash = proposal.ProposalHash,
                ProposalJson = NhAssistantProposalSerializer.Serialize(proposal),
                ToolId = descriptor.Id,
                Summary = Summary,
                PresentationJson = NhAssistantToolPresentationResolver.Serialize(presented.Presentation),
                PresentationsJson = presented.LocalizedJson,
                ArgumentsPreview = NhAssistantContent.Preview(capture.Call.ArgumentsJson, capture.Call.DataClassification) ?? "{}",
                TargetsJson = JsonSerializer.Serialize(
                    proposal.Targets.Select(target => $"{target.Type}:{target.Id}").ToArray(),
                    NhAssistantContent.JsonOptions),
                Status = NhAssistantApprovalStatuses.Pending,
                CreatedAt = now,
                ExpiresAt = expiresAt,
                RunId = null,
                PlacedFromConversationId = null,
                PolicyId = policy.Id,
                // Deferred only: nobody waits for the decision.
                Mode = NhAssistantApprovalModes.Deferred,
                PolicyJson = NhAssistantApprovalPolicySnapshot.Serialize(policy),
                // The person is the initiator, so four eyes keep them out.
                InitiatorActorId = _person.ActorId,
                SubjectVersion = subject.Version,
                ParentSubjectType = parent?.Subject.Type,
                ParentSubjectId = parent?.Subject.Id,
                ParentDisplayName = parent?.DisplayName is { } parentName
                    ? NhAssistantContent.Bound(parentName, NhAiSubjectParent.MaxDisplayNameLength)
                    : null,
                GrantSource = context.GrantSource,
                NextReminderAt = NhAssistantApprovalReminders.Next(policy, proposedAt, 0, expiresAt),
                ArgumentsHash = argumentsHash,
                ConcurrencyStamp = Guid.NewGuid()
            };
            capture.Call.ConversationId = thread.ConversationId;
            capture.Call.ProposalId = proposal.ProposalId;
            capture.Call.ApprovalId = approval.Id;
            capture.Call.GrantSource = approval.GrantSource;
            var keyRows = keys
                .Select(item => new AssistantApprovalKey
                {
                    ApprovalId = approval.Id,
                    ScopeKey = thread.ScopeKey,
                    KeyType = item.Type,
                    KeyId = item.Id
                })
                .ToArray();
            try
            {
                await _store.AddApprovalAsync(approval, keyRows, capture.Call, cancellationToken);
            }
            catch (DbUpdateException) when (key is not null)
            {
                // A call that raced with the same key recorded the proposal first; nothing was saved here.
                if (await _store.FindApprovalAsync(approval.Id, CancellationToken.None) is { } raced)
                {
                    return await RepeatAsync(raced, admission, argumentsHash, CancellationToken.None);
                }

                throw;
            }

            var superseded = await SupersedeAsync(duplicate.Superseded, thread, approval, agent, CancellationToken.None);
            await AuditAsync(approval, thread, agent, descriptor, CancellationToken.None);
            await services.GetRequiredService<NhAssistantAgentWorkUpdates>().PublishAsync(
                thread.ConversationId,
                NhAssistantAgentWorkReasons.Proposal,
                approval.Id,
                null,
                CancellationToken.None);
            // The approvers hear about it as about the proposals a run leaves; a failed notification
            // never fails the proposal.
            await services.GetRequiredService<NhAssistantAgentNotifier>().NotifyAsync(
                NhAssistantAgentNotificationKind.RunEndedWithProposals,
                thread,
                null,
                approval,
                _person.ActorId,
                false,
                CancellationToken.None,
                proposalCount: 1);

            return TaskResult<NhAssistantProposalResult>.Succeeded(ResultOf(NhAssistantProposeOutcome.Created, approval, policy) with
            {
                Superseded = superseded
            });
        }

        /// <summary>
        /// The policy the application named or code resolves for the agent and the tool, then the
        /// application's selector (FB19), which may choose another registered one; it fails closed.
        /// </summary>
        private async Task<TaskResult<NhAiApprovalPolicy>> ChoosePolicyAsync(
            Admission admission,
            Capture capture,
            CancellationToken cancellationToken)
        {
            var policies = services.GetRequiredService<NhAssistantApprovalPolicies>();
            NhAiApprovalPolicy resolved;
            if (request.PolicyId is { } named)
            {
                if (policies.Find(named) is not { } registered)
                {
                    return TaskResult<NhAiApprovalPolicy>.Failed(NhAssistantErrorCodes.ApprovalPolicyUnknown, "The approval policy is not registered.");
                }

                resolved = registered;
            }
            else
            {
                resolved = policies.Resolve(admission.Agent.Id, admission.Descriptor.Id, null, isRun: true);
            }

            var choice = await policies.SelectAsync(
                resolved,
                new NhAssistantApprovalPolicySelection(
                    admission.Agent.Id,
                    admission.Descriptor.Id,
                    admission.Subject.Subject,
                    capture.Arguments,
                    resolved.Id)
                {
                    Source = NhAssistantApprovalPolicySource.Application,
                    InitiatorActorId = _person.ActorId,
                    TenantId = admission.Subject.TenantId
                },
                cancellationToken);
            return choice.Refused
                ? TaskResult<NhAiApprovalPolicy>.Failed(choice.RefusalCode!, "The approval policy of the proposal could not be chosen.")
                : TaskResult<NhAiApprovalPolicy>.Succeeded(choice.Policy!);
        }

        /// <summary>
        /// The application's prepared presentations, the one of the culture the proposal is made in first;
        /// without them the application's tool presenters present it in every supported UI culture.
        /// </summary>
        private async Task<NhAssistantLocalizedApprovalPresentation> PresentAsync(
            Admission admission,
            Capture capture,
            CancellationToken cancellationToken)
        {
            if (presentations.Count == 0)
            {
                return await services.GetRequiredService<NhAssistantToolPresentationResolver>().ResolveApprovalInCulturesAsync(
                    admission.Descriptor,
                    capture.Input,
                    capture.Context,
                    CultureInfo.CurrentUICulture,
                    cancellationToken);
            }

            var primary = PrimaryOf(presentations, CultureInfo.CurrentUICulture);
            return new NhAssistantLocalizedApprovalPresentation(
                primary,
                presentations.Count > 1 ? JsonSerializer.Serialize(presentations, NhAssistantContent.JsonOptions) : null);
        }

        /// <summary>
        /// The presentation of <paramref name="culture"/>, a parent culture or another culture of its language,
        /// otherwise the first one the application gave.
        /// </summary>
        private static NhAssistantApprovalPresentation PrimaryOf(
            IReadOnlyDictionary<string, NhAssistantApprovalPresentation> localized,
            CultureInfo culture)
        {
            for (var candidate = culture; !string.IsNullOrEmpty(candidate.Name); candidate = candidate.Parent)
            {
                if (localized.TryGetValue(candidate.Name, out var exact))
                {
                    return exact;
                }
            }

            foreach (var (name, presentation) in localized)
            {
                if (string.Equals(CultureInfo.GetCultureInfo(name).TwoLetterISOLanguageName, culture.TwoLetterISOLanguageName, StringComparison.OrdinalIgnoreCase))
                {
                    return presentation;
                }
            }

            return localized.Values.First();
        }

        /// <summary>
        /// The proposal the idempotency key recorded before: the same person's proposal of the same agent,
        /// subject, tool and arguments is returned as it is now; anything else is a conflict.
        /// </summary>
        private async Task<TaskResult<NhAssistantProposalResult>> RepeatAsync(
            AssistantApproval earlier,
            Admission admission,
            string argumentsHash,
            CancellationToken cancellationToken)
        {
            var thread = await _runs.FindThreadByConversationAsync(earlier.ConversationId, cancellationToken);
            if (thread is null
                || !NhAssistantPersonProposals.IsPersonProposal(earlier)
                || !string.Equals(thread.AgentId, admission.Agent.Id, StringComparison.Ordinal)
                || !string.Equals(thread.SubjectType, admission.Subject.Subject.Type, StringComparison.Ordinal)
                || !string.Equals(thread.SubjectId, admission.Subject.Subject.Id, StringComparison.Ordinal)
                || !string.Equals(earlier.ToolId, admission.Descriptor.Id, StringComparison.Ordinal)
                || !string.Equals(earlier.ArgumentsHash, argumentsHash, StringComparison.Ordinal)
                || !string.Equals(earlier.InitiatorActorId, _person.ActorId, StringComparison.Ordinal))
            {
                return Failed(NhAssistantErrorCodes.ProposalIdempotencyConflict, "The idempotency key recorded another proposal.");
            }

            var policy = earlier.PolicyJson is null ? null : NhAssistantApprovalPolicySnapshot.Deserialize(earlier.PolicyJson);
            return TaskResult<NhAssistantProposalResult>.Succeeded(ResultOf(NhAssistantProposeOutcome.Created, earlier, policy) with
            {
                Repeated = true
            });
        }

        /// <summary>
        /// Nothing is recorded because the same proposal is open or was rejected recently; the audit names
        /// the existing proposal, as for a run.
        /// </summary>
        private async Task<TaskResult<NhAssistantProposalResult>> SkipDuplicateAsync(
            Admission admission,
            NhAssistantProposeOutcome outcome,
            AssistantApproval existing,
            CancellationToken cancellationToken)
        {
            var code = outcome == NhAssistantProposeOutcome.RecentlyRejected
                ? NhAssistantErrorCodes.ProposalRecentlyRejected
                : NhAssistantErrorCodes.ProposalAlreadyOpen;
            await services.GetRequiredService<NhAssistantAgentEvents>().DeduplicatedAsync(
                existing,
                admission.Thread,
                _person.ActorId,
                code,
                cancellationToken);
            var policy = existing.PolicyJson is null ? null : NhAssistantApprovalPolicySnapshot.Deserialize(existing.PolicyJson);
            return TaskResult<NhAssistantProposalResult>.Succeeded(ResultOf(outcome, existing, policy) with
            {
                RejectedAt = outcome == NhAssistantProposeOutcome.RecentlyRejected ? existing.DecidedAt : null
            });
        }

        /// <summary>
        /// Nothing is recorded because a guard found the proposal not needed; the result carries the guard's
        /// reason and its explanation in every supported UI culture.
        /// </summary>
        private TaskResult<NhAssistantProposalResult> NotNeeded(NhAiPreconditionResult stale)
        {
            return TaskResult<NhAssistantProposalResult>.Succeeded(new NhAssistantProposalResult(NhAssistantProposeOutcome.NotNeeded, null)
            {
                NotNeededReason = NhAssistantProposalGuards.ReasonCodeOf(stale),
                NotNeededKind = stale.Kind,
                Explanations = services.GetRequiredService<NhAssistantStaleExplanations>().RenderTexts(stale.Explain)
            });
        }

        /// <summary>
        /// Withdraws the pending proposals for the same objects that the new one replaces.
        /// </summary>
        private async Task<IReadOnlyList<Guid>> SupersedeAsync(
            IReadOnlyList<AssistantApproval> older,
            AssistantThread thread,
            AssistantApproval approval,
            NhAssistantAgent agent,
            CancellationToken cancellationToken)
        {
            var invalidation = services.GetRequiredService<NhAssistantProposalInvalidation>();
            var superseded = new List<Guid>(older.Count);
            foreach (var replaced in older)
            {
                if (await invalidation.SupersedeAsync(replaced, thread, approval.Id, agent.Definition.ActorId, cancellationToken) is not null)
                {
                    superseded.Add(replaced.Id);
                }
            }

            return superseded;
        }

        /// <summary>
        /// The content-free audit of the proposal: requested, as every proposal, and recorded for the person
        /// with the application's key.
        /// </summary>
        private async Task AuditAsync(
            AssistantApproval approval,
            AssistantThread thread,
            NhAssistantAgent agent,
            NhAiToolDescriptor descriptor,
            CancellationToken cancellationToken)
        {
            var sinks = services.GetServices<INhAssistantBusinessAuditSink>().ToArray();
            foreach (var kind in new[] { NhAssistantAuditEventKind.ApprovalRequested, NhAssistantAuditEventKind.AgentProposalRecordedForPerson })
            {
                var evt = new NhAssistantAuditEvent(
                    kind,
                    approval.ConversationId,
                    approval.TurnId,
                    _person.ActorId,
                    agent.Id,
                    agent.Definition.Version,
                    approval.CreatedAt)
                {
                    ToolId = descriptor.Id,
                    ToolVersion = descriptor.Version,
                    InvocationId = approval.ToolInvocationId,
                    ResultCode = kind == NhAssistantAuditEventKind.ApprovalRequested
                        ? NhAiToolFailureCodes.ApprovalRequired
                        : NhAssistantToolCallInterceptor.ProposalRecordedCode,
                    ApprovalId = approval.Id,
                    ProposalId = approval.ProposalId,
                    SubjectType = thread.SubjectType,
                    SubjectId = thread.SubjectId,
                    InitiatorActorId = _person.ActorId,
                    GrantSource = approval.GrantSource,
                    ApprovalPolicyId = approval.PolicyId,
                    IdempotencyKey = kind == NhAssistantAuditEventKind.AgentProposalRecordedForPerson ? request.IdempotencyKey : null
                };
                foreach (var sink in sinks)
                {
                    await sink.RecordAsync(evt, cancellationToken);
                }
            }
        }

        private static NhAssistantProposalResult ResultOf(
            NhAssistantProposeOutcome outcome,
            AssistantApproval approval,
            NhAiApprovalPolicy? policy)
        {
            return new NhAssistantProposalResult(outcome, approval.Id)
            {
                ConversationId = approval.ConversationId,
                Status = approval.Status,
                ProposalHash = approval.ProposalHash,
                PolicyId = approval.PolicyId,
                FourEyes = policy?.FourEyes == true,
                ExpiresAt = approval.ExpiresAt
            };
        }

        private static TaskResult<Admission> Refused(string code, string message)
        {
            return TaskResult<Admission>.Failed(code, message);
        }
    }

    /// <summary>
    /// The person, agent, subject, thread and tool a proposal is admitted for. The thread has an empty
    /// conversation id until it is created.
    /// </summary>
    private sealed record Admission(
        NhAiPersonInvocation Person,
        NhAssistantAgent Agent,
        NhAssistantAgentRunOptions RunOptions,
        NhAiSubjectInfo Subject,
        AssistantThread Thread,
        AIFunction Function,
        NhAiToolDescriptor Descriptor);

    /// <summary>
    /// What the governed call handed over: the waiting call, the evaluated input, its context and the input
    /// as the proposal records it.
    /// </summary>
    private sealed record Capture(
        AssistantToolInvocation Call,
        object Input,
        NhAiInvocationContext Context,
        JsonElement Arguments);
}
