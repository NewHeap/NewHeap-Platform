using System.Text.Json;

namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// The engine that runs rule scripts (RB-1): a script stored as data that takes the place of a code rule of
/// an agent or skill and runs from its triggers without a model. <c>NewHeap.Platform.AI.Scripting</c>
/// implements and registers it with <c>AddNewHeapPlatformAIScripting</c>, on the same sandbox, limits,
/// signatures and codes as the script tool; the assistant only calls it. An application that uses
/// <see cref="NhAssistantBuilder.UseRuleScriptsAsData"/> without it fails at startup.
/// </summary>
public interface INhAssistantRuleScriptEngine
{
    /// <summary>
    /// Why <paramref name="script"/> cannot run as a rule script of <paramref name="agentId"/>: too long
    /// (<c>ai-script-too-long</c>) or it does not parse (<c>ai-script-syntax-error</c>, with its line and
    /// column); <see langword="null"/> when it may run. Nothing runs.
    /// </summary>
    NhAssistantRuleScriptProblem? Check(string agentId, string script);

    /// <summary>
    /// What a rule script of <paramref name="agentId"/> sees: the signature of each of
    /// <paramref name="tools"/>, whether a version that opts in to <paramref name="writes"/> may call it,
    /// the platform functions and the limits, for an editor or the agent builder.
    /// </summary>
    NhAssistantRuleScriptDeclarations Describe(
        string agentId,
        IReadOnlyList<NhAiToolDescriptor> tools,
        IReadOnlySet<string> writes);

    /// <summary>
    /// Runs the script of <paramref name="run"/>. Expected outcomes, such as a syntax error or a limit, are
    /// a stopped outcome with the script's code; cancellation throws.
    /// </summary>
    Task<NhAssistantRuleScriptOutcome> RunAsync(NhAssistantRuleScriptRun run, CancellationToken cancellationToken);
}

/// <summary>
/// How a rule script runs.
/// </summary>
public enum NhAssistantRuleScriptMode
{
    /// <summary>The live version in a rule turn: its reads and writes run, and its outcome ends the run.</summary>
    Live = 0,

    /// <summary>
    /// A candidate next to the live rules of the same run: its reads run, nothing it would write, propose
    /// or raise takes effect, and the platform records what it would do.
    /// </summary>
    Shadow = 1,

    /// <summary>
    /// A dry run an administrator or the agent builder started on chosen subjects (RB-5): as
    /// <see cref="Shadow"/>, outside a run.
    /// </summary>
    DryRun = 2
}

/// <summary>
/// One run of a rule script as the assistant hands it to the engine: the version, the run's subject,
/// inputs and clock, the tools the run may use with the writes the version may call, and the
/// platform's way to propose (<see cref="ProposeAsync"/>). The steps (<see cref="Steps"/>) run every call
/// with the run's grants and accountability; in shadow and in a dry run a write never reaches its tool.
/// </summary>
public sealed class NhAssistantRuleScriptRun
{
    private static readonly IReadOnlySet<string> Nothing = new HashSet<string>(StringComparer.Ordinal);

    private readonly Func<string, JsonElement, string?, CancellationToken, Task<NhAssistantRuleProposal>> _propose;

    /// <param name="propose">
    /// Records a proposal of a tool, by function name or id, with its input and rationale, through the
    /// same path as <see cref="NhAssistantRuleContext.ProposeAsync{TInput}"/>; in shadow and in a dry run
    /// it records nothing.
    /// </param>
    public NhAssistantRuleScriptRun(Func<string, JsonElement, string?, CancellationToken, Task<NhAssistantRuleProposal>> propose)
    {
        ArgumentNullException.ThrowIfNull(propose);
        _propose = propose;
    }

    public required string AgentId { get; init; }

    /// <summary>The skill whose rules the script is; <see langword="null"/> for the agent's own rules.</summary>
    public string? SkillId { get; init; }

    /// <summary>The version of the rule script, from 1.</summary>
    public required int Version { get; init; }

    public required string Script { get; init; }

    public required NhAssistantRuleScriptMode Mode { get; init; }

    /// <summary>The subject the run works on, as its resolver describes it now; the whole application for an application-wide run.</summary>
    public required NhAiSubjectInfo Subject { get; init; }

    /// <summary>The trigger that started the run, such as a schedule; <c>dry-run</c> for a dry run.</summary>
    public required string TriggerId { get; init; }

    /// <summary>The run; <see langword="null"/> for a dry run.</summary>
    public Guid? RunId { get; init; }

    /// <summary>The application's clock when the run started.</summary>
    public required DateTimeOffset Now { get; init; }

    /// <summary>The run's inputs, oldest first: events, schedule ticks and messages.</summary>
    public IReadOnlyList<NhAssistantRuleInput> Inputs { get; init; } = [];

    /// <summary>The alert kinds the agent lists with <c>Alerts(...)</c>; the script raises and resolves only these.</summary>
    public IReadOnlyCollection<string> AlertKinds { get; init; } = [];

    /// <summary>The tools the run may use, reads and writes; a write the version may not call is refused.</summary>
    public IReadOnlyList<NhAiToolDescriptor> Tools { get; init; } = [];

    /// <summary>The writing tools, by id, the version may call within the ceiling.</summary>
    public IReadOnlySet<string> Writes { get; init; } = Nothing;

    /// <summary>The destructive tools among <see cref="Writes"/> the ceiling lets run in a script.</summary>
    public IReadOnlySet<string> Destructive { get; init; } = Nothing;

    /// <summary>The most writes the run makes: the ceiling's <c>MaxWritesPerRun</c>, 0 for a read-only script.</summary>
    public int MaxWrites { get; init; }

    /// <summary>
    /// The step session every tool call of the script runs through; the platform opens it for the run.
    /// </summary>
    public NhAssistantToolStepSession? Steps { get; init; }

    /// <summary>
    /// Records a proposal through the rules' path: the agent's tools and grants, its approval policy
    /// (always deferred for rules), guards, deduplication and audit. Nothing executes before people approve.
    /// </summary>
    public Task<NhAssistantRuleProposal> ProposeAsync(
        string tool,
        JsonElement input,
        string? rationale,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tool);
        return _propose(tool, input, rationale, cancellationToken);
    }
}

/// <summary>
/// Why a script cannot run, with its place in the script when it has one.
/// </summary>
/// <param name="Code">One of the script codes, such as <c>ai-script-syntax-error</c>.</param>
public sealed record NhAssistantRuleScriptProblem(string Code, string Message)
{
    public int? Line { get; init; }

    public int? Column { get; init; }
}

/// <summary>
/// How a rule script ended: the summary of the run, a hand-over to the model or a skill the run continues in.
/// </summary>
public enum NhAssistantRuleScriptEndKind
{
    /// <summary><c>done(summary)</c>: the run ends with the summary.</summary>
    Done = 0,

    /// <summary><c>handOver(reason)</c>: the agent's model continues the same run.</summary>
    HandOver = 1,

    /// <summary><c>useSkill(skillId, reason)</c>: the run continues in one of the agent's skills.</summary>
    UseSkill = 2
}

/// <summary>
/// The end a script chose.
/// </summary>
/// <param name="Text">The summary or the reason.</param>
public sealed record NhAssistantRuleScriptEnd(NhAssistantRuleScriptEndKind Kind, string Text)
{
    public string? SkillId { get; init; }
}

/// <summary>
/// One tool call of a rule script and what became of it, in order.
/// </summary>
/// <param name="Sequence">The call's number, from 1.</param>
/// <param name="Tool">The function name the script called.</param>
/// <param name="Outcome">
/// <c>done</c>, <c>failed</c>, <c>proposed</c>, <c>not-needed</c>, <c>waiting-for-approval</c>, <c>refused</c>
/// or <c>invalid</c>, as the script tool names them.
/// </param>
public sealed record NhAssistantRuleScriptCall(int Sequence, string Tool, string ToolId, string Outcome)
{
    public string? Code { get; init; }

    public Guid? ProposalId { get; init; }

    /// <summary>Whether the tool changes something.</summary>
    public bool Write { get; init; }
}

/// <summary>
/// What a rule script did: whether it completed, the end it chose, the alerts it raised and resolved, its
/// calls with their outcomes and its log, or where it stopped with the script's code.
/// </summary>
public sealed record NhAssistantRuleScriptOutcome
{
    /// <summary>The SHA-256 of the script text, lowercase hexadecimal.</summary>
    public required string ScriptHash { get; init; }

    public required bool Completed { get; init; }

    /// <summary>The script's code of a stopped script, such as <c>ai-script-runtime-error</c>.</summary>
    public string? Code { get; init; }

    /// <summary>Why it stopped, in English for administrators.</summary>
    public string? Message { get; init; }

    /// <summary>The line in the script where it stopped, from 1.</summary>
    public int? Line { get; init; }

    public int? Column { get; init; }

    /// <summary>The call it stopped at.</summary>
    public int? StoppedAtCall { get; init; }

    /// <summary>The function name of the tool or platform function it stopped at.</summary>
    public string? StoppedAtTool { get; init; }

    /// <summary>
    /// The end the script chose; a completed script without one ended with
    /// <see cref="NhAssistantRuleScriptEndKind.Done"/> and the value it returned, when that was text.
    /// </summary>
    public NhAssistantRuleScriptEnd? End { get; init; }

    /// <summary>The alerts the script raised with <c>alerts.raise</c>, in order.</summary>
    public IReadOnlyList<NhAssistantAlert> Alerts { get; init; } = [];

    /// <summary>The alerts the script resolved with <c>alerts.resolve</c>, in order.</summary>
    public IReadOnlyList<NhAssistantAlertKey> Resolves { get; init; } = [];

    public IReadOnlyList<NhAssistantRuleScriptCall> Calls { get; init; } = [];

    /// <summary>What the script wrote with <c>console.log</c>, bounded.</summary>
    public IReadOnlyList<string> Logs { get; init; } = [];

    /// <summary>The outcome in the script tool's result shape, as the run's activity keeps it.</summary>
    public JsonElement Result { get; init; }
}

/// <summary>
/// What a rule script sees, for an editor or the agent builder: its tools with their signatures, the
/// platform functions and the limits.
/// </summary>
/// <param name="Tools">The tools the agent's rules may use, by function name.</param>
/// <param name="PlatformFunctions">The TypeScript-style declarations of the values and platform functions.</param>
/// <param name="MaxToolCalls">The most tool calls of one run.</param>
/// <param name="TimeoutSeconds">How long one run may take, its calls included.</param>
public sealed record NhAssistantRuleScriptDeclarations(
    IReadOnlyList<NhAssistantRuleScriptToolSignature> Tools,
    string PlatformFunctions,
    int MaxToolCalls,
    double TimeoutSeconds,
    int MaxScriptLength);

/// <summary>
/// One tool of a rule script.
/// </summary>
/// <param name="Name">The function name, the property of <c>tools</c>.</param>
/// <param name="Write">Whether it changes something.</param>
/// <param name="Callable">Whether the script may call it: every read, and a write the version opted in to.</param>
/// <param name="Signature">Its TypeScript-style declaration.</param>
/// <param name="Refusal">Why the script may not call it; <see langword="null"/> when it may.</param>
public sealed record NhAssistantRuleScriptToolSignature(
    string Name,
    string ToolId,
    bool Write,
    bool Callable,
    string Signature,
    string? Refusal);
