using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using NewHeap.Platform.AI.Chat.Runtime;

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
/// One named, user-facing fact in an approval explanation or a tool result card. A field of a change
/// may also state what it holds now (<see cref="CurrentValue"/>), so the approver sees what changes:
/// the current value next to the proposed <see cref="Value"/>.
/// </summary>
/// <param name="Label">What the field is, such as "Status".</param>
/// <param name="Value">
/// The proposed or shown value. It may be empty only when <see cref="CurrentValue"/> is not: the
/// change then clears the field.
/// </param>
public sealed record NhAssistantPresentationField(string Label, string Value)
{
    /// <summary>
    /// What the field holds now, when the presenter states it: <see langword="null"/> for a plain fact
    /// (the default), an empty string for a field that is empty now and gets <see cref="Value"/>, or the
    /// current value. People then read the change from it to <see cref="Value"/> (<see cref="Change"/>).
    /// It keeps within <see cref="NhAssistantPresentationLimits.MaxFieldValueLength"/>, as the value does.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CurrentValue { get; init; }

    /// <summary>
    /// How the field changes, read from <see cref="CurrentValue"/> and <see cref="Value"/>:
    /// <see cref="NhAssistantPresentationFieldChange.None"/> for a plain fact.
    /// </summary>
    [JsonIgnore]
    public NhAssistantPresentationFieldChange Change
    {
        get
        {
            if (CurrentValue is null)
            {
                return NhAssistantPresentationFieldChange.None;
            }

            var hadValue = !string.IsNullOrWhiteSpace(CurrentValue);
            var hasValue = !string.IsNullOrWhiteSpace(Value);
            if (hadValue && hasValue)
            {
                return string.Equals(CurrentValue, Value, StringComparison.Ordinal)
                    ? NhAssistantPresentationFieldChange.Unchanged
                    : NhAssistantPresentationFieldChange.Changed;
            }

            return hasValue ? NhAssistantPresentationFieldChange.Added : NhAssistantPresentationFieldChange.Cleared;
        }
    }

    /// <summary>
    /// A field of a change from <paramref name="currentValue"/> to <paramref name="proposedValue"/>; a
    /// <see langword="null"/> or empty value on either side stands for an empty field, so the field is
    /// <see cref="NhAssistantPresentationFieldChange.Added"/> or <see cref="NhAssistantPresentationFieldChange.Cleared"/>.
    /// </summary>
    public static NhAssistantPresentationField Changing(string label, string? currentValue, string? proposedValue)
    {
        return new NhAssistantPresentationField(label, proposedValue ?? string.Empty)
        {
            CurrentValue = currentValue ?? string.Empty
        };
    }
}

/// <summary>
/// How a <see cref="NhAssistantPresentationField"/> changes. Clients show it in words, not only in
/// color.
/// </summary>
public enum NhAssistantPresentationFieldChange
{
    /// <summary>The field is a plain fact; the presenter states no current value.</summary>
    None = 0,

    /// <summary>The field keeps its current value.</summary>
    Unchanged = 1,

    /// <summary>The field is empty now and gets a value.</summary>
    Added = 2,

    /// <summary>The field changes from its current value to another.</summary>
    Changed = 3,

    /// <summary>The field has a value now and the change clears it.</summary>
    Cleared = 4
}

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
/// What an optional result presenter (<see cref="INhAssistantToolResultPresenter"/>) receives about one
/// tool call that succeeded in a turn: the tool, the data it returned and the culture to present in. The
/// presenter runs with the current culture set to <paramref name="Culture"/>, in the request scope of the
/// turn.
/// </summary>
/// <param name="Descriptor">The tool that ran.</param>
/// <param name="Data">
/// What the tool returned: the <c>data</c> of a governed tool's result, otherwise the whole result. It is
/// the authorized output of the call; present it in the application's own words, never as raw JSON.
/// </param>
/// <param name="Culture">The culture to present in.</param>
public sealed record NhAssistantToolResultPresentationContext(
    NhAiToolDescriptor Descriptor,
    JsonElement Data,
    CultureInfo Culture)
{
    private static readonly JsonSerializerOptions DataOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// The arguments the model supplied, as the call stored them; <see langword="null"/> when they were
    /// too long to store. They are untrusted model output: read what the presentation shows from
    /// <see cref="Data"/> or the application, never from the arguments alone.
    /// </summary>
    public JsonElement? Arguments { get; init; }

    /// <summary>
    /// <see cref="Data"/> as <typeparamref name="T"/>, read with web (camelCase) names and enums as names
    /// or numbers; <see langword="default"/> when it does not have that shape.
    /// </summary>
    public T? ReadData<T>()
    {
        return ReadData<T>(DataOptions);
    }

    /// <summary>
    /// <see cref="Data"/> as <typeparamref name="T"/>, read with <paramref name="options"/>, such as the
    /// options the tool's catalog serializes with; <see langword="default"/> when it does not have that shape.
    /// </summary>
    public T? ReadData<T>(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            return Data.Deserialize<T>(options);
        }
        catch (JsonException)
        {
            return default;
        }
        catch (NotSupportedException)
        {
            return default;
        }
    }
}

/// <summary>
/// A link of a tool result card to a page of the application, such as <c>/projects/42</c>: a path
/// within the application, which the chat opens through the application's router, with the text people
/// read. An address outside the application is not a valid link.
/// </summary>
/// <param name="Url">The path, starting with one <c>/</c>, at most <see cref="NhAssistantPresentationLimits.MaxLinkUrlLength"/> characters.</param>
/// <param name="Label">The text of the link, at most <see cref="NhAssistantPresentationLimits.MaxLinkLabelLength"/> characters.</param>
public sealed record NhAssistantPresentationLink(string Url, string Label);

/// <summary>
/// Trusted, localized card of what a completed tool call returned, which the chat shows next to the call
/// and stores with the conversation, so a reloaded conversation and the other people of a shared one see
/// it too. It must keep within <see cref="NhAssistantPresentationLimits"/>: a card that breaks one of them
/// is dropped as a whole with a warning that names the presenter, the tool and the broken rule (never
/// content), and the chat shows the call without a card. It never changes what the model reads.
/// </summary>
/// <param name="Title">What the card shows, such as the name of the project the tool read.</param>
/// <param name="Fields">At most <see cref="NhAssistantPresentationLimits.MaxFields"/> named facts.</param>
/// <param name="Summary">An optional sentence under the title, such as "3 open tasks".</param>
/// <param name="Link">An optional link to the page of the application that shows the result.</param>
public sealed record NhAssistantToolResultPresentation(
    string Title,
    IReadOnlyList<NhAssistantPresentationField> Fields,
    string? Summary = null,
    NhAssistantPresentationLink? Link = null);

/// <summary>
/// The bounds of what an <see cref="INhAssistantToolPresenter"/> or <see cref="INhAssistantToolResultPresenter"/>
/// returns. A display name that breaks them falls back to the tool id; a presentation that breaks one is
/// dropped as a whole, so the approver never decides without fields they were not shown, and the
/// approval shows the generic summary; a result card that breaks one is not shown. Each drop logs a
/// warning with the presenter's type, the tool id, the culture and the broken rule
/// (<see cref="NhAssistantPresentationRules"/>), never the text. A presenter that throws or takes longer
/// than <see cref="PresenterTimeout"/> falls back the same way. A tool classified
/// <see cref="NhAiDataClassification.Confidential"/> or above is never presented. Test a presenter's
/// result with <see cref="FindBrokenRule"/> or
/// <see cref="FindBrokenResultRule"/>, the checks that decide the drop.
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

    /// <summary>The longest <see cref="NhAssistantToolResultPresentation.Title"/>.</summary>
    public const int MaxTitleLength = 160;

    /// <summary>The longest <see cref="NhAssistantPresentationLink.Url"/>.</summary>
    public const int MaxLinkUrlLength = 512;

    /// <summary>The longest <see cref="NhAssistantPresentationLink.Label"/>.</summary>
    public const int MaxLinkLabelLength = 80;

    /// <summary>The most characters of one presentation as stored JSON.</summary>
    public const int MaxSerializedLength = 4_000;

    /// <summary>
    /// The most UI cultures one approval or result card is presented in: the culture it was made in and
    /// the others of the application's request localization, which
    /// <c>NewHeap:PlatformAspNetCommon:Settings:SupportedCultures</c> configures; cultures beyond it are
    /// not presented, with one warning.
    /// </summary>
    public const int MaxCultures = 8;

    /// <summary>How long one presenter call may take before it falls back.</summary>
    public static TimeSpan PresenterTimeout { get; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The first of these limits that <paramref name="presentation"/> breaks, as a
    /// <see cref="NhAssistantPresentationRules"/> name, or <see langword="null"/> when it keeps within all
    /// of them. This is the check the assistant drops a presenter's result by, so a presenter's unit test
    /// can call it with the longest texts the application allows and fail before production drops the
    /// presentation for the generic summary.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="presentation"/> is <see langword="null"/>.</exception>
    public static string? FindBrokenRule(NhAssistantApprovalPresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(presentation);

        if (string.IsNullOrWhiteSpace(presentation.ToolDisplayName))
        {
            return NhAssistantPresentationRules.DisplayNameMissing;
        }
        if (presentation.ToolDisplayName.Length > MaxDisplayNameLength)
        {
            return NhAssistantPresentationRules.DisplayNameTooLong;
        }
        if (string.IsNullOrWhiteSpace(presentation.Summary))
        {
            return NhAssistantPresentationRules.SummaryMissing;
        }
        if (presentation.Summary.Length > MaxSummaryLength)
        {
            return NhAssistantPresentationRules.SummaryTooLong;
        }
        if (FindBrokenFieldRule(presentation.Fields) is { } fieldRule)
        {
            return fieldRule;
        }
        if (presentation.Notice?.Length > MaxNoticeLength)
        {
            return NhAssistantPresentationRules.NoticeTooLong;
        }

        // The stored JSON counts escaped characters, so texts within their own caps can still be too long.
        var serialized = JsonSerializer.Serialize(presentation, NhAssistantContent.JsonOptions);
        if (serialized.Length > MaxSerializedLength)
        {
            return NhAssistantPresentationRules.TooLong;
        }

        return null;
    }

    /// <summary>
    /// The first of these limits that the result card <paramref name="presentation"/> breaks, as a
    /// <see cref="NhAssistantPresentationRules"/> name, or <see langword="null"/> when it keeps within all
    /// of them: the check the assistant drops an <see cref="INhAssistantToolResultPresenter"/>'s card by,
    /// so a presenter's unit test can call it with the longest texts the application allows.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="presentation"/> is <see langword="null"/>.</exception>
    public static string? FindBrokenResultRule(NhAssistantToolResultPresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(presentation);

        if (string.IsNullOrWhiteSpace(presentation.Title))
        {
            return NhAssistantPresentationRules.TitleMissing;
        }
        if (presentation.Title.Length > MaxTitleLength)
        {
            return NhAssistantPresentationRules.TitleTooLong;
        }
        if (presentation.Summary?.Length > MaxSummaryLength)
        {
            return NhAssistantPresentationRules.SummaryTooLong;
        }
        if (FindBrokenFieldRule(presentation.Fields) is { } fieldRule)
        {
            return fieldRule;
        }
        if (presentation.Link is { } link)
        {
            if (!IsApplicationPath(link.Url))
            {
                return NhAssistantPresentationRules.LinkInvalid;
            }
            if (string.IsNullOrWhiteSpace(link.Label))
            {
                return NhAssistantPresentationRules.LinkLabelMissing;
            }
            if (link.Label.Length > MaxLinkLabelLength)
            {
                return NhAssistantPresentationRules.LinkLabelTooLong;
            }
        }

        // The stored JSON counts escaped characters, so texts within their own caps can still be too long.
        var serialized = JsonSerializer.Serialize(presentation, NhAssistantContent.JsonOptions);
        if (serialized.Length > MaxSerializedLength)
        {
            return NhAssistantPresentationRules.TooLong;
        }

        return null;
    }

    /// <summary>
    /// The first rule the fields break: at most <see cref="MaxFields"/>, each with a label and a value
    /// within their caps. A field may leave its value empty only when it states a current value, which
    /// the change then clears.
    /// </summary>
    private static string? FindBrokenFieldRule(IReadOnlyList<NhAssistantPresentationField>? fields)
    {
        if (fields is null)
        {
            return NhAssistantPresentationRules.FieldsMissing;
        }
        if (fields.Count > MaxFields)
        {
            return NhAssistantPresentationRules.TooManyFields;
        }

        foreach (var field in fields)
        {
            if (field is null || string.IsNullOrWhiteSpace(field.Label))
            {
                return NhAssistantPresentationRules.FieldLabelMissing;
            }
            if (field.Label.Length > MaxFieldLabelLength)
            {
                return NhAssistantPresentationRules.FieldLabelTooLong;
            }
            if (field.Value is null || (string.IsNullOrWhiteSpace(field.Value) && string.IsNullOrWhiteSpace(field.CurrentValue)))
            {
                return NhAssistantPresentationRules.FieldValueMissing;
            }
            if (field.Value.Length > MaxFieldValueLength)
            {
                return NhAssistantPresentationRules.FieldValueTooLong;
            }
            if (field.CurrentValue?.Length > MaxFieldValueLength)
            {
                return NhAssistantPresentationRules.FieldCurrentValueTooLong;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether <paramref name="url"/> is a path within the application: one leading <c>/</c>, no
    /// backslash, whitespace or control characters, at most <see cref="MaxLinkUrlLength"/> characters.
    /// </summary>
    private static bool IsApplicationPath(string? url)
    {
        if (string.IsNullOrEmpty(url) || url.Length > MaxLinkUrlLength || url[0] != '/')
        {
            return false;
        }
        if (url.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }

        return !url.Any(character => char.IsControl(character) || char.IsWhiteSpace(character) || character == '\\');
    }
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

    /// <summary>
    /// A field's <see cref="NhAssistantPresentationField.CurrentValue"/> is longer than
    /// <see cref="NhAssistantPresentationLimits.MaxFieldValueLength"/>.
    /// </summary>
    public const string FieldCurrentValueTooLong = "field-current-value-too-long";

    public const string NoticeTooLong = "notice-too-long";

    /// <summary>A result card has no <see cref="NhAssistantToolResultPresentation.Title"/>.</summary>
    public const string TitleMissing = "title-missing";

    /// <summary>A result card's title is longer than <see cref="NhAssistantPresentationLimits.MaxTitleLength"/>.</summary>
    public const string TitleTooLong = "title-too-long";

    /// <summary>
    /// A result card's link is no path within the application, or longer than
    /// <see cref="NhAssistantPresentationLimits.MaxLinkUrlLength"/>.
    /// </summary>
    public const string LinkInvalid = "link-invalid";

    /// <summary>A result card's link has no text.</summary>
    public const string LinkLabelMissing = "link-label-missing";

    /// <summary>A result card's link text is longer than <see cref="NhAssistantPresentationLimits.MaxLinkLabelLength"/>.</summary>
    public const string LinkLabelTooLong = "link-label-too-long";

    /// <summary>The presentation as stored JSON is longer than <see cref="NhAssistantPresentationLimits.MaxSerializedLength"/>.</summary>
    public const string TooLong = "too-long";

    /// <summary>
    /// A presentation the application prepared names no valid culture, or two of them name the same one.
    /// </summary>
    public const string CultureInvalid = "culture-invalid";

    /// <summary>
    /// The application prepared presentations in more than <see cref="NhAssistantPresentationLimits.MaxCultures"/> cultures.
    /// </summary>
    public const string TooManyCultures = "too-many-cultures";

    /// <summary>
    /// The application prepared a presentation for a tool classified
    /// <see cref="NhAiDataClassification.Confidential"/> or above, which is never presented.
    /// </summary>
    public const string NotPresented = "not-presented";
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

/// <summary>
/// Optionally presents what a tool call returned as a trusted, localized card (generative UI): the chat
/// shows it next to the call instead of only the tool's name, and stores it with the conversation. The
/// assistant asks for a card for every call that succeeded in a turn, never for a tool classified
/// <see cref="NhAiDataClassification.Confidential"/> or above, and in every supported UI culture when the
/// presenter presents the call, with the current culture set to the one it presents in. A card keeps
/// within <see cref="NhAssistantPresentationLimits"/>; one that breaks a limit, a failure or a presenter
/// that takes longer than <see cref="NhAssistantPresentationLimits.PresenterTimeout"/> shows no card, with
/// a content-free warning. The card never reaches the model and never changes a proposal, approval
/// evidence or authorization; a viewer from whom the call's result is shielded sees no card either.
/// Register it with <see cref="NhAssistantBuilder.AddToolResultPresenter{TPresenter}"/>. Return
/// <see langword="null"/> when the presenter does not present the tool.
/// </summary>
public interface INhAssistantToolResultPresenter
{
    ValueTask<NhAssistantToolResultPresentation?> PresentResultAsync(
        NhAssistantToolResultPresentationContext context,
        CancellationToken cancellationToken);
}
