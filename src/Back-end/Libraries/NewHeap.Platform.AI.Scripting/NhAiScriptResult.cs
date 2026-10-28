using System.Buffers;
using System.Globalization;
using System.Text.Json;

namespace NewHeap.Platform.AI.Scripting;

/// <summary>
/// One tool call of a script as its result lists it, in the order the calls ran: the call's number, the
/// tool's function name and id, what became of it (<see cref="NhAiScriptCallOutcomes"/>) and, for a
/// failure or a refusal, its code. Results of calls never return to the model; only the program's value
/// does.
/// </summary>
public sealed record NhAiScriptCallRecord(
    int Sequence,
    string Tool,
    string ToolId,
    string Outcome)
{
    /// <summary>The code of a call that failed, was refused or was invalid.</summary>
    public string? Code { get; init; }

    /// <summary>The proposal a proposed or waiting call recorded, when the turn named it.</summary>
    public Guid? ProposalId { get; init; }

    /// <summary>Whether the tool changes something.</summary>
    public bool Mutation { get; init; }
}

/// <summary>
/// Where and why a script stopped before it returned.
/// </summary>
public sealed record NhAiScriptStop(string Code, string Message)
{
    /// <summary>The call the script stopped at, when it stopped at a call.</summary>
    public int? Sequence { get; init; }

    /// <summary>The function name of the tool the script stopped at.</summary>
    public string? Tool { get; init; }

    /// <summary>The line in the script, counted from 1, when the stop has a place in the program.</summary>
    public int? Line { get; init; }

    /// <summary>The column in the script, counted from 1.</summary>
    public int? Column { get; init; }

    /// <summary>The exact signature of the tool of an invalid call or a refused tool.</summary>
    public string? Signature { get; init; }

    /// <summary>The field of an invalid call that does not match the signature.</summary>
    public NhAiScriptFieldError? FieldError { get; init; }

    /// <summary>The code the turn or the tool gave for a refused, waiting or invalid call.</summary>
    public string? CallCode { get; init; }

    /// <summary>The proposal a call that waits for approval recorded, when the turn named it.</summary>
    public Guid? ProposalId { get; init; }
}

/// <summary>
/// The outcome of one script: whether the program returned and with which value, every tool call it made
/// in order with what became of it, and where it stopped when it did not return. <see cref="ToToolResult"/>
/// writes it in the shared tool result shape, as the model receives it.
/// </summary>
public sealed record NhAiScriptResult
{
    /// <summary>One of <see cref="NhAiScriptStatuses"/>.</summary>
    public required string Status { get; init; }

    /// <summary>The SHA-256 of the script text, lowercase hexadecimal.</summary>
    public required string ScriptHash { get; init; }

    /// <summary>The JSON of the value the program returned; <see langword="null"/> when it stopped.</summary>
    public JsonElement? Value { get; init; }

    /// <summary>The size of <see cref="Value"/> in bytes of UTF-8 JSON.</summary>
    public int ValueBytes { get; init; }

    public IReadOnlyList<NhAiScriptCallRecord> Calls { get; init; } = [];

    /// <summary>What the program wrote with <c>console.log</c>, bounded.</summary>
    public IReadOnlyList<string> Logs { get; init; } = [];

    /// <summary>Why and where the script stopped; <see langword="null"/> when it completed.</summary>
    public NhAiScriptStop? Stop { get; init; }

    public bool Completed => string.Equals(Status, NhAiScriptStatuses.Completed, StringComparison.Ordinal);

    /// <summary>The number of calls that ran and succeeded.</summary>
    public int Done => Calls.Count(call => call.Outcome == NhAiScriptCallOutcomes.Done);

    /// <summary>The number of calls recorded as proposals that wait for people.</summary>
    public int Proposed => Calls.Count(call => call.Outcome == NhAiScriptCallOutcomes.Proposed);

    /// <summary>
    /// The model rounds the script saved: the calls it ran minus one, as one script call replaced them.
    /// </summary>
    public int RoundsSaved => Math.Max(0, Calls.Count - 1);

    /// <summary>
    /// The result the model receives: <c>{"success":true,"data":{...}}</c> with <c>value</c> for a script
    /// that returned, <c>{"success":false,"code":...,"message":...,"data":{...}}</c> with <c>stoppedAt</c>
    /// for one that stopped. Both list the calls in order with their outcome.
    /// </summary>
    public JsonElement ToToolResult()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteBoolean(NhAiToolResult.SuccessPropertyName, Completed);
            if (!Completed && Stop is not null)
            {
                writer.WriteString(NhAiToolResult.CodePropertyName, Stop.Code);
                writer.WriteString(NhAiToolResult.MessagePropertyName, Describe());
            }

            writer.WritePropertyName(NhAiToolResult.DataPropertyName);
            WriteData(writer);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// The message of a stopped script: why it stopped, at which call, and what its calls did before.
    /// </summary>
    public string Describe()
    {
        if (Stop is null)
        {
            return "The script completed.";
        }

        var where = Stop.Sequence is { } sequence
            ? "The script stopped at call " + sequence.ToString(CultureInfo.InvariantCulture) + " (" + Stop.Tool + "): "
            : Stop.Line is { } line
                ? "The script stopped at line " + line.ToString(CultureInfo.InvariantCulture) + ": "
                : "The script stopped: ";
        var before = Calls.Count == 0
            ? " No tool call ran."
            : " Before that, " + Done.ToString(CultureInfo.InvariantCulture) + " call(s) were done and "
                + Proposed.ToString(CultureInfo.InvariantCulture) + " recorded as proposals; nothing after the stop ran.";
        return where + Stop.Message + before;
    }

    private void WriteData(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteString("status", Status);
        if (Completed)
        {
            writer.WritePropertyName("value");
            if (Value is { } value)
            {
                value.WriteTo(writer);
            }
            else
            {
                writer.WriteNullValue();
            }
        }

        writer.WriteStartArray("calls");
        foreach (var call in Calls)
        {
            writer.WriteStartObject();
            writer.WriteNumber("call", call.Sequence);
            writer.WriteString("tool", call.Tool);
            writer.WriteString("outcome", call.Outcome);
            if (call.Code is not null)
            {
                writer.WriteString("code", call.Code);
            }
            if (call.ProposalId is { } proposalId)
            {
                writer.WriteString("proposalId", proposalId);
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WriteNumber("done", Done);
        writer.WriteNumber("proposed", Proposed);
        writer.WriteNumber("roundsSaved", RoundsSaved);
        if (Stop is not null)
        {
            WriteStop(writer, Stop);
        }
        if (Logs.Count > 0)
        {
            writer.WriteStartArray("logs");
            foreach (var log in Logs)
            {
                writer.WriteStringValue(log);
            }
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
    }

    private static void WriteStop(Utf8JsonWriter writer, NhAiScriptStop stop)
    {
        writer.WriteStartObject("stoppedAt");
        writer.WriteString("code", stop.Code);
        if (stop.Sequence is { } sequence)
        {
            writer.WriteNumber("call", sequence);
        }
        if (stop.Tool is not null)
        {
            writer.WriteString("tool", stop.Tool);
        }
        if (stop.Line is { } line)
        {
            writer.WriteNumber("line", line);
        }
        if (stop.Column is { } column)
        {
            writer.WriteNumber("column", column);
        }
        if (stop.CallCode is not null)
        {
            writer.WriteString("callCode", stop.CallCode);
        }
        if (stop.ProposalId is { } proposalId)
        {
            writer.WriteString("proposalId", proposalId);
        }
        if (stop.Signature is not null)
        {
            writer.WriteString("signature", stop.Signature);
        }
        if (stop.FieldError is { } fieldError)
        {
            writer.WriteString("field", fieldError.Field);
            writer.WriteString("error", fieldError.Error);
        }
        writer.WriteEndObject();
    }

    /// <summary>
    /// The JSON schema of the <c>data</c> of the script tool's result.
    /// </summary>
    internal const string DataSchemaJson = """
        {"type":"object","properties":{"status":{"type":"string","enum":["completed","stopped"]},"value":{"description":"What the program returned; present when status is completed."},"calls":{"type":"array","items":{"type":"object","properties":{"call":{"type":"integer"},"tool":{"type":"string"},"outcome":{"type":"string","enum":["done","failed","proposed","not-needed","waiting-for-approval","refused","invalid"]},"code":{"type":"string"},"proposalId":{"type":"string","format":"uuid"}},"required":["call","tool","outcome"]}},"done":{"type":"integer"},"proposed":{"type":"integer"},"roundsSaved":{"type":"integer"},"stoppedAt":{"type":"object","properties":{"code":{"type":"string"},"call":{"type":"integer"},"tool":{"type":"string"},"line":{"type":"integer"},"column":{"type":"integer"},"callCode":{"type":"string"},"proposalId":{"type":"string","format":"uuid"},"signature":{"type":"string"},"field":{"type":"string"},"error":{"type":"string"}},"required":["code"]},"logs":{"type":"array","items":{"type":"string"}}},"required":["status","calls","done","proposed","roundsSaved"]}
        """;
}
