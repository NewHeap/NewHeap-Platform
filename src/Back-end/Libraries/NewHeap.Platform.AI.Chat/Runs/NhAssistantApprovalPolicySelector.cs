using System.Text.Json;

namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// The application's choice of the approval policy of one proposal (FB19). The assistant resolves the
/// policy from code first (the trigger of the run, the skill, the tool within the agent, the agent, then
/// the default) and then asks the selector, with the agent, tool, skill, trigger, subject, arguments and
/// the policy code resolved. The selector returns the id of a policy registered with
/// <see cref="NhAssistantBuilder.AddApprovalPolicy"/>, or <see langword="null"/> to keep the resolved one;
/// for example a policy with four eyes only while a second person may approve, which it can ask
/// <c>INhAssistantApproverDirectory</c> of <c>NewHeap.Platform.AI.Chat.AspNet</c>.
/// </summary>
/// <remarks>
/// <para>
/// Register one selector with <see cref="NhAssistantBuilder.UseApprovalPolicySelector{TSelector}()"/>; it is
/// a scoped service. It is asked wherever a proposal is made through the governed path: the calls of an
/// agent's runs and rule runs, a person's messages in an agent's thread, a consultation's calls (also when
/// the person who asked leaves one for the subject's approvers), and the steps of compositions and scripts
/// in those turns. A person's own chat with the assistant, whose proposals that person decides without an
/// approval policy, does not ask it.
/// </para>
/// <para>
/// The choice is kept in the proposal's policy copy, so the decision, its reminders, expiry, escalation and
/// the duplicate and cooldown rules check the chosen policy, never a later change of the selector or of the
/// registration. What the call's route adds still applies to the chosen policy: a person's message in an
/// agent's thread decides inline, a consultation's proposal at the subject waits deferred, and one that
/// executes with the consulted agent's own rights is four-eyes.
/// </para>
/// <para>
/// A selector decides and changes nothing: it reads, has no side effects and answers within its time limit
/// (<see cref="NhAssistantApprovalPolicySelection.DefaultTimeLimit"/>, or the one passed to
/// <see cref="NhAssistantBuilder.UseApprovalPolicySelector{TSelector}(TimeSpan)"/>). It fails closed: an id
/// that is not registered refuses the proposal with <c>assistant-approval-policy-unknown</c>, a selector that
/// throws with <c>assistant-approval-policy-selector-failed</c> and one that does not answer in time with
/// <c>assistant-approval-policy-selector-timed-out</c>. Nothing is recorded then; the model, the rules or the
/// step read the code, and the log names the selector's type and, for an exception, its type only.
/// </para>
/// </remarks>
public interface INhAssistantApprovalPolicySelector
{
    /// <summary>
    /// The id of the registered approval policy the proposal that <paramref name="selection"/> describes
    /// waits under, or <see langword="null"/> (or <see cref="NhAssistantApprovalPolicySelection.ResolvedPolicyId"/>)
    /// to keep the policy code resolved.
    /// </summary>
    /// <param name="selection">The proposal the policy is chosen for.</param>
    /// <param name="cancellationToken">Cancelled when the time limit passes or the turn ends.</param>
    ValueTask<string?> SelectAsync(
        NhAssistantApprovalPolicySelection selection,
        CancellationToken cancellationToken);
}

/// <summary>
/// Where a proposal whose approval policy is chosen comes from.
/// </summary>
public enum NhAssistantApprovalPolicySource
{
    /// <summary>
    /// A run of the agent that a trigger, a schedule, a webhook, a person's start, a hand-off or a
    /// follow-up began, with a model or with rules.
    /// </summary>
    Run = 0,

    /// <summary>
    /// A person's message in the agent's thread. Its proposal is decided inline whatever the policy's mode.
    /// </summary>
    Request = 1,

    /// <summary>
    /// A consultation: another agent asked the agent a question on behalf of a person, who is the
    /// initiator. Also the proposal the person who asked leaves for the subject's approvers.
    /// </summary>
    Consultation = 2
}

/// <summary>
/// The proposal an <see cref="INhAssistantApprovalPolicySelector"/> chooses the approval policy of. It
/// carries no prompt or result content; the arguments are the ones the proposal records and approvers see.
/// </summary>
/// <param name="AgentId">The agent that proposes.</param>
/// <param name="ToolId">The tool whose call is proposed.</param>
/// <param name="Subject">The subject the proposal waits at: the thread's subject, or the object a run
/// placed it at.</param>
/// <param name="Arguments">The call's arguments as the proposal records them.</param>
/// <param name="ResolvedPolicyId">The policy code resolved, such as the agent's policy or
/// <c>trigger-default</c>.</param>
public sealed record NhAssistantApprovalPolicySelection(
    string AgentId,
    string ToolId,
    NhAiSubjectRef Subject,
    JsonElement Arguments,
    string ResolvedPolicyId)
{
    /// <summary>
    /// The time limit of a selector registered without one.
    /// </summary>
    public static readonly TimeSpan DefaultTimeLimit = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The longest time limit a selector may have.
    /// </summary>
    public static readonly TimeSpan MaxTimeLimit = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Where the proposal comes from.
    /// </summary>
    public NhAssistantApprovalPolicySource Source { get; init; }

    /// <summary>
    /// The skill the run works in, or <see langword="null"/> outside a skill.
    /// </summary>
    public string? SkillId { get; init; }

    /// <summary>
    /// The trigger that started the run, such as an event trigger's id or <c>manual</c>;
    /// <see langword="null"/> outside a run.
    /// </summary>
    public string? TriggerId { get; init; }

    /// <summary>
    /// The person the proposal is for: the initiator of the run, the person who typed or the person who
    /// asked in a consultation. <see langword="null"/> for a run nobody started. Under four eyes this
    /// person may not approve.
    /// </summary>
    public string? InitiatorActorId { get; init; }

    /// <summary>
    /// The tenant of the subject, as its resolver described it, or <see langword="null"/>.
    /// </summary>
    public string? TenantId { get; init; }

    /// <summary>
    /// The run that proposes, or <see langword="null"/> outside a run.
    /// </summary>
    public Guid? RunId { get; init; }

    /// <summary>
    /// The composition or script whose step the call is (<see cref="NhAiToolCallOrigin"/>), or
    /// <see langword="null"/> for a call the model or the rules made themselves.
    /// </summary>
    public NhAiToolCallOrigin? Origin { get; init; }
}
