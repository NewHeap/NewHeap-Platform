using System.Globalization;

namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// Context for the optional, user-facing name of an assistant tool.
/// </summary>
public sealed record NhAssistantToolDisplayNameContext(
    NhAiToolDescriptor Descriptor,
    CultureInfo Culture);

/// <summary>
/// Exact governed input available to an optional approval presenter. Presentation never changes
/// the proposal, approval evidence or authorization decision.
/// </summary>
public sealed record NhAssistantApprovalPresentationContext(
    NhAiToolDescriptor Descriptor,
    object Arguments,
    NhAiInvocationContext InvocationContext,
    CultureInfo Culture);

/// <summary>
/// One named, user-facing fact in an approval explanation.
/// </summary>
public sealed record NhAssistantPresentationField(string Label, string Value);

/// <summary>
/// Trusted, localized presentation stored alongside an assistant approval. It must keep within
/// <see cref="NhAssistantPresentationLimits"/>: a presentation that breaks one of them is dropped as a
/// whole, a warning names the presenter, the tool and the broken rule (never content), and the approver
/// reads the generic summary instead.
/// </summary>
public sealed record NhAssistantApprovalPresentation(
    string ToolDisplayName,
    string Summary,
    IReadOnlyList<NhAssistantPresentationField> Fields,
    string? Notice = null);

/// <summary>
/// The bounds of what an <see cref="INhAssistantToolPresenter"/> returns. A display name that breaks
/// them falls back to the tool id; a presentation that breaks one is dropped as a whole, so the approver
/// never decides without fields they were not shown, and the approval shows the generic summary. Each
/// drop logs a warning with the presenter's type, the tool id, the culture and the broken rule
/// (<see cref="NhAssistantPresentationRules"/>), never the text. A presenter that throws or takes longer
/// than <see cref="PresenterTimeout"/> falls back the same way. A tool classified
/// <see cref="NhAiDataClassification.Confidential"/> or above is never presented.
/// </summary>
public static class NhAssistantPresentationLimits
{
    /// <summary>The longest tool display name and <see cref="NhAssistantApprovalPresentation.ToolDisplayName"/>.</summary>
    public const int MaxDisplayNameLength = 160;

    /// <summary>The longest <see cref="NhAssistantApprovalPresentation.Summary"/>.</summary>
    public const int MaxSummaryLength = 512;

    /// <summary>The most <see cref="NhAssistantApprovalPresentation.Fields"/>, so approvers read a short card.</summary>
    public const int MaxFields = 8;

    /// <summary>The longest <see cref="NhAssistantPresentationField.Label"/>.</summary>
    public const int MaxFieldLabelLength = 80;

    /// <summary>The longest <see cref="NhAssistantPresentationField.Value"/>.</summary>
    public const int MaxFieldValueLength = 256;

    /// <summary>The longest <see cref="NhAssistantApprovalPresentation.Notice"/>.</summary>
    public const int MaxNoticeLength = 512;

    /// <summary>The most characters of one presentation as stored JSON.</summary>
    public const int MaxSerializedLength = 4_000;

    /// <summary>
    /// The most UI cultures one approval is presented in: the culture it was made in and the others of
    /// the application's request localization, which <c>NewHeap:PlatformAspNetCommon:Settings:SupportedCultures</c>
    /// configures; cultures beyond it are not presented, with one warning.
    /// </summary>
    public const int MaxCultures = 8;

    /// <summary>How long one presenter call may take before it falls back.</summary>
    public static TimeSpan PresenterTimeout { get; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// The content-free rule names a warning about a dropped presentation or display name names
/// (<see cref="NhAssistantPresentationLimits"/>).
/// </summary>
public static class NhAssistantPresentationRules
{
    public const string DisplayNameMissing = "display-name-missing";
    public const string DisplayNameTooLong = "display-name-too-long";
    public const string SummaryMissing = "summary-missing";
    public const string SummaryTooLong = "summary-too-long";
    public const string FieldsMissing = "fields-missing";
    public const string TooManyFields = "too-many-fields";
    public const string FieldLabelMissing = "field-label-missing";
    public const string FieldLabelTooLong = "field-label-too-long";
    public const string FieldValueMissing = "field-value-missing";
    public const string FieldValueTooLong = "field-value-too-long";
    public const string NoticeTooLong = "notice-too-long";

    /// <summary>The presentation as stored JSON is longer than <see cref="NhAssistantPresentationLimits.MaxSerializedLength"/>.</summary>
    public const string TooLong = "too-long";
}

/// <summary>
/// Optionally presents governed tools and approvals in consumer terminology. Implementations must
/// derive approval text from the supplied arguments and invocation context, not from client input.
/// Return <see langword="null"/> when the presenter does not handle the tool.
/// </summary>
public interface INhAssistantToolPresenter
{
    ValueTask<string?> GetDisplayNameAsync(
        NhAssistantToolDisplayNameContext context,
        CancellationToken cancellationToken);

    ValueTask<NhAssistantApprovalPresentation?> PresentApprovalAsync(
        NhAssistantApprovalPresentationContext context,
        CancellationToken cancellationToken);
}
