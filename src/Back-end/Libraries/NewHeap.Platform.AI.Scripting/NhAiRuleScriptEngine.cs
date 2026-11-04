using System.Buffers;
using System.Globalization;
using System.Text.Json;
using NewHeap.Platform.AI.Chat;

namespace NewHeap.Platform.AI.Scripting;

/// <summary>
/// Runs rule scripts (<see cref="INhAssistantRuleScriptEngine"/>, RB-1) on the engine of the script tool:
/// the same sandbox, limits, signatures and codes, with the run's values and the platform functions of a
/// rule next to <c>tools</c> and <c>console</c>. A script reads <c>subject</c>, <c>inputs</c> and
/// <c>run</c>; raises and resolves alerts with <c>alerts.raise</c> and <c>alerts.resolve</c>; proposes with
/// <c>propose</c>; and ends the run with <c>done</c>, <c>handOver</c> or <c>useSkill</c>. Its tools are the
/// tools of its run; a write is callable only when its version opted in to it within the ceiling, and
/// every call runs through the run's steps with the decision it gets on its own.
/// </summary>
internal sealed class NhAiRuleScriptEngine(NhAiScriptingOptions options, NhAiScriptRunner runner) : INhAssistantRuleScriptEngine
{
    /// <summary>The summary of a script that completed without an end and without returning text.</summary>
    internal const string DefaultSummary = "The rule script ran.";

    internal const string PlatformDeclarations = """
        declare const subject: { type: string; id: string; displayName: string | null; wholeApplication: boolean };
        declare const inputs: { triggerId: string | null; text: string; payload: unknown; createdAt: string }[];
        declare const run: { agentId: string; skillId: string | null; version: number; triggerId: string; runId: string | null; now: string; mode: "live" | "shadow" | "dry-run" };
        declare const alerts: {
          // A kind the agent lists with Alerts(...); the subject is the run's unless named. The same kind, subject and key update one open alert.
          raise(alert: { kind: string; key?: string; urgency?: "info" | "warning" | "urgent"; parameters?: Record<string, string | number | boolean>; recipients?: string[]; expiresAt?: string; subject?: { type: string; id: string } }): void;
          // key omitted: the alert with the empty key; key null: every key of the kind about the subject.
          resolve(alert: { kind: string; key?: string | null; subject?: { type: string; id: string } }): void;
        };
        // Records a proposal that waits for people, through the rules' path; nothing executes before they approve.
        declare function propose(proposal: { tool: string; input?: object; rationale?: string }): Promise<{ outcome: "created" | "already-open" | "recently-rejected" | "refused" | "not-needed" | "dry-run"; approvalId?: string; code?: string }>;
        // End the run once: with a summary, by handing it to the agent's model, or in one of the agent's skills.
        declare function done(summary: string): void;
        declare function handOver(reason: string): void;
        declare function useSkill(skillId: string, reason: string): void;
        """;

    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    public NhAssistantRuleScriptProblem? Check(string agentId, string script)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(script);
        var stop = NhAiScriptRunner.Check(script, options.RuleScriptLimitsOf(agentId));
        return stop is null
            ? null
            : new NhAssistantRuleScriptProblem(stop.Code, stop.Message) { Line = stop.Line, Column = stop.Column };
    }

    public NhAssistantRuleScriptDeclarations Describe(
        string agentId,
        IReadOnlyList<NhAiToolDescriptor> tools,
        IReadOnlySet<string> writes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(writes);
        var limits = LimitsOf(agentId, writes.Count == 0 ? 0 : NhAssistantRuleScriptLimits.MaxWritesPerRun);
        var environment = new NhAiScriptEnvironment
        {
            Refuse = offer => RefusalOf(offer, writes),
            AllowsDestructive = _ => true
        };
        var signatures = tools
            .Select(tool => new NhAiScriptToolOffer(NameOf(tool), tool))
            .GroupBy(offer => offer.Name, StringComparer.Ordinal)
            .Select(group => group.First())
            .Select(offer =>
            {
                var refusal = runner.CallableTools([offer], limits, environment).Count == 0
                    ? RefusalOf(offer, writes) ?? "it changes data and this version may not call it."
                    : null;
                return new NhAssistantRuleScriptToolSignature(
                    offer.Name,
                    offer.Descriptor.Id,
                    offer.IsMutation,
                    refusal is null,
                    NhAiScriptSignatures.Declare(offer),
                    refusal);
            })
            .ToArray();
        return new NhAssistantRuleScriptDeclarations(
            signatures,
            PlatformDeclarations,
            limits.MaxToolCalls,
            limits.Timeout.TotalSeconds,
            limits.MaxScriptLength);
    }

    public Task<NhAssistantRuleScriptOutcome> RunAsync(NhAssistantRuleScriptRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.Steps is null)
        {
            throw new ArgumentException("A rule script runs through the steps of its run.", nameof(run));
        }

        return RunAsync(run, new RuleScriptHost(run.Tools, run.Steps), cancellationToken);
    }

    /// <summary>
    /// Runs the script of <paramref name="run"/> against <paramref name="host"/>, which a test may give.
    /// </summary>
    internal async Task<NhAssistantRuleScriptOutcome> RunAsync(
        NhAssistantRuleScriptRun run,
        INhAiScriptHost host,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(host);
        var limits = LimitsOf(run.AgentId, run.MaxWrites);
        var platform = new Platform(run);
        var environment = new NhAiScriptEnvironment
        {
            Values = Values(run),
            Functions = platform.Functions(),
            Refuse = offer => RefusalOf(offer, run.Writes),
            AllowsDestructive = run.Destructive.Contains
        };

        var result = await runner.RunAsync(run.Script, host, limits, environment, cancellationToken);
        var end = platform.End;
        if (result.Completed && end is null)
        {
            // A script that returns text ends with it as the summary.
            end = result.Value is { ValueKind: JsonValueKind.String } text
                && text.GetString()?.Trim() is { Length: > 0 and <= NhAssistantRuleResult.MaxSummaryLength } summary
                    ? new NhAssistantRuleScriptEnd(NhAssistantRuleScriptEndKind.Done, summary)
                    : new NhAssistantRuleScriptEnd(NhAssistantRuleScriptEndKind.Done, DefaultSummary);
        }

        return new NhAssistantRuleScriptOutcome
        {
            ScriptHash = result.ScriptHash,
            Completed = result.Completed,
            Code = result.Stop?.Code,
            Message = result.Completed ? null : result.Describe(),
            Line = result.Stop?.Line,
            Column = result.Stop?.Column,
            StoppedAtCall = result.Stop?.Sequence,
            StoppedAtTool = result.Stop?.Tool,
            End = result.Completed ? end : null,
            Alerts = platform.Alerts,
            Resolves = platform.Resolves,
            Calls = result.Calls
                .Select(call => new NhAssistantRuleScriptCall(call.Sequence, call.Tool, call.ToolId, call.Outcome)
                {
                    Code = call.Code,
                    ProposalId = call.ProposalId,
                    Write = call.Mutation
                })
                .ToArray(),
            Logs = result.Logs,
            Result = result.ToToolResult()
        };
    }

    /// <summary>
    /// The limits of a rule script of <paramref name="agentId"/>: the rule script limits of the scripting
    /// options, with the ceiling's writes per run as its calls that change data.
    /// </summary>
    private NhAiScriptLimits LimitsOf(string agentId, int maxWrites)
    {
        var limits = options.RuleScriptLimitsOf(agentId).Clone();
        limits.MaxMutationsPerScript = Math.Clamp(maxWrites, 0, limits.MaxToolCalls);
        return limits;
    }

    private static string? RefusalOf(NhAiScriptToolOffer offer, IReadOnlySet<string> writes)
    {
        return offer.IsMutation && !writes.Contains(offer.Descriptor.Id)
            ? "it changes data and this rule script version may not call it. Opt it in for the version within the code's ceiling, or propose it with propose()."
            : null;
    }

    private static string NameOf(NhAiToolDescriptor descriptor)
    {
        return string.IsNullOrWhiteSpace(descriptor.ExportName) ? descriptor.Id : descriptor.ExportName;
    }

    private static Dictionary<string, JsonElement> Values(NhAssistantRuleScriptRun run)
    {
        var subject = run.Subject;
        return new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["subject"] = JsonSerializer.SerializeToElement(new
            {
                type = subject.Subject.Type,
                id = subject.Subject.Id,
                displayName = subject.DisplayName,
                wholeApplication = NhAiSystemSubjects.IsSystem(subject.Subject)
            }, Json),
            ["inputs"] = Write(writer =>
            {
                writer.WriteStartArray();
                foreach (var input in run.Inputs)
                {
                    writer.WriteStartObject();
                    if (input.TriggerId is null)
                    {
                        writer.WriteNull("triggerId");
                    }
                    else
                    {
                        writer.WriteString("triggerId", input.TriggerId);
                    }

                    writer.WriteString("text", input.Text);
                    writer.WritePropertyName("payload");
                    WritePayload(writer, input.PayloadJson);
                    writer.WriteString("createdAt", input.CreatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }),
            ["run"] = JsonSerializer.SerializeToElement(new
            {
                agentId = run.AgentId,
                skillId = run.SkillId,
                version = run.Version,
                triggerId = run.TriggerId,
                runId = run.RunId?.ToString("D"),
                now = run.Now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                mode = ModeName(run.Mode)
            }, Json)
        };
    }

    internal static string ModeName(NhAssistantRuleScriptMode mode)
    {
        return mode switch
        {
            NhAssistantRuleScriptMode.Shadow => "shadow",
            NhAssistantRuleScriptMode.DryRun => "dry-run",
            _ => "live"
        };
    }

    private static void WritePayload(Utf8JsonWriter writer, string? payloadJson)
    {
        if (payloadJson is null)
        {
            writer.WriteNullValue();
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            document.RootElement.WriteTo(writer);
        }
        catch (JsonException)
        {
            writer.WriteNullValue();
        }
    }

    private static JsonElement Write(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            write(writer);
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// The platform functions of one run and what the script did with them.
    /// </summary>
    private sealed class Platform(NhAssistantRuleScriptRun run)
    {
        private readonly List<NhAssistantAlert> _alerts = [];
        private readonly List<NhAssistantAlertKey> _resolves = [];

        public IReadOnlyList<NhAssistantAlert> Alerts => _alerts;

        public IReadOnlyList<NhAssistantAlertKey> Resolves => _resolves;

        public NhAssistantRuleScriptEnd? End { get; private set; }

        public IReadOnlyList<NhAiScriptHostFunction> Functions()
        {
            return
            [
                new NhAiScriptHostFunction("alerts.raise", Raise),
                new NhAiScriptHostFunction("alerts.resolve", Resolve),
                new NhAiScriptHostFunction("propose", ProposeAsync),
                new NhAiScriptHostFunction("done", arguments => Finish(NhAssistantRuleScriptEndKind.Done, null, Text(arguments, 0))),
                new NhAiScriptHostFunction("handOver", arguments => Finish(NhAssistantRuleScriptEndKind.HandOver, null, Text(arguments, 0))),
                new NhAiScriptHostFunction("useSkill", arguments => Finish(NhAssistantRuleScriptEndKind.UseSkill, Text(arguments, 0), Text(arguments, 1)))
            ];
        }

        private NhAiScriptHostReply Raise(IReadOnlyList<JsonElement> arguments)
        {
            if (!TryObject(arguments, out var input))
            {
                return NhAiScriptHostReply.Failed("alerts.raise takes one object with the alert's kind.");
            }
            if (KindOf(input, out var kind) is { } error)
            {
                return NhAiScriptHostReply.Failed("alerts.raise: " + error);
            }
            if (_alerts.Count >= NhAssistantAlertLimits.MaxAlertsPerResult)
            {
                return NhAiScriptHostReply.Failed($"a rule script raises at most {NhAssistantAlertLimits.MaxAlertsPerResult} alerts.");
            }

            try
            {
                var alert = new NhAssistantAlert(kind, SubjectOf(input)) { Key = OptionalText(input, "key") ?? string.Empty };
                if (OptionalText(input, "urgency") is { } urgency)
                {
                    alert = alert with { Urgency = UrgencyOf(urgency) };
                }
                if (input.TryGetProperty("parameters", out var parameters) && parameters.ValueKind != JsonValueKind.Null)
                {
                    alert = alert with { Parameters = ParametersOf(parameters) };
                }
                if (input.TryGetProperty("recipients", out var recipients) && recipients.ValueKind != JsonValueKind.Null)
                {
                    alert = alert with { Recipients = RecipientsOf(recipients) };
                }
                if (OptionalText(input, "expiresAt") is { } expiresAt)
                {
                    alert = alert with { ExpiresAt = DateTimeOffset.Parse(expiresAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) };
                }

                _alerts.Add(alert);
                return NhAiScriptHostReply.Nothing;
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException or InvalidOperationException)
            {
                return NhAiScriptHostReply.Failed("alerts.raise: " + exception.Message);
            }
        }

        private NhAiScriptHostReply Resolve(IReadOnlyList<JsonElement> arguments)
        {
            if (!TryObject(arguments, out var input))
            {
                return NhAiScriptHostReply.Failed("alerts.resolve takes one object with the alert's kind.");
            }
            if (KindOf(input, out var kind) is { } error)
            {
                return NhAiScriptHostReply.Failed("alerts.resolve: " + error);
            }
            if (_resolves.Count >= NhAssistantAlertLimits.MaxAlertsPerResult)
            {
                return NhAiScriptHostReply.Failed($"a rule script resolves at most {NhAssistantAlertLimits.MaxAlertsPerResult} alerts.");
            }

            try
            {
                // An omitted key is the alert's empty key; null resolves every key of the kind about the subject.
                string? key = string.Empty;
                if (input.TryGetProperty("key", out var named))
                {
                    key = named.ValueKind switch
                    {
                        JsonValueKind.Null => null,
                        JsonValueKind.String => named.GetString(),
                        _ => throw new ArgumentException("the key is a string or null.")
                    };
                }

                _resolves.Add(new NhAssistantAlertKey(kind, SubjectOf(input), key));
                return NhAiScriptHostReply.Nothing;
            }
            catch (ArgumentException exception)
            {
                return NhAiScriptHostReply.Failed("alerts.resolve: " + exception.Message);
            }
        }

        private async ValueTask<NhAiScriptHostReply> ProposeAsync(IReadOnlyList<JsonElement> arguments, CancellationToken cancellationToken)
        {
            if (!TryObject(arguments, out var input)
                || !input.TryGetProperty("tool", out var named)
                || named.ValueKind != JsonValueKind.String
                || named.GetString() is not { Length: > 0 } tool)
            {
                return NhAiScriptHostReply.Failed("propose takes one object with the tool's function name or id and its input.");
            }

            var proposed = input.TryGetProperty("input", out var value) && value.ValueKind != JsonValueKind.Null
                ? value
                : JsonSerializer.SerializeToElement(new { }, Json);
            if (proposed.ValueKind != JsonValueKind.Object)
            {
                return NhAiScriptHostReply.Failed("the input of propose is an object of the tool's input properties.");
            }

            string? rationale = null;
            if (input.TryGetProperty("rationale", out var why) && why.ValueKind != JsonValueKind.Null)
            {
                if (why.ValueKind != JsonValueKind.String)
                {
                    return NhAiScriptHostReply.Failed("the rationale of propose is a string.");
                }

                rationale = why.GetString();
            }
            if (rationale?.Length > NhAssistantRuleScriptLimits.MaxReasonLength)
            {
                return NhAiScriptHostReply.Failed($"a rationale has at most {NhAssistantRuleScriptLimits.MaxReasonLength} characters.");
            }

            var proposal = await run.ProposeAsync(tool, proposed, rationale, cancellationToken);
            if (run.Mode != NhAssistantRuleScriptMode.Live)
            {
                // Recorded as what the script would propose; nothing waits for people.
                return NhAiScriptHostReply.Of(JsonSerializer.SerializeToElement(new { outcome = "dry-run" }, Json));
            }

            return NhAiScriptHostReply.Of(JsonSerializer.SerializeToElement(new
            {
                outcome = OutcomeName(proposal.Outcome),
                approvalId = proposal.ApprovalId?.ToString("D"),
                code = proposal.Code
            }, Json));
        }

        private NhAiScriptHostReply Finish(NhAssistantRuleScriptEndKind kind, string? skillId, string? text)
        {
            if (End is not null)
            {
                return NhAiScriptHostReply.Failed("the run already ended; call done, handOver or useSkill once.");
            }

            var name = kind switch
            {
                NhAssistantRuleScriptEndKind.HandOver => "handOver",
                NhAssistantRuleScriptEndKind.UseSkill => "useSkill",
                _ => "done"
            };
            if (kind == NhAssistantRuleScriptEndKind.UseSkill && !NhAiSubjectRef.IsValidType(skillId))
            {
                return NhAiScriptHostReply.Failed("useSkill takes the dash-case id of one of the agent's skills.");
            }

            var trimmed = text?.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.Length > NhAssistantRuleResult.MaxSummaryLength)
            {
                return NhAiScriptHostReply.Failed($"{name} takes a text of 1 to {NhAssistantRuleResult.MaxSummaryLength} characters.");
            }

            End = new NhAssistantRuleScriptEnd(kind, trimmed) { SkillId = skillId };
            return NhAiScriptHostReply.Nothing;
        }

        /// <summary>
        /// Why the alert's kind cannot be raised or resolved, or <see langword="null"/> when the agent lists it.
        /// </summary>
        private string? KindOf(JsonElement input, out string kind)
        {
            kind = input.TryGetProperty("kind", out var named) && named.ValueKind == JsonValueKind.String
                ? named.GetString()!
                : string.Empty;
            if (kind.Length == 0)
            {
                return "an alert names its kind.";
            }

            return run.AlertKinds.Contains(kind, StringComparer.Ordinal)
                ? null
                : "alert kind '" + (kind.Length > 64 ? kind[..64] : kind) + "' is not listed by the agent, with Alerts(...) in code or in its run settings.";
        }

        private NhAiSubjectRef SubjectOf(JsonElement input)
        {
            if (!input.TryGetProperty("subject", out var subject) || subject.ValueKind == JsonValueKind.Null)
            {
                return run.Subject.Subject;
            }
            if (subject.ValueKind != JsonValueKind.Object
                || OptionalText(subject, "type") is not { } type
                || OptionalText(subject, "id") is not { } id)
            {
                throw new ArgumentException("a subject is an object with a type and an id.");
            }

            return new NhAiSubjectRef(type, id);
        }

        private static NhAssistantAlertUrgency UrgencyOf(string urgency)
        {
            return urgency switch
            {
                "info" => NhAssistantAlertUrgency.Info,
                "warning" => NhAssistantAlertUrgency.Warning,
                "urgent" => NhAssistantAlertUrgency.Urgent,
                _ => throw new ArgumentException("the urgency is info, warning or urgent.")
            };
        }

        private static Dictionary<string, string> ParametersOf(JsonElement parameters)
        {
            if (parameters.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("the parameters are an object of names and values.");
            }

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var parameter in parameters.EnumerateObject())
            {
                values[parameter.Name] = parameter.Value.ValueKind switch
                {
                    JsonValueKind.String => parameter.Value.GetString()!,
                    JsonValueKind.Number => parameter.Value.GetRawText(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => throw new ArgumentException("a parameter's value is text, a number or a boolean.")
                };
            }

            return values;
        }

        private static string[] RecipientsOf(JsonElement recipients)
        {
            if (recipients.ValueKind != JsonValueKind.Array)
            {
                throw new ArgumentException("the recipients are a list of actor ids.");
            }

            return recipients.EnumerateArray()
                .Select(item => item.ValueKind == JsonValueKind.String
                    ? item.GetString()!
                    : throw new ArgumentException("a recipient is an actor id."))
                .ToArray();
        }

        private static string OutcomeName(NhAssistantProposeOutcome outcome)
        {
            return outcome switch
            {
                NhAssistantProposeOutcome.Created => "created",
                NhAssistantProposeOutcome.AlreadyOpen => "already-open",
                NhAssistantProposeOutcome.RecentlyRejected => "recently-rejected",
                NhAssistantProposeOutcome.NotNeeded => "not-needed",
                _ => "refused"
            };
        }

        private static bool TryObject(IReadOnlyList<JsonElement> arguments, out JsonElement input)
        {
            input = arguments.Count > 0 ? arguments[0] : default;
            return arguments.Count == 1 && input.ValueKind == JsonValueKind.Object;
        }

        private static string? Text(IReadOnlyList<JsonElement> arguments, int index)
        {
            return arguments.Count > index && arguments[index].ValueKind == JsonValueKind.String
                ? arguments[index].GetString()
                : null;
        }

        private static string? OptionalText(JsonElement input, string name)
        {
            if (!input.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            return value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : throw new ArgumentException("'" + name + "' is a string.");
        }
    }

    /// <summary>
    /// The host of a rule script: the run's tools as the script names them, called through the run's
    /// step session.
    /// </summary>
    private sealed class RuleScriptHost : INhAiScriptHost
    {
        private readonly NhAssistantToolStepSession _session;

        public RuleScriptHost(IReadOnlyList<NhAiToolDescriptor> tools, NhAssistantToolStepSession session)
        {
            _session = session;
            Tools = tools.Select(descriptor => new NhAiScriptToolOffer(NameOf(descriptor), descriptor)).ToArray();
        }

        public IReadOnlyList<NhAiScriptToolOffer> Tools { get; }

        public async ValueTask<NhAiScriptCallOutcome> CallAsync(NhAiScriptToolCall call, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(call);
            var step = await _session.RunAsync(call.Tool.Name, call.Arguments, cancellationToken);
            return NhAiScriptStepHost.OutcomeOf(step, _session.Stopped);
        }
    }
}
