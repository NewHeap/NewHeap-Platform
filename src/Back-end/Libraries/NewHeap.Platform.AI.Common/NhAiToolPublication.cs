using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI;

/// <summary>
/// The effective protocol annotation hints of a tool descriptor: the values its governance
/// effect implies, adjusted by explicit hint overrides that may only make them more cautious.
/// </summary>
public sealed record NhAiToolAnnotationHints(
    bool ReadOnly,
    bool Destructive,
    bool Idempotent,
    bool OpenWorld)
{
    /// <summary>
    /// Resolves the effective hints for a descriptor. A hint override that would publish a
    /// less cautious value than the effect implies is a programming error.
    /// </summary>
    public static NhAiToolAnnotationHints Resolve(NhAiToolDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var violation = FindWideningOverride(descriptor);
        if (violation is not null)
        {
            throw new InvalidOperationException(
                $"AI tool '{descriptor.Id}' declares {violation}, which is less cautious than its '{descriptor.Effect}' effect.");
        }

        var implied = FromEffect(descriptor.Effect);
        return new NhAiToolAnnotationHints(
            Apply(implied.ReadOnly, descriptor.ReadOnlyHint),
            Apply(implied.Destructive, descriptor.DestructiveHint),
            Apply(implied.Idempotent, descriptor.IdempotentHint),
            Apply(implied.OpenWorld, descriptor.OpenWorldHint));
    }

    /// <summary>
    /// Returns the hints a governance effect implies without overrides.
    /// </summary>
    public static NhAiToolAnnotationHints FromEffect(NhAiToolEffect effect)
    {
        return new NhAiToolAnnotationHints(
            effect == NhAiToolEffect.ReadOnly,
            effect == NhAiToolEffect.Destructive,
            effect is NhAiToolEffect.ReadOnly or NhAiToolEffect.IdempotentMutation,
            effect == NhAiToolEffect.ExternalSideEffect);
    }

    /// <summary>
    /// Describes the first override that would widen the effect, or <see langword="null"/>
    /// when every override is at least as cautious as the effect.
    /// </summary>
    public static string? FindWideningOverride(NhAiToolDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var implied = FromEffect(descriptor.Effect);
        if (descriptor.ReadOnlyHint == NhAiToolHint.True && !implied.ReadOnly)
        {
            return "ReadOnlyHint = True";
        }
        if (descriptor.DestructiveHint == NhAiToolHint.False && implied.Destructive)
        {
            return "DestructiveHint = False";
        }
        if (descriptor.IdempotentHint == NhAiToolHint.True && !implied.Idempotent)
        {
            return "IdempotentHint = True";
        }
        if (descriptor.OpenWorldHint == NhAiToolHint.False && implied.OpenWorld)
        {
            return "OpenWorldHint = False";
        }
        return null;
    }

    /// <summary>
    /// A bounded audit code listing the explicit overrides, such as
    /// <c>destructive-hint=true</c>, or <see langword="null"/> when none are declared.
    /// </summary>
    public static string? DescribeOverrides(NhAiToolDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var overrides = new List<string>(4);
        AddOverride(overrides, "read-only-hint", descriptor.ReadOnlyHint);
        AddOverride(overrides, "destructive-hint", descriptor.DestructiveHint);
        AddOverride(overrides, "idempotent-hint", descriptor.IdempotentHint);
        AddOverride(overrides, "open-world-hint", descriptor.OpenWorldHint);
        return overrides.Count == 0 ? null : string.Join(";", overrides);
    }

    private static bool Apply(bool implied, NhAiToolHint hint)
    {
        return hint switch
        {
            NhAiToolHint.True => true,
            NhAiToolHint.False => false,
            _ => implied
        };
    }

    private static void AddOverride(List<string> overrides, string name, NhAiToolHint hint)
    {
        if (hint == NhAiToolHint.True)
        {
            overrides.Add(name + "=true");
        }
        else if (hint == NhAiToolHint.False)
        {
            overrides.Add(name + "=false");
        }
    }
}

/// <summary>
/// JSON options applied to generated tools that do not declare a
/// <c>JsonSerializerContext</c>.
/// </summary>
public static class NhAiToolJsonSerializerOptions
{
    /// <summary>
    /// Deterministic options for flat exports without a declared context: web naming
    /// (camelCase), string enums, relaxed escaping, compact output and every property written,
    /// including nulls. Declare a context on the tool set when a different wire contract applies.
    /// </summary>
    public static JsonSerializerOptions FlatExport { get; } = CreateFlatExport();

    private static JsonSerializerOptions CreateFlatExport()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = false,
            TypeInfoResolver = AIJsonUtilities.DefaultOptions.TypeInfoResolver
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.MakeReadOnly();
        return options;
    }
}

/// <summary>
/// Carries the safe failure detail of the last governed invocation in the current
/// asynchronous flow to a protocol adapter that only sees the serialized result.
/// </summary>
internal sealed class NhAiToolOutcomeCapture
{
    private static readonly AsyncLocal<NhAiToolOutcomeCapture?> CurrentCapture = new();

    public bool Recorded { get; private set; }

    public bool Succeeded { get; private set; }

    public string? Code { get; private set; }

    public string? Message { get; private set; }

    public string? EvidenceReference { get; private set; }

    /// <summary>
    /// Starts capturing for the calling asynchronous flow. The capture is visible to every
    /// awaited call below the caller and ends when the calling method returns.
    /// </summary>
    public static NhAiToolOutcomeCapture Begin()
    {
        var capture = new NhAiToolOutcomeCapture();
        CurrentCapture.Value = capture;
        return capture;
    }

    public static void Record(TaskResult result, string? evidenceReference)
    {
        var capture = CurrentCapture.Value;
        if (capture is null)
        {
            return;
        }

        capture.Recorded = true;
        capture.Succeeded = result.Success;
        if (result.Success)
        {
            capture.Code = null;
            capture.Message = null;
            capture.EvidenceReference = null;
            return;
        }

        var items = result.GetResultItems();
        var named = items.FirstOrDefault(item => NhAiNames.IsSegment(item.Name));
        capture.Code = named?.Name ?? NhAiToolFailureCodes.Failed;
        capture.Message = string.Join(
            " ",
            (named ?? items.FirstOrDefault())?.ErrorMessages.Select(message => message.ToString())
                ?? []);
        if (string.IsNullOrWhiteSpace(capture.Message))
        {
            capture.Message = null;
        }
        capture.EvidenceReference = evidenceReference;
    }
}

/// <summary>
/// Stable failure codes for outcomes owned by the shared invocation pipeline.
/// </summary>
public static class NhAiToolFailureCodes
{
    public const string Failed = "ai-tool-failed";
    public const string AuthorizationDenied = "ai-tool-authorization-denied";
    public const string InputTooLarge = "ai-tool-input-too-large";
    public const string CapabilityDenied = "ai-tool-capability-denied";
    public const string ExecutionEvidenceInvalid = "ai-tool-execution-evidence-invalid";
    public const string EffectDenied = "ai-tool-effect-denied";
    public const string ApprovalDelegationInvalid = "ai-tool-approval-delegation-invalid";
    public const string ApprovalRequired = "ai-tool-approval-required";
    public const string ApprovalInvalid = "ai-tool-approval-invalid";
    public const string BudgetExhausted = "ai-tool-budget-exhausted";
    public const string BudgetDenied = "ai-tool-budget-denied";
    public const string ConcurrencyLimited = "ai-tool-concurrency-limited";
    public const string DeadlineExpired = "ai-tool-deadline-expired";
    public const string IdempotencyKeyInvalid = "ai-tool-idempotency-key-invalid";
    public const string IdempotencyDenied = "ai-tool-idempotency-denied";
    public const string ResultTooLarge = "ai-tool-result-too-large";
    public const string VerificationFailed = "ai-tool-verification-failed";
}
