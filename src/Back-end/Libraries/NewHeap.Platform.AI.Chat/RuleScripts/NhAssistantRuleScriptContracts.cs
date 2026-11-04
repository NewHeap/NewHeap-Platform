using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// The administration of rule scripts (RB-1 to RB-6): the one service the administration's endpoints and
/// the agent builder's tools call, behind the administration policy. A rule script belongs to an agent
/// (<c>skillId</c> <see langword="null"/>) or to one of its skills, within the code's ceiling
/// (<see cref="NhAssistantBuilder.UseRuleScriptsAsData"/>). Its versions are append-only: a version is
/// saved as a candidate or straight live; a candidate may run in shadow, go live or be discarded; a live
/// version that another replaces is superseded. Every change expects the rule script's current revision
/// (<c>expectedRevision</c>, 0 before the first version) and makes the next one, and is audited content-free
/// (<see cref="NhAssistantAuditEventKind.AdminRuleScriptSaved"/> to
/// <see cref="NhAssistantAuditEventKind.AdminRuleScriptDryRun"/>). A dry run runs a version on chosen
/// subjects without effects and is kept as the version's latest; going live returns it, or says there is
/// none. Expected refusals are failed results with the codes of <see cref="NhAssistantRuleScriptCodes"/>,
/// <c>assistant-version-conflict</c>, <c>assistant-agent-not-found</c>, <c>assistant-skill-not-found</c>
/// and field errors.
/// </summary>
public interface INhAssistantRuleScripts
{
    /// <summary>
    /// The rule script of the agent's own rules or of one skill, with every version, newest first, and the
    /// ceiling; <see langword="null"/> when the agent or skill does not exist.
    /// </summary>
    Task<NhAssistantRuleScriptDto?> GetAsync(string agentId, string? skillId, CancellationToken cancellationToken = default);

    /// <summary>
    /// What a script of the agent or skill sees: the tools its rules may use with their signatures, which
    /// are writes, which the ceiling allows and which a version with <paramref name="writes"/> may call;
    /// the alert kinds the agent lists, the platform functions and the limits. The tools are those the
    /// agent's selectors name with its autonomy as a rule turn has it; whether a run may call each one
    /// still follows the run's grants.
    /// </summary>
    Task<TaskResult<NhAssistantRuleScriptCatalogDto>> GetCatalogAsync(
        string agentId,
        string? skillId,
        IReadOnlyCollection<string>? writes = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether <paramref name="script"/> parses and fits the length, with the line and column of a syntax
    /// error; nothing runs and nothing is saved.
    /// </summary>
    Task<TaskResult<NhAssistantRuleScriptCheckDto>> CheckAsync(
        string agentId,
        string? skillId,
        string script,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves the next version: a candidate, which may run in shadow at once
    /// (<see cref="NhAssistantRuleScriptSaveRequest.Shadow"/>), or straight live
    /// (<see cref="NhAssistantRuleScriptSaveRequest.Live"/>, RB-3), which supersedes the live version. A
    /// newer candidate discards the older one. The script must parse (field error <c>script</c>) and the
    /// writes stay within the ceiling (<c>assistant-rule-script-write-not-allowed</c>). Live under four eyes
    /// is refused (<c>assistant-rule-script-four-eyes</c>): another administrator sets it live.
    /// </summary>
    Task<TaskResult<NhAssistantRuleScriptVersionDto>> SaveAsync(
        string agentId,
        string? skillId,
        NhAssistantRuleScriptSaveRequest request,
        NhAssistantRuleScriptAuthor author,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs <paramref name="version"/> on the chosen subjects without effects (RB-5): reads run with the
    /// rights a run of the agent on the subject has; writes, proposals and alerts are recorded with what
    /// would become of them and never happen, and nothing is delivered. The record is kept as the version's
    /// latest dry run and returned.
    /// </summary>
    Task<TaskResult<NhAssistantRuleScriptRunRecordDto>> DryRunAsync(
        string agentId,
        string? skillId,
        int version,
        NhAssistantRuleScriptDryRunRequest request,
        NhAssistantRuleScriptAuthor actor,
        CancellationToken cancellationToken = default);

    /// <summary>The latest dry run of the version; <see langword="null"/> when it has none.</summary>
    Task<NhAssistantRuleScriptRunRecordDto?> GetDryRunAsync(
        string agentId,
        string? skillId,
        int version,
        CancellationToken cancellationToken = default);

    /// <summary>The newest shadow runs of the version, newest first.</summary>
    Task<IReadOnlyList<NhAssistantRuleScriptRunRecordDto>> GetShadowRunsAsync(
        string agentId,
        string? skillId,
        int version,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the candidate <paramref name="version"/> live: the live version is superseded and the next rule
    /// turn runs it. Returns it with its latest dry run, or none. Under four eyes the person who saved it
    /// is refused (<c>assistant-rule-script-four-eyes</c>); a version that is no candidate
    /// <c>assistant-rule-script-not-candidate</c>.
    /// </summary>
    Task<TaskResult<NhAssistantRuleScriptLiveDto>> SetLiveAsync(
        string agentId,
        string? skillId,
        int version,
        NhAssistantRuleScriptChangeRequest request,
        NhAssistantRuleScriptAuthor actor,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lets the candidate <paramref name="version"/> run in shadow next to the live rules, or stop.
    /// </summary>
    Task<TaskResult<NhAssistantRuleScriptVersionDto>> SetShadowAsync(
        string agentId,
        string? skillId,
        int version,
        NhAssistantRuleScriptShadowRequest request,
        NhAssistantRuleScriptAuthor actor,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Discards the candidate <paramref name="version"/>; it stays as history. The live version cannot be
    /// discarded: another version replaces it.
    /// </summary>
    Task<TaskResult<NhAssistantRuleScriptVersionDto>> DiscardAsync(
        string agentId,
        string? skillId,
        int version,
        NhAssistantRuleScriptChangeRequest request,
        NhAssistantRuleScriptAuthor actor,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Compares <paramref name="version"/> with <paramref name="otherVersion"/>, by default the live version
    /// or else the one before: the script line by line, the writes and their latest dry runs.
    /// </summary>
    Task<TaskResult<NhAssistantRuleScriptComparisonDto>> CompareAsync(
        string agentId,
        string? skillId,
        int version,
        int? otherVersion,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The stable codes of rule scripts, in failed results of <see cref="INhAssistantRuleScripts"/> and in the
/// error table of the assistant's endpoints.
/// </summary>
public static class NhAssistantRuleScriptCodes
{
    /// <summary>
    /// The code's ceiling (<see cref="NhAssistantBuilder.UseRuleScriptsAsData"/>) does not let the agent have
    /// rule scripts. Nothing was saved or run.
    /// </summary>
    public const string NotAllowed = "assistant-rule-scripts-not-allowed";

    /// <summary>
    /// A version names a write the ceiling does not allow for the agent, or a tool that changes nothing;
    /// the field errors name <c>writes</c>. Nothing was saved.
    /// </summary>
    public const string WriteNotAllowed = "assistant-rule-script-write-not-allowed";

    /// <summary>The rule script has no such version.</summary>
    public const string VersionNotFound = "assistant-rule-script-version-not-found";

    /// <summary>
    /// The version is no candidate: only a candidate goes live, runs in shadow or is discarded.
    /// </summary>
    public const string NotCandidate = "assistant-rule-script-not-candidate";

    /// <summary>
    /// The ceiling requires four eyes: the person who saved the version, also through the agent builder,
    /// cannot set it live. Another administrator does.
    /// </summary>
    public const string FourEyes = "assistant-rule-script-four-eyes";

    /// <summary>
    /// A dry run names no subject, more than <see cref="NhAssistantRuleScriptLimits.MaxDryRunSubjects"/> or
    /// an invalid one; the field errors name <c>subjects</c>.
    /// </summary>
    public const string SubjectsInvalid = "assistant-rule-script-subjects-invalid";

    /// <summary>
    /// What a write of a script returns in shadow and in a dry run: it was recorded and did not happen.
    /// </summary>
    public const string DryRunWrite = "assistant-rule-script-dry-run";
}

/// <summary>
/// What a write of a script in shadow or in a dry run would have become.
/// </summary>
public static class NhAssistantRuleScriptWouldOutcomes
{
    /// <summary>The tool needs no approval: it would run.</summary>
    public const string Run = "would-run";

    /// <summary>The tool needs an approval, or the agent only proposes: it would become a proposal.</summary>
    public const string Propose = "would-propose";

    /// <summary>The approval policy decides at the call whether it runs or becomes a proposal.</summary>
    public const string Decide = "would-decide";
}

/// <summary>
/// The rule script of an agent or skill.
/// </summary>
/// <param name="SkillId">The skill; <see langword="null"/> for the agent's own rules.</param>
/// <param name="Allowed">Whether the ceiling lets the agent have rule scripts; without it nothing can be saved.</param>
/// <param name="Revision">The revision every change expects, 0 before the first version.</param>
/// <param name="LiveVersion">The version in effect.</param>
/// <param name="CandidateVersion">The version that waits to go live.</param>
/// <param name="CandidateShadow">Whether the candidate runs in shadow next to the live rules.</param>
/// <param name="CodeRule">The code rule of the agent or skill, by its handler's name, which a live version
/// takes the place of; <see langword="null"/> without one.</param>
/// <param name="Versions">Every version, newest first.</param>
public sealed record NhAssistantRuleScriptDto(
    string AgentId,
    string? SkillId,
    bool Allowed,
    int Revision,
    int? LiveVersion,
    int? CandidateVersion,
    bool CandidateShadow,
    string? CodeRule,
    NhAssistantRuleScriptCeilingDto Ceiling,
    IReadOnlyList<NhAssistantRuleScriptVersionDto> Versions);

/// <summary>
/// What the code's ceiling allows the agent's rule scripts.
/// </summary>
public sealed record NhAssistantRuleScriptCeilingDto(
    IReadOnlyList<string> Writes,
    int MaxWritesPerRun,
    IReadOnlyList<string> Destructive,
    bool RequireFourEyes);

/// <summary>
/// One version of a rule script.
/// </summary>
/// <param name="Status"><c>live</c>, <c>candidate</c>, <c>superseded</c> or <c>discarded</c>.</param>
/// <param name="Writes">The writing tools the version may call, by id.</param>
/// <param name="AuthorActorId">The person who wrote it.</param>
/// <param name="AuthorAgentId">The agent that wrote it for the person, such as the agent builder.</param>
/// <param name="Shadow">Whether it is the candidate and runs in shadow.</param>
/// <param name="Revision">The rule script's revision now, which the next change expects.</param>
public sealed record NhAssistantRuleScriptVersionDto(
    int Version,
    string Status,
    string Script,
    string ScriptHash,
    IReadOnlyList<string> Writes,
    string AuthorActorId,
    string? AuthorAgentId,
    string? Reason,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LiveAt,
    string? LiveBy,
    DateTimeOffset? EndedAt,
    string? EndedBy,
    bool Shadow,
    int Revision);

/// <summary>
/// A new version of a rule script.
/// </summary>
/// <param name="Script">The body of an async JavaScript function, at most <see cref="NhAssistantRuleScriptLimits.MaxScriptLength"/> characters.</param>
/// <param name="Writes">The writing tools it may call, by id, within the ceiling; empty or <see langword="null"/> for none.</param>
/// <param name="ExpectedRevision">The rule script's revision, 0 or <see langword="null"/> before the first version.</param>
/// <param name="Reason">Why, at most 500 characters; kept with the version and left out of the audit.</param>
/// <param name="Live">Save it straight live (RB-3) instead of as a candidate.</param>
/// <param name="Shadow">Let the candidate run in shadow at once.</param>
public sealed record NhAssistantRuleScriptSaveRequest(
    string? Script,
    IReadOnlyList<string>? Writes,
    int? ExpectedRevision,
    string? Reason = null,
    bool Live = false,
    bool Shadow = false);

/// <summary>
/// A change of a version's status.
/// </summary>
public sealed record NhAssistantRuleScriptChangeRequest(int? ExpectedRevision, string? Reason = null);

/// <summary>
/// Lets a candidate run in shadow, or stop.
/// </summary>
public sealed record NhAssistantRuleScriptShadowRequest(int? ExpectedRevision, bool Shadow, string? Reason = null);

/// <summary>
/// The subjects a dry run runs a version on, at most <see cref="NhAssistantRuleScriptLimits.MaxDryRunSubjects"/>;
/// <c>nh-system</c>/<c>global</c> runs it on the whole application.
/// </summary>
public sealed record NhAssistantRuleScriptDryRunRequest(IReadOnlyList<NhAssistantRuleScriptSubjectDto>? Subjects);

/// <summary>
/// A subject by type and id.
/// </summary>
public sealed record NhAssistantRuleScriptSubjectDto(string Type, string Id);

/// <summary>
/// A version that went live, with its latest dry run, or none.
/// </summary>
public sealed record NhAssistantRuleScriptLiveDto(
    NhAssistantRuleScriptVersionDto Version,
    NhAssistantRuleScriptRunRecordDto? LatestDryRun);

/// <summary>
/// Whether a script parses and fits the length.
/// </summary>
/// <param name="Code"><c>ai-script-syntax-error</c> or <c>ai-script-too-long</c> when it does not.</param>
public sealed record NhAssistantRuleScriptCheckDto(bool Valid, string? Code, string? Message, int? Line, int? Column);

/// <summary>
/// What a rule script of the agent or skill sees.
/// </summary>
/// <param name="Tools">The tools the agent's rules may use, by function name.</param>
/// <param name="AlertKinds">The alert kinds the agent lists: with <c>Alerts(...)</c> in code, or in the
/// run settings of an agent created in the administration.</param>
/// <param name="PlatformFunctions">The TypeScript-style declarations of <c>subject</c>, <c>inputs</c>,
/// <c>run</c>, <c>alerts</c>, <c>propose</c>, <c>done</c>, <c>handOver</c> and <c>useSkill</c>.</param>
public sealed record NhAssistantRuleScriptCatalogDto(
    string AgentId,
    string? SkillId,
    IReadOnlyList<NhAssistantRuleScriptToolDto> Tools,
    IReadOnlyList<string> AlertKinds,
    string PlatformFunctions,
    int MaxToolCalls,
    double TimeoutSeconds,
    int MaxScriptLength,
    int MaxWritesPerRun);

/// <summary>
/// One tool of a rule script.
/// </summary>
/// <param name="Name">The function name, the property of <c>tools</c>.</param>
/// <param name="Write">Whether it changes something.</param>
/// <param name="InCeiling">For a write, whether the ceiling lets a version opt in to it.</param>
/// <param name="Callable">Whether a version with the asked writes may call it.</param>
/// <param name="Destructive">Whether it is destructive.</param>
/// <param name="Signature">Its TypeScript-style declaration.</param>
/// <param name="Refusal">Why such a version may not call it.</param>
public sealed record NhAssistantRuleScriptToolDto(
    string Name,
    string ToolId,
    bool Write,
    bool InCeiling,
    bool Callable,
    bool Destructive,
    string Signature,
    string? Refusal);

/// <summary>
/// A dry run or a shadow run of a version: what it would do per subject.
/// </summary>
/// <param name="Kind"><c>dry-run</c> or <c>shadow</c>.</param>
/// <param name="RunId">The run a shadow run went along with.</param>
/// <param name="RanBy">Who started a dry run; the agent's actor for a shadow run.</param>
public sealed record NhAssistantRuleScriptRunRecordDto(
    Guid Id,
    string AgentId,
    string? SkillId,
    int Version,
    string Kind,
    Guid? RunId,
    string RanBy,
    DateTimeOffset RanAt,
    int SubjectCount,
    int CompletedCount,
    IReadOnlyList<NhAssistantRuleScriptSubjectRunDto> Subjects);

/// <summary>
/// What a version would do on one subject.
/// </summary>
/// <param name="Status"><c>completed</c>, <c>stopped</c> (with the script's code) or <c>failed</c> (the
/// subject could not be read, with the assistant's code).</param>
/// <param name="End">The end the script chose.</param>
/// <param name="Calls">Every tool call in order, with its outcome.</param>
/// <param name="Writes">The writes, with what would have become of them, and their input.</param>
/// <param name="Proposals">What it would propose.</param>
/// <param name="Alerts">The alerts it would raise.</param>
/// <param name="Resolves">The alerts it would resolve.</param>
public sealed record NhAssistantRuleScriptSubjectRunDto(
    string SubjectType,
    string SubjectId,
    string? DisplayName,
    string Status,
    string? Code,
    string? Message,
    int? Line,
    int? Column,
    NhAssistantRuleScriptEndDto? End,
    IReadOnlyList<NhAssistantRuleScriptCallDto> Calls,
    IReadOnlyList<NhAssistantRuleScriptWriteDto> Writes,
    IReadOnlyList<NhAssistantRuleScriptProposalDto> Proposals,
    IReadOnlyList<NhAssistantRuleScriptAlertDto> Alerts,
    IReadOnlyList<NhAssistantRuleScriptAlertKeyDto> Resolves,
    IReadOnlyList<string> Logs);

/// <summary>
/// The end a script chose: <c>done</c>, <c>hand-over</c> or <c>use-skill</c>.
/// </summary>
public sealed record NhAssistantRuleScriptEndDto(string Kind, string Text, string? SkillId);

/// <summary>
/// One tool call of a script, as its outcome names it.
/// </summary>
public sealed record NhAssistantRuleScriptCallDto(int Call, string Tool, string ToolId, string Outcome, string? Code, bool Write);

/// <summary>
/// A write a script would make in shadow or in a dry run.
/// </summary>
/// <param name="Outcome"><c>would-run</c>, <c>would-propose</c> or <c>would-decide</c> (<see cref="NhAssistantRuleScriptWouldOutcomes"/>).</param>
/// <param name="Input">The input as JSON, bounded.</param>
public sealed record NhAssistantRuleScriptWriteDto(int Call, string Tool, string ToolId, string Outcome, string Input);

/// <summary>
/// A proposal a script would record in shadow or in a dry run.
/// </summary>
public sealed record NhAssistantRuleScriptProposalDto(string Tool, string? ToolId, string Input, string? Rationale);

/// <summary>
/// An alert a script raises or would raise.
/// </summary>
public sealed record NhAssistantRuleScriptAlertDto(
    string Kind,
    string SubjectType,
    string SubjectId,
    string Key,
    string? Urgency,
    IReadOnlyDictionary<string, string> Parameters,
    IReadOnlyList<string> Recipients,
    DateTimeOffset? ExpiresAt);

/// <summary>
/// An alert a script resolves or would resolve; <see cref="Key"/> <see langword="null"/> for every key.
/// </summary>
public sealed record NhAssistantRuleScriptAlertKeyDto(string Kind, string SubjectType, string SubjectId, string? Key);

/// <summary>
/// Two versions compared.
/// </summary>
/// <param name="Lines">The script of <paramref name="OtherVersion"/> turned into the script of
/// <paramref name="Version"/>, line by line.</param>
public sealed record NhAssistantRuleScriptComparisonDto(
    int Version,
    int OtherVersion,
    IReadOnlyList<NhAssistantRuleScriptLineDto> Lines,
    IReadOnlyList<string> WritesAdded,
    IReadOnlyList<string> WritesRemoved,
    NhAssistantRuleScriptRunRecordDto? DryRun,
    NhAssistantRuleScriptRunRecordDto? OtherDryRun);

/// <summary>
/// One line of a comparison.
/// </summary>
/// <param name="Change"><c>same</c>, <c>added</c> or <c>removed</c>.</param>
/// <param name="Line">Its number in the compared version, from 1.</param>
/// <param name="OtherLine">Its number in the other version.</param>
public sealed record NhAssistantRuleScriptLineDto(string Change, int? Line, int? OtherLine, string Text);
