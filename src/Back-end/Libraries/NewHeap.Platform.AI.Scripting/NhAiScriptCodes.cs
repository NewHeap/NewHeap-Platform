namespace NewHeap.Platform.AI.Scripting;

/// <summary>
/// The stable codes of a script that did not complete, returned as the <c>code</c> of the script
/// tool's result. Every result still lists, in order, what the script's calls did before it stopped.
/// </summary>
public static class NhAiScriptCodes
{
    /// <summary>The program does not parse; the result names the line and column in the script.</summary>
    public const string SyntaxError = "ai-script-syntax-error";

    /// <summary>The program threw an error it did not catch; the result names the error and its line.</summary>
    public const string RuntimeError = "ai-script-runtime-error";

    /// <summary>The program is longer than <see cref="NhAiScriptLimits.MaxScriptLength"/>; nothing ran.</summary>
    public const string TooLong = "ai-script-too-long";

    /// <summary>The program ran longer than <see cref="NhAiScriptLimits.Timeout"/>, its tool calls included.</summary>
    public const string Timeout = "ai-script-timeout";

    /// <summary>The program ran more statements than <see cref="NhAiScriptLimits.MaxStatements"/>.</summary>
    public const string StatementLimit = "ai-script-statement-limit";

    /// <summary>The program allocated more than <see cref="NhAiScriptLimits.MaxMemoryBytes"/>.</summary>
    public const string MemoryLimit = "ai-script-memory-limit";

    /// <summary>The program recursed deeper than <see cref="NhAiScriptLimits.MaxRecursionDepth"/>.</summary>
    public const string RecursionLimit = "ai-script-recursion-limit";

    /// <summary>The program called more tools than <see cref="NhAiScriptLimits.MaxToolCalls"/>; that call did not run.</summary>
    public const string ToolCallLimit = "ai-script-tool-call-limit";

    /// <summary>
    /// The program called more tools that change something than
    /// <see cref="NhAiScriptLimits.MaxMutationsPerScript"/>; that call did not run.
    /// </summary>
    public const string MutationLimit = "ai-script-mutation-limit";

    /// <summary>The value the program returned is larger than <see cref="NhAiScriptLimits.MaxResultBytes"/>.</summary>
    public const string ResultTooLarge = "ai-script-result-too-large";

    /// <summary>The program called a tool that is not offered in this turn; nothing ran for it.</summary>
    public const string UnknownTool = "ai-script-unknown-tool";

    /// <summary>
    /// The program called a tool that is offered in this turn but may not run in a script: a tool that
    /// changes something while the script is read-only, or a destructive tool that did not opt in.
    /// </summary>
    public const string ToolRefused = "ai-script-tool-refused";

    /// <summary>
    /// The program called a tool with arguments that do not match its signature; the result names the
    /// exact signature and the field error, and the call did not run.
    /// </summary>
    public const string CallInvalid = "ai-script-call-invalid";

    /// <summary>
    /// The turn refused the call before it ran, for example at its own tool-call limit or budget; the
    /// result names the turn's code.
    /// </summary>
    public const string CallRefused = "ai-script-call-refused";

    /// <summary>
    /// A call needs a person's approval before the turn can go on (an inline approval): the script stopped
    /// at that call, and the turn waits as it does for the same call on its own.
    /// </summary>
    public const string WaitingForApproval = "ai-script-waiting-for-approval";

    /// <summary>The program waits for something that never happens, such as a promise nobody settles.</summary>
    public const string Stalled = "ai-script-stalled";

    /// <summary>The program used something a script cannot use, such as a module import.</summary>
    public const string NotSupported = "ai-script-not-supported";

    /// <summary>
    /// The script tool was called outside a turn or MCP request that offers it, so it has no tools to
    /// run; nothing ran.
    /// </summary>
    public const string NotAvailable = "ai-script-not-available";
}

/// <summary>
/// What became of one tool call of a script, as the script tool's result lists it.
/// </summary>
public static class NhAiScriptCallOutcomes
{
    /// <summary>The tool ran and succeeded.</summary>
    public const string Done = "done";

    /// <summary>The tool returned a failure, or governance refused it as it would on its own; the call names the code.</summary>
    public const string Failed = "failed";

    /// <summary>A proposal was recorded and waits for people; the action has not happened and the script went on.</summary>
    public const string Proposed = "proposed";

    /// <summary>A proposal guard found the work already done or no longer applicable; nothing was proposed and the script went on.</summary>
    public const string NotNeeded = "not-needed";

    /// <summary>The call waits for a person's approval and the script stopped here.</summary>
    public const string WaitingForApproval = "waiting-for-approval";

    /// <summary>The turn refused the call before it ran and the script stopped here.</summary>
    public const string Refused = "refused";

    /// <summary>The tool rejected the arguments before it ran and the script stopped here.</summary>
    public const string Invalid = "invalid";
}

/// <summary>
/// How a script ended, as <c>status</c> of the script tool's result names it.
/// </summary>
public static class NhAiScriptStatuses
{
    /// <summary>The program returned; <c>value</c> holds what it returned.</summary>
    public const string Completed = "completed";

    /// <summary>The program stopped before it returned; <c>code</c> says why and <c>stoppedAt</c> where.</summary>
    public const string Stopped = "stopped";
}
