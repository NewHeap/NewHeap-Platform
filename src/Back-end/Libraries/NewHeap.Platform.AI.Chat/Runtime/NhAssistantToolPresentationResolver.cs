using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace NewHeap.Platform.AI.Chat.Runtime;

internal sealed class NhAssistantToolPresentationResolver(
    IEnumerable<INhAssistantToolPresenter> presenters,
    ILogger<NhAssistantToolPresentationResolver> logger)
{
    public const int MaxDisplayNameLength = 160;
    public const int MaxSummaryLength = 512;
    public const int MaxFieldCount = 8;
    public const int MaxFieldLabelLength = 80;
    public const int MaxFieldValueLength = 256;
    public const int MaxNoticeLength = 512;
    public const int MaxSerializedLength = 4_000;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);
    private readonly IReadOnlyList<INhAssistantToolPresenter> _presenters = presenters.ToArray();

    public async Task<string> ResolveDisplayNameAsync(
        NhAiToolDescriptor descriptor,
        CultureInfo culture,
        CancellationToken cancellationToken)
    {
        foreach (var presenter in _presenters)
        {
            var result = await TryInvokeAsync(
                presenter,
                token => presenter.GetDisplayNameAsync(
                    new NhAssistantToolDisplayNameContext(descriptor, culture),
                    token),
                descriptor.Id,
                cancellationToken);
            if (result is { Length: > 0 and <= MaxDisplayNameLength })
            {
                return result;
            }
        }

        return descriptor.Id;
    }

    public async Task<NhAssistantApprovalPresentation?> ResolveApprovalAsync(
        NhAiToolDescriptor descriptor,
        object arguments,
        NhAiInvocationContext invocationContext,
        CultureInfo culture,
        CancellationToken cancellationToken)
    {
        if (descriptor.DataClassification >= NhAiDataClassification.Confidential)
        {
            return null;
        }

        foreach (var presenter in _presenters)
        {
            var result = await TryInvokeAsync(
                presenter,
                token => presenter.PresentApprovalAsync(
                    new NhAssistantApprovalPresentationContext(
                        descriptor,
                        arguments,
                        invocationContext,
                        culture),
                    token),
                descriptor.Id,
                cancellationToken);
            if (IsValid(result))
            {
                return result;
            }
        }

        return null;
    }

    public static string? Serialize(NhAssistantApprovalPresentation? presentation)
    {
        if (!IsValid(presentation))
        {
            return null;
        }

        return JsonSerializer.Serialize(presentation, NhAssistantContent.JsonOptions);
    }

    public static NhAssistantApprovalPresentation? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var presentation = JsonSerializer.Deserialize<NhAssistantApprovalPresentation>(
                json,
                NhAssistantContent.JsonOptions);
            return IsValid(presentation) ? presentation : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsValid(NhAssistantApprovalPresentation? presentation)
    {
        if (presentation is null
            || string.IsNullOrWhiteSpace(presentation.ToolDisplayName)
            || presentation.ToolDisplayName.Length > MaxDisplayNameLength
            || string.IsNullOrWhiteSpace(presentation.Summary)
            || presentation.Summary.Length > MaxSummaryLength
            || presentation.Fields is null
            || presentation.Fields.Count > MaxFieldCount
            || presentation.Fields.Any(field =>
                string.IsNullOrWhiteSpace(field.Label)
                || field.Label.Length > MaxFieldLabelLength
                || string.IsNullOrWhiteSpace(field.Value)
                || field.Value.Length > MaxFieldValueLength)
            || presentation.Notice?.Length > MaxNoticeLength)
        {
            return false;
        }

        return JsonSerializer.Serialize(presentation, NhAssistantContent.JsonOptions).Length
            <= MaxSerializedLength;
    }

    private async Task<T?> TryInvokeAsync<T>(
        INhAssistantToolPresenter presenter,
        Func<CancellationToken, ValueTask<T?>> invoke,
        string toolId,
        CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);
            return await invoke(timeout.Token).AsTask().WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "Assistant tool presenter {PresenterType} timed out for tool {ToolId}.",
                presenter.GetType().FullName,
                toolId);
            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            logger.LogWarning(
                "Assistant tool presenter {PresenterType} failed for tool {ToolId} with {ExceptionType}.",
                presenter.GetType().FullName,
                toolId,
                exception.GetType().FullName ?? exception.GetType().Name);
            return null;
        }
    }
}
