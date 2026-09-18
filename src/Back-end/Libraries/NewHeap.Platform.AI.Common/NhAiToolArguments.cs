using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI;

/// <summary>
/// Normalizes and rejects the arguments of governed functions. Only argument names are ever
/// reported; argument values never reach a message, an audit record or a log.
/// </summary>
internal static class NhAiToolArguments
{
    public const string InputPropertyName = "input";
    private const int MaxReportedNames = 8;
    private const int MaxReportedNameLength = 64;
    private static readonly AsyncLocal<BindingScope?> CurrentBinding = new();

    public static bool HasInputEnvelope(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object
            || !schema.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var names = properties.EnumerateObject().Select(property => property.Name).ToArray();
        return names.Length == 1 && string.Equals(names[0], InputPropertyName, StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns the arguments to bind, or null with the unexpected top-level names when the
    /// envelope is combined with other properties.
    /// </summary>
    public static NhAiToolArgumentShape Normalize(AIFunctionArguments arguments, bool enveloped)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!enveloped)
        {
            return new NhAiToolArgumentShape(arguments, []);
        }

        if (arguments.ContainsKey(InputPropertyName))
        {
            var unexpected = arguments.Keys
                .Where(name => !string.Equals(name, InputPropertyName, StringComparison.Ordinal))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            return unexpected.Length == 0
                ? new NhAiToolArgumentShape(arguments, [])
                : new NhAiToolArgumentShape(null, unexpected);
        }

        // The caller sent the input properties at the top level: the whole object is the input.
        var flat = new Dictionary<string, object?>(arguments, StringComparer.Ordinal);
        var input = JsonSerializer.SerializeToElement(flat, AIJsonUtilities.DefaultOptions);
        var normalized = new AIFunctionArguments(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [InputPropertyName] = input
        })
        {
            Services = arguments.Services,
            Context = arguments.Context
        };
        return new NhAiToolArgumentShape(normalized, []);
    }

    public static BindingScope BeginBinding()
    {
        var scope = new BindingScope();
        CurrentBinding.Value = scope;
        return scope;
    }

    /// <summary>
    /// Called by the shared invoker when a governed invocation starts, so an exception raised
    /// afterwards is never mistaken for an argument-binding failure.
    /// </summary>
    public static void MarkInvokerEntered()
    {
        if (CurrentBinding.Value is { } scope)
        {
            scope.InvokerEntered = true;
        }
    }

    public static async ValueTask<object?> RejectAsync(
        NhAiToolDescriptor descriptor,
        IReadOnlyList<string> unexpectedNames,
        IServiceProvider? services,
        CancellationToken cancellationToken)
    {
        var message = CreateMessage(unexpectedNames);
        NhAiToolOutcomeCapture.Record(TaskResult.Failed(NhAiToolFailureCodes.InputInvalid, message), null);

        if (services is not null)
        {
            var record = new NhAiAuditRecord(
                Guid.NewGuid(),
                descriptor.Id,
                descriptor.Version,
                null,
                null,
                NhAiOutcomeKind.TerminalFailure,
                DateTimeOffset.UtcNow)
            {
                ResultCode = NhAiToolFailureCodes.InputInvalid
            };
            foreach (var sink in services.GetServices<INhAiAuditSink>())
            {
                await sink.WriteAsync(record, cancellationToken);
            }
        }

        return JsonSerializer.SerializeToElement(
            new Dictionary<string, object?>
            {
                ["success"] = false,
                ["code"] = NhAiToolFailureCodes.InputInvalid,
                ["message"] = message
            },
            AIJsonUtilities.DefaultOptions);
    }

    private static string CreateMessage(IReadOnlyList<string> unexpectedNames)
    {
        const string expected = "Send the arguments as {\"input\": {...}} with every input property inside \"input\".";
        if (unexpectedNames.Count == 0)
        {
            return "The tool arguments do not match the tool input. " + expected;
        }

        var names = unexpectedNames
            .Take(MaxReportedNames)
            .Select(name => name.Length <= MaxReportedNameLength ? name : name[..MaxReportedNameLength]);
        return "The tool arguments combine \"input\" with other top-level properties ("
            + string.Join(", ", names)
            + (unexpectedNames.Count > MaxReportedNames ? ", ..." : string.Empty)
            + "). "
            + expected;
    }

    internal sealed class BindingScope
    {
        public bool InvokerEntered { get; set; }
    }
}

internal sealed record NhAiToolArgumentShape(
    AIFunctionArguments? Arguments,
    IReadOnlyList<string> UnexpectedNames);
