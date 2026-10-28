using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Acornima;
using Acornima.Ast;
using Jint;
using Jint.Constraints;
using Jint.Native;
using Jint.Native.Function;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Descriptors;
using Jint.Runtime.Interop;
using JintJsonParser = Jint.Native.Json.JsonParser;
using JintJsonSerializer = Jint.Native.Json.JsonSerializer;

namespace NewHeap.Platform.AI.Scripting;

/// <summary>
/// Runs one script: a short JavaScript program, the body of an async function, in which every tool of
/// its host is an async function of <c>tools</c>. Each script gets a fresh Jint engine in strict mode with
/// no access to .NET, no modules, no <c>require</c>, no <c>eval</c> and no timers, and with the hard
/// limits of <see cref="NhAiScriptLimits"/>. Tool calls run one at a time through the host, in the order the
/// program made them. Only the program's return value goes back to the model; the result lists every
/// call with what became of it.
/// </summary>
public sealed class NhAiScriptRunner
{
    /// <summary>The name the engine gives the program in error locations.</summary>
    private const string SourceName = "script";

    private const int MaxLogEntries = 20;
    private const int MaxLogLength = 300;
    private const int MaxErrorMessageLength = 500;
    private const int MaxListedTools = 30;

    private static readonly Regex StackLocation = new(
        "script:(\\d+):(\\d+)",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private readonly NhAiScriptingOptions _options;

    public NhAiScriptRunner(NhAiScriptingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>
    /// The SHA-256 of a script text, lowercase hexadecimal, as calls, guards and the activity name it.
    /// </summary>
    public static string HashOf(string script)
    {
        ArgumentNullException.ThrowIfNull(script);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(script)));
    }

    /// <summary>
    /// The tools of <paramref name="host"/> a script under <paramref name="limits"/> may call: read-only
    /// tools always; tools that change something only when the limits allow mutations; destructive tools
    /// only when they opted in. The script tool itself is never among them.
    /// </summary>
    public IReadOnlyList<NhAiScriptToolOffer> CallableTools(INhAiScriptHost host, NhAiScriptLimits limits)
    {
        ArgumentNullException.ThrowIfNull(host);
        return CallableTools(host.Tools, limits);
    }

    /// <summary>
    /// The tools among <paramref name="offers"/> a script under <paramref name="limits"/> may call.
    /// </summary>
    public IReadOnlyList<NhAiScriptToolOffer> CallableTools(IReadOnlyList<NhAiScriptToolOffer> offers, NhAiScriptLimits limits)
    {
        ArgumentNullException.ThrowIfNull(offers);
        ArgumentNullException.ThrowIfNull(limits);
        return Offers(offers)
            .Where(offer => RefusalOf(offer, limits) is null)
            .ToArray();
    }

    /// <summary>
    /// Runs <paramref name="script"/> against <paramref name="host"/>. Expected outcomes, such as a syntax
    /// error, a limit or a call that waits for approval, are a stopped <see cref="NhAiScriptResult"/>;
    /// cancellation of <paramref name="cancellationToken"/> throws.
    /// </summary>
    public async Task<NhAiScriptResult> RunAsync(
        string script,
        INhAiScriptHost host,
        NhAiScriptLimits limits,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(limits);
        limits.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        var hash = HashOf(script);
        if (script.Length > limits.MaxScriptLength)
        {
            return Stopped(
                hash,
                new NhAiScriptStop(
                    NhAiScriptCodes.TooLong,
                    "the program has " + script.Length.ToString(CultureInfo.InvariantCulture) + " characters; a script has at most "
                        + limits.MaxScriptLength.ToString(CultureInfo.InvariantCulture) + ". Write a shorter program."),
                [],
                []);
        }

        var prepared = Prepare(script, out var syntaxError);
        if (syntaxError is not null)
        {
            return Stopped(hash, syntaxError, [], []);
        }

        using var run = new ScriptRun(this, host, limits, hash, script);
        return await run.ExecuteAsync(prepared!.Value, cancellationToken);
    }

    private static IReadOnlyList<NhAiScriptToolOffer> Offers(IReadOnlyList<NhAiScriptToolOffer> tools)
    {
        var offers = new List<NhAiScriptToolOffer>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var offer in tools)
        {
            // No script calls a composition or the script tool itself.
            if (offer is null
                || offer.Descriptor.CompositionFeature != NhAiCompositionFeature.None
                || string.Equals(offer.Name, NhAiScriptTool.FunctionName, StringComparison.Ordinal)
                || string.Equals(offer.Descriptor.Id, NhAiScriptTool.ToolId, StringComparison.Ordinal)
                || !names.Add(offer.Name))
            {
                continue;
            }

            offers.Add(offer);
        }

        return offers;
    }

    /// <summary>
    /// Why a script under <paramref name="limits"/> may not call <paramref name="offer"/>, or
    /// <see langword="null"/> when it may.
    /// </summary>
    private string? RefusalOf(NhAiScriptToolOffer offer, NhAiScriptLimits limits)
    {
        if (!offer.IsMutation)
        {
            return null;
        }
        if (limits.MaxMutationsPerScript == 0)
        {
            return "it changes data and scripts here are read-only. Call it on its own instead.";
        }
        if (offer.Descriptor.Effect == NhAiToolEffect.Destructive && !_options.AllowsDestructive(offer.Descriptor.Id))
        {
            return "it is destructive and has not opted in to scripts. Call it on its own instead.";
        }

        return null;
    }

    private static Prepared<Script>? Prepare(string script, out NhAiScriptStop? error)
    {
        error = null;
        Prepared<Script> prepared;
        try
        {
            prepared = Engine.PrepareScript(Wrap(script), SourceName, strict: true);
        }
        catch (ScriptPreparationException exception) when (exception.InnerException is ParseErrorException parse)
        {
            var (line, column) = ScriptPosition(script, parse.LineNumber, parse.Column + 1);
            error = new NhAiScriptStop(NhAiScriptCodes.SyntaxError, Bound(parse.Description ?? "the program does not parse.", MaxErrorMessageLength))
            {
                Line = line,
                Column = column
            };
            return null;
        }

        // The program must stay the body of the async function: one statement, the call of that function.
        if (prepared.Program is not { Body: [ExpressionStatement { Expression: CallExpression { Callee: ArrowFunctionExpression { Async: true } } }] })
        {
            error = new NhAiScriptStop(
                NhAiScriptCodes.SyntaxError,
                "the program closes the function it runs in. Write only the body of the program, with balanced braces.");
            return null;
        }

        return prepared;
    }

    private static string Wrap(string script)
    {
        return "(async () => {\n" + script + "\n})()";
    }

    /// <summary>
    /// Maps a position in the wrapped program to the script: the wrapper adds one line before it.
    /// </summary>
    private static (int Line, int Column) ScriptPosition(string script, int wrappedLine, int column)
    {
        var lines = script.Count(character => character == '\n') + 1;
        var line = Math.Clamp(wrappedLine - 1, 1, lines);
        return (line, Math.Max(1, column));
    }

    private static string Bound(string text, int length)
    {
        return text.Length <= length ? text : text[..(length - 3)] + "...";
    }

    private static NhAiScriptResult Stopped(
        string hash,
        NhAiScriptStop stop,
        IReadOnlyList<NhAiScriptCallRecord> calls,
        IReadOnlyList<string> logs)
    {
        return new NhAiScriptResult
        {
            Status = NhAiScriptStatuses.Stopped,
            ScriptHash = hash,
            Stop = stop,
            Calls = calls,
            Logs = logs
        };
    }

    /// <summary>
    /// Stops the script; thrown from the host functions of the engine, which the program cannot catch.
    /// </summary>
    private sealed class ScriptStopException(NhAiScriptStop stop) : Exception(stop.Message)
    {
        public NhAiScriptStop Stop { get; } = stop;
    }

    /// <summary>
    /// One limit of the script was reached inside the engine.
    /// </summary>
    private sealed class ScriptLimitException(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }

    /// <summary>
    /// Counts statements over the whole script: Jint resets its own statement limit at every entry into
    /// the engine, and a script enters it again after every tool call.
    /// </summary>
    private sealed class StatementBudget(long limit) : Constraint
    {
        private long _count;

        public override void Check()
        {
            if (++_count > limit)
            {
                throw new ScriptLimitException(
                    NhAiScriptCodes.StatementLimit,
                    "the program ran more than " + limit.ToString(CultureInfo.InvariantCulture) + " statements. Loop over less data or return earlier.");
            }
        }

        public override void Reset()
        {
            // Deliberately cumulative: one budget for the whole script.
        }
    }

    /// <summary>
    /// Counts the memory a script allocates over the whole script. The engine runs in segments, each on
    /// one thread without awaiting: the start and every continuation after a tool call. Each segment is
    /// measured on its own thread and added up, so a continuation on another thread is still counted.
    /// </summary>
    private sealed class MemoryBudget(long limit) : Constraint
    {
        private long _total;
        private long _baseline;
        private int _thread = -1;

        public void BeginSegment()
        {
            _thread = Environment.CurrentManagedThreadId;
            _baseline = GC.GetAllocatedBytesForCurrentThread();
        }

        public void EndSegment()
        {
            if (_thread == Environment.CurrentManagedThreadId)
            {
                _total += Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - _baseline);
            }
            _thread = -1;
        }

        public override void Check()
        {
            if (_thread != Environment.CurrentManagedThreadId)
            {
                return;
            }

            var allocated = _total + Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - _baseline);
            if (allocated > limit)
            {
                throw new ScriptLimitException(
                    NhAiScriptCodes.MemoryLimit,
                    "the program allocated more than " + limit.ToString(CultureInfo.InvariantCulture) + " bytes. Keep less data in memory.");
            }
        }

        public override void Reset()
        {
            // Deliberately cumulative: one budget for the whole script.
        }
    }

    private sealed record PendingCall(
        NhAiScriptToolOffer Offer,
        JsonElement Arguments,
        int Sequence,
        Action<JsValue> Resolve);

    /// <summary>
    /// The state of one running script and its engine.
    /// </summary>
    private sealed class ScriptRun : IDisposable
    {
        private const string ToolsFactorySource = """
            (function (source, unknown) {
              const target = Object.create(null);
              for (const name of Object.keys(source)) { target[name] = source[name]; }
              Object.freeze(target);
              return new Proxy(target, {
                get(t, name) {
                  if (typeof name !== 'string') { return undefined; }
                  if (Object.prototype.hasOwnProperty.call(t, name)) { return t[name]; }
                  if (name === 'then' || name === 'toJSON') { return undefined; }
                  return unknown(name);
                },
                set() { return false; },
                defineProperty() { return false; },
                deleteProperty() { return false; }
              });
            })
            """;

        private readonly NhAiScriptRunner _runner;
        private readonly INhAiScriptHost _host;
        private readonly NhAiScriptLimits _limits;
        private readonly string _hash;
        private readonly string _script;
        private readonly Dictionary<string, NhAiScriptToolOffer> _offers = new(StringComparer.Ordinal);
        private readonly List<NhAiScriptToolOffer> _callable = [];
        private readonly Queue<PendingCall> _queue = new();
        private readonly List<NhAiScriptCallRecord> _calls = [];
        private readonly List<string> _logs = [];
        private readonly StatementBudget _statements;
        private readonly MemoryBudget _memory;
        private readonly OperationDeadlineConstraint _deadline = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Engine _engine;
        private int _made;
        private int _mutations;
        private int _droppedLogs;
        private NhAiScriptStop? _stop;
        private bool _settled;
        private bool _fulfilled;
        private JsValue _settledValue = JsValue.Undefined;

        public ScriptRun(NhAiScriptRunner runner, INhAiScriptHost host, NhAiScriptLimits limits, string hash, string script)
        {
            _runner = runner;
            _host = host;
            _limits = limits;
            _hash = hash;
            _script = script;
            _statements = new StatementBudget(limits.MaxStatements);
            _memory = new MemoryBudget(limits.MaxMemoryBytes);
            foreach (var offer in Offers(host.Tools))
            {
                _offers.Add(offer.Name, offer);
                if (runner.RefusalOf(offer, limits) is null)
                {
                    _callable.Add(offer);
                }
            }

            _engine = new Engine(options =>
            {
                options.Strict = true;
                options.Interop.Enabled = false;
                options.Interop.AllowGetType = false;
                options.Interop.AllowSystemReflection = false;
                options.DisableStringCompilation();
                options.LimitRecursion(limits.MaxRecursionDepth);
                // Jint's own memory limit charges long string concatenations when they are built; the budget
                // below adds every segment up.
                options.LimitMemory(limits.MaxMemoryBytes);
                options.Constraints.StackOverflowGuard = true;
                options.Constraints.MaxArraySize = (uint)Math.Clamp(limits.MaxMemoryBytes / 16, 1_024, 10_000_000);
                options.Constraints.RegexTimeout = TimeSpan.FromMilliseconds(Math.Min(1_000, limits.Timeout.TotalMilliseconds));
                options.Constraint(_statements);
                options.Constraint(_memory);
                options.Constraint(_deadline);
            });
        }

        public async Task<NhAiScriptResult> ExecuteAsync(Prepared<Script> prepared, CancellationToken cancellationToken)
        {
            _deadline.Begin(_limits.Timeout, cancellationToken);
            try
            {
                Segment(() => Start(prepared));
                while (!_settled)
                {
                    ThrowIfStopped();
                    if (!_queue.TryDequeue(out var pending))
                    {
                        throw Stop(new NhAiScriptStop(
                            NhAiScriptCodes.Stalled,
                            "the program waits for something that never happens. Await only tool calls and promises they settle."));
                    }
                    if (_clock.Elapsed >= _limits.Timeout)
                    {
                        throw Stop(TimeoutStop(pending.Sequence, pending.Offer.Name));
                    }

                    var outcome = await _host.CallAsync(
                        new NhAiScriptToolCall(pending.Offer, pending.Arguments, _hash, pending.Sequence),
                        cancellationToken);
                    Record(pending, outcome);
                    var result = outcome.Result;
                    Segment(() =>
                    {
                        pending.Resolve(new JintJsonParser(_engine).Parse(result.GetRawText()));
                        _engine.Advanced.ProcessTasks();
                    });
                }

                ThrowIfStopped();
                return _fulfilled ? Complete() : Rejected();
            }
            catch (ScriptStopException exception)
            {
                return Stopped(_stop ?? exception.Stop);
            }
            catch (ScriptLimitException exception)
            {
                return Stopped(_stop ?? new NhAiScriptStop(exception.Code, exception.Message));
            }
            catch (MemoryLimitExceededException)
            {
                return Stopped(_stop ?? new NhAiScriptStop(
                    NhAiScriptCodes.MemoryLimit,
                    "the program allocated more than " + _limits.MaxMemoryBytes.ToString(CultureInfo.InvariantCulture) + " bytes or built too large an array. Keep less data in memory."));
            }
            catch (RecursionDepthOverflowException)
            {
                return Stopped(_stop ?? RecursionStop());
            }
            catch (TimeoutException) when (!cancellationToken.IsCancellationRequested)
            {
                return Stopped(_stop ?? TimeoutStop(null, null));
            }
            catch (ExecutionCanceledException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Stopped(_stop ?? TimeoutStop(null, null));
            }
            catch (JavaScriptException exception)
            {
                return Stopped(_stop ?? RuntimeStop(exception.Message, exception.Location.Start.Line, exception.Location.Start.Column + 1));
            }
            catch (InvalidOperationException exception) when (exception.Message.Contains("Module loading", StringComparison.Ordinal))
            {
                return Stopped(_stop ?? new NhAiScriptStop(NhAiScriptCodes.NotSupported, "a script cannot import modules."));
            }
            finally
            {
                _deadline.End();
            }
        }

        public void Dispose()
        {
            _engine.Dispose();
        }

        private void Start(Prepared<Script> prepared)
        {
            var tools = new JsObject(_engine);
            foreach (var offer in _callable)
            {
                var function = new ClrFunction(_engine, offer.Name, (_, arguments) => CallTool(offer, arguments), 1);
                tools.DefineOwnProperty(offer.Name, new PropertyDescriptor(function, PropertyFlag.Enumerable));
            }
            tools.PreventExtensions();

            var factory = _engine.Evaluate(ToolsFactorySource);
            var unknown = new ClrFunction(_engine, "unknown", (_, arguments) => Unknown(arguments.Length > 0 ? arguments[0].ToString() : string.Empty), 1);
            var proxy = _engine.Invoke(factory, JsValue.Undefined, new JsValue[] { tools, unknown });
            _engine.SetValue("tools", proxy);
            _engine.SetValue("console", CreateConsole());

            var main = _engine.Evaluate(prepared);
            if (main is not ObjectInstance promise || promise.Get("then") is not Function)
            {
                throw Stop(new NhAiScriptStop(NhAiScriptCodes.SyntaxError, "the program does not run as the body of an async function."));
            }

            var done = new ClrFunction(_engine, "done", (_, arguments) => Settle(true, arguments), 1);
            var fail = new ClrFunction(_engine, "fail", (_, arguments) => Settle(false, arguments), 1);
            _engine.Invoke(promise.Get("then"), promise, new JsValue[] { done, fail });
            _engine.Advanced.ProcessTasks();
        }

        private JsValue Settle(bool fulfilled, JsValue[] arguments)
        {
            if (!_settled)
            {
                _settled = true;
                _fulfilled = fulfilled;
                _settledValue = arguments.Length > 0 ? arguments[0] : JsValue.Undefined;
            }

            return JsValue.Undefined;
        }

        private ObjectInstance CreateConsole()
        {
            var console = new JsObject(_engine);
            foreach (var level in new[] { "log", "info", "warn", "error", "debug" })
            {
                console.DefineOwnProperty(
                    level,
                    new PropertyDescriptor(new ClrFunction(_engine, level, (_, arguments) => Log(arguments)), PropertyFlag.Enumerable));
            }
            console.PreventExtensions();
            return console;
        }

        private JsValue Log(JsValue[] arguments)
        {
            if (_logs.Count >= MaxLogEntries)
            {
                _droppedLogs++;
                return JsValue.Undefined;
            }

            var parts = arguments.Select(argument => argument.IsString() ? argument.AsString() : Stringify(argument));
            _logs.Add(Bound(string.Join(' ', parts), MaxLogLength));
            return JsValue.Undefined;
        }

        private string Stringify(JsValue value)
        {
            try
            {
                var json = new JintJsonSerializer(_engine).Serialize(value);
                return json.IsString() ? json.AsString() : value.ToString();
            }
            catch (JavaScriptException)
            {
                return value.ToString();
            }
        }

        private JsValue CallTool(NhAiScriptToolOffer offer, JsValue[] arguments)
        {
            ThrowIfStopped();
            var sequence = _made + 1;
            if (sequence > _limits.MaxToolCalls)
            {
                throw Stop(new NhAiScriptStop(
                    NhAiScriptCodes.ToolCallLimit,
                    "a script makes at most " + _limits.MaxToolCalls.ToString(CultureInfo.InvariantCulture) + " tool calls. Ask for less data per call or split the work.")
                {
                    Sequence = sequence,
                    Tool = offer.Name
                });
            }
            if (offer.IsMutation && _mutations >= _limits.MaxMutationsPerScript)
            {
                throw Stop(new NhAiScriptStop(
                    NhAiScriptCodes.MutationLimit,
                    "a script makes at most " + _limits.MaxMutationsPerScript.ToString(CultureInfo.InvariantCulture) + " calls that change data. Make the remaining changes in another step.")
                {
                    Sequence = sequence,
                    Tool = offer.Name
                });
            }

            var input = ReadArguments(offer, arguments, sequence);
            var fieldError = NhAiScriptSignatures.Validate(offer, input);
            if (fieldError is not null)
            {
                throw Stop(InvalidCallStop(offer, sequence, fieldError.Field + ": " + fieldError.Error, null) with { FieldError = fieldError });
            }

            _made = sequence;
            if (offer.IsMutation)
            {
                _mutations++;
            }

            var (promise, resolve, _) = _engine.Advanced.RegisterPromise();
            _queue.Enqueue(new PendingCall(offer, input, sequence, resolve));
            return promise;
        }

        private JsonElement ReadArguments(NhAiScriptToolOffer offer, JsValue[] arguments, int sequence)
        {
            var argument = arguments.Length > 0 ? arguments[0] : JsValue.Undefined;
            var objectInput = NhAiScriptSignatures.IsObjectSchema(offer.Descriptor.InputSchemaJson);
            if (arguments.Length > 1
                || argument is Function
                || (objectInput && !(argument.IsUndefined() || (argument.IsObject() && !argument.IsArray()))))
            {
                throw Stop(InvalidCallStop(offer, sequence, "a tool takes one object with its input properties.", null));
            }

            var json = argument.IsUndefined() ? (objectInput ? "{}" : "null") : Stringify(argument);
            try
            {
                using var document = JsonDocument.Parse(json);
                return document.RootElement.Clone();
            }
            catch (JsonException)
            {
                throw Stop(InvalidCallStop(offer, sequence, "the input cannot be written as JSON.", null));
            }
        }

        private JsValue Unknown(string name)
        {
            ThrowIfStopped();
            if (_offers.TryGetValue(name, out var offer))
            {
                throw Stop(new NhAiScriptStop(
                    NhAiScriptCodes.ToolRefused,
                    "'" + name + "' cannot run in a script: " + _runner.RefusalOf(offer, _limits))
                {
                    Tool = name
                });
            }

            var listed = _callable.Take(MaxListedTools).Select(item => item.Name).ToArray();
            var tools = listed.Length == 0
                ? "This turn offers no tools to scripts."
                : "The tools are: " + string.Join(", ", listed) + (_callable.Count > listed.Length ? ", ..." : ".");
            throw Stop(new NhAiScriptStop(
                NhAiScriptCodes.UnknownTool,
                "'" + Bound(name, 100) + "' is not a tool of this turn. " + tools)
            {
                Tool = Bound(name, 100)
            });
        }

        private void Record(PendingCall pending, NhAiScriptCallOutcome outcome)
        {
            var code = FailureCode(outcome.Result);
            var record = new NhAiScriptCallRecord(pending.Sequence, pending.Offer.Name, pending.Offer.Descriptor.Id, NhAiScriptCallOutcomes.Done)
            {
                Mutation = pending.Offer.IsMutation,
                ProposalId = outcome.ProposalId ?? ProposalIdOf(outcome.Result)
            };
            switch (outcome.Kind)
            {
                case NhAiScriptCallOutcomeKind.Proposed:
                    _calls.Add(record with { Outcome = NhAiScriptCallOutcomes.Proposed, Code = code });
                    return;
                case NhAiScriptCallOutcomeKind.NotNeeded:
                    _calls.Add(record with { Outcome = NhAiScriptCallOutcomes.NotNeeded, Code = code });
                    return;
                case NhAiScriptCallOutcomeKind.AwaitingApproval:
                    _calls.Add(record with { Outcome = NhAiScriptCallOutcomes.WaitingForApproval, Code = code });
                    throw Stop(new NhAiScriptStop(
                        NhAiScriptCodes.WaitingForApproval,
                        "this call waits for a person's approval. The turn waits for the decision; after it, continue with a new script for the rest if needed.")
                    {
                        Sequence = pending.Sequence,
                        Tool = pending.Offer.Name,
                        CallCode = code,
                        ProposalId = record.ProposalId
                    });
                case NhAiScriptCallOutcomeKind.Refused:
                    _calls.Add(record with { Outcome = NhAiScriptCallOutcomes.Refused, Code = code });
                    throw Stop(new NhAiScriptStop(
                        NhAiScriptCodes.CallRefused,
                        "the turn refused this call before it ran (" + (code ?? NhAiToolFailureCodes.Failed) + ").")
                    {
                        Sequence = pending.Sequence,
                        Tool = pending.Offer.Name,
                        CallCode = code
                    });
            }

            if (code is null)
            {
                _calls.Add(record);
                return;
            }
            if (string.Equals(code, NhAiToolFailureCodes.InputInvalid, StringComparison.Ordinal))
            {
                _calls.Add(record with { Outcome = NhAiScriptCallOutcomes.Invalid, Code = code });
                throw Stop(InvalidCallStop(pending.Offer, pending.Sequence, MessageOf(outcome.Result) ?? "the tool rejected the input.", code));
            }

            _calls.Add(record with { Outcome = NhAiScriptCallOutcomes.Failed, Code = code });
        }

        private NhAiScriptStop InvalidCallStop(NhAiScriptToolOffer offer, int sequence, string error, string? callCode)
        {
            return new NhAiScriptStop(
                NhAiScriptCodes.CallInvalid,
                "the call does not match the signature of " + offer.Name + ": " + Bound(error, MaxErrorMessageLength) + " Correct the call to match the signature.")
            {
                Sequence = sequence,
                Tool = offer.Name,
                Signature = NhAiScriptSignatures.Signature(offer),
                CallCode = callCode
            };
        }

        private NhAiScriptStop TimeoutStop(int? sequence, string? tool)
        {
            return new NhAiScriptStop(
                NhAiScriptCodes.Timeout,
                "the script ran longer than " + _limits.Timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " seconds, its tool calls included. Make fewer calls or loop over less data.")
            {
                Sequence = sequence,
                Tool = tool
            };
        }

        private NhAiScriptStop RecursionStop()
        {
            return new NhAiScriptStop(
                NhAiScriptCodes.RecursionLimit,
                "the program recursed deeper than " + _limits.MaxRecursionDepth.ToString(CultureInfo.InvariantCulture) + " levels. Use a loop instead.");
        }

        private NhAiScriptStop RuntimeStop(string message, int? wrappedLine, int? column)
        {
            var stop = new NhAiScriptStop(NhAiScriptCodes.RuntimeError, "the program threw " + Bound(message, MaxErrorMessageLength));
            if (wrappedLine is > 0 && column is { } at)
            {
                var (line, scriptColumn) = ScriptPosition(_script, wrappedLine.Value, at);
                stop = stop with { Line = line, Column = scriptColumn };
            }

            return stop;
        }

        private NhAiScriptResult Complete()
        {
            string json = "null";
            Segment(() =>
            {
                if (!_settledValue.IsUndefined())
                {
                    var written = new JintJsonSerializer(_engine).Serialize(_settledValue);
                    json = written.IsString() ? written.AsString() : "null";
                }
            });

            var bytes = Encoding.UTF8.GetByteCount(json);
            if (bytes > _limits.MaxResultBytes)
            {
                return Stopped(new NhAiScriptStop(
                    NhAiScriptCodes.ResultTooLarge,
                    "the program returned " + bytes.ToString(CultureInfo.InvariantCulture) + " bytes; a script returns at most "
                        + _limits.MaxResultBytes.ToString(CultureInfo.InvariantCulture) + ". Return only what the answer needs."));
            }

            using var document = JsonDocument.Parse(json);
            return new NhAiScriptResult
            {
                Status = NhAiScriptStatuses.Completed,
                ScriptHash = _hash,
                Value = document.RootElement.Clone(),
                ValueBytes = bytes,
                Calls = _calls.ToArray(),
                Logs = Logs()
            };
        }

        private NhAiScriptResult Rejected()
        {
            NhAiScriptStop stop = new(NhAiScriptCodes.RuntimeError, "the program threw an error.");
            Segment(() =>
            {
                if (_settledValue is ObjectInstance error && error.Get("message") is { } message && !message.IsUndefined())
                {
                    var name = error.Get("name");
                    var text = (name.IsUndefined() ? "Error" : name.ToString()) + ": " + message;
                    if (string.Equals(name.ToString(), "RangeError", StringComparison.Ordinal)
                        && message.ToString().Contains("call stack", StringComparison.OrdinalIgnoreCase))
                    {
                        stop = RecursionStop();
                        return;
                    }

                    var location = StackLocation.Match(error.Get("stack").ToString());
                    stop = location.Success
                        ? RuntimeStop(text, int.Parse(location.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(location.Groups[2].Value, CultureInfo.InvariantCulture))
                        : RuntimeStop(text, null, null);
                    return;
                }

                stop = RuntimeStop(Stringify(_settledValue), null, null);
            });
            return Stopped(stop);
        }

        private void Segment(Action action)
        {
            _memory.BeginSegment();
            try
            {
                action();
            }
            finally
            {
                _memory.EndSegment();
            }
        }

        private ScriptStopException Stop(NhAiScriptStop stop)
        {
            _stop ??= stop;
            return new ScriptStopException(_stop);
        }

        private void ThrowIfStopped()
        {
            if (_stop is not null)
            {
                throw new ScriptStopException(_stop);
            }
        }

        private NhAiScriptResult Stopped(NhAiScriptStop stop)
        {
            return NhAiScriptRunner.Stopped(_hash, stop, _calls.ToArray(), Logs());
        }

        private IReadOnlyList<string> Logs()
        {
            return _droppedLogs == 0
                ? _logs.ToArray()
                : [.. _logs, "... " + _droppedLogs.ToString(CultureInfo.InvariantCulture) + " more log entries were dropped."];
        }

        private static string? FailureCode(JsonElement result)
        {
            if (result.ValueKind != JsonValueKind.Object
                || !result.TryGetProperty(NhAiToolResult.SuccessPropertyName, out var success))
            {
                return NhAiToolFailureCodes.Failed;
            }
            if (success.ValueKind == JsonValueKind.True)
            {
                return null;
            }

            return result.TryGetProperty(NhAiToolResult.CodePropertyName, out var code) && code.ValueKind == JsonValueKind.String
                ? code.GetString()
                : NhAiToolFailureCodes.Failed;
        }

        private static string? MessageOf(JsonElement result)
        {
            return result.ValueKind == JsonValueKind.Object
                && result.TryGetProperty(NhAiToolResult.MessagePropertyName, out var message)
                && message.ValueKind == JsonValueKind.String
                    ? message.GetString()
                    : null;
        }

        private static Guid? ProposalIdOf(JsonElement result)
        {
            return result.ValueKind == JsonValueKind.Object
                && result.TryGetProperty("proposalId", out var proposalId)
                && proposalId.ValueKind == JsonValueKind.String
                && Guid.TryParse(proposalId.GetString(), out var id)
                    ? id
                    : null;
        }
    }
}
