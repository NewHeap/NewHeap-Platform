using System.Net;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI;

/// <summary>
/// What a model call that failed at the provider tells about its cause: whether a later call may
/// succeed, or the model client's configuration or the request must change first. The failure code
/// stays <see cref="NhAiModelFailureCodes.DependencyUnavailable"/> for every kind.
/// </summary>
public enum NhAiModelProviderFailureKind
{
    /// <summary>
    /// The failure carries no status or exception type that tells more.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// Rate limiting (429), a request timeout (408), a server error (5xx), a timeout or a broken
    /// connection: a retry-eligible profile retries it, and a later call may succeed.
    /// </summary>
    Transient = 1,

    /// <summary>
    /// The provider rejected the credentials (401) or their rights (403): no retry helps until the
    /// model client's configuration changes.
    /// </summary>
    Authentication = 2,

    /// <summary>
    /// The provider rejected the request itself with another 4xx status, for example an unknown model
    /// or deployment, a request that is too large or an option the model does not support.
    /// </summary>
    Request = 3
}

/// <summary>
/// The content-free diagnostics of a governed model call that failed at the provider: the failed
/// result of <see cref="INhAiChatExecutor"/> (<see cref="Of"/>), its usage record
/// (<see cref="NhAiUsageRecord.ProviderFailure"/>) and its warning log carry them next to the
/// unchanged failure code <see cref="NhAiModelFailureCodes.DependencyUnavailable"/>. They never hold
/// the provider's message, response body or headers.
/// </summary>
/// <param name="Kind">What the status or exception tells about the cause.</param>
/// <param name="StatusCode">
/// The HTTP status the provider answered with, between 100 and 599; <see langword="null"/> when the
/// call got no answer or no reader knows the exception.
/// </param>
/// <param name="ProviderCode">
/// The provider's own error code, bounded to 64 letters, digits, dashes, underscores and dots, such as
/// <c>invalid_api_key</c> from an <see cref="INhAiModelProviderFailureReader"/>, or the
/// <see cref="HttpRequestError"/> of a call that got no answer, such as <c>ConnectionError</c>.
/// </param>
/// <param name="ExceptionType">
/// The full type name of the exception the details came from, or of the exception the provider threw
/// when nothing in its chain is known.
/// </param>
public sealed record NhAiModelProviderFailure(
    NhAiModelProviderFailureKind Kind,
    int? StatusCode,
    string? ProviderCode,
    string ExceptionType)
{
    private const int MaxProviderCodeLength = 64;
    private const int MaxExceptionTypeLength = 256;
    private const int MaxInnerExceptionDepth = 16;

    /// <summary>
    /// The provider failure a failed result of <see cref="INhAiChatExecutor"/> carries: the result of
    /// <see cref="INhAiChatExecutor.GetResponseAsync"/> or <see cref="NhAiChatStream.Completion"/> with
    /// code <see cref="NhAiModelFailureCodes.DependencyUnavailable"/>. Returns <see langword="null"/> for
    /// any other result, also for a result another <see cref="TaskResult"/> was copied from, which keeps
    /// only the code and message.
    /// </summary>
    public static NhAiModelProviderFailure? Of(TaskResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result is INhAiModelProviderFailureResult failed && !result.Success
            ? failed.ProviderFailure
            : null;
    }

    /// <summary>
    /// The exception the provider threw for a failed result of the executor. It stays inside the
    /// libraries: its message can carry provider text, so only an exception's
    /// <see cref="Exception.InnerException"/> passes it on.
    /// </summary>
    internal static Exception? ExceptionOf(TaskResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result is INhAiModelProviderFailureResult failed && !result.Success
            ? failed.ProviderException
            : null;
    }

    /// <summary>
    /// Reads the failure from the exception chain, outermost first. At each exception the registered
    /// readers answer first, in their registration order; then an <see cref="HttpRequestException"/>
    /// gives its status, or its <see cref="HttpRequestError"/> when it got no answer, and a
    /// <see cref="TimeoutException"/> or <see cref="IOException"/> counts as transient.
    /// </summary>
    internal static NhAiModelProviderFailure Read(
        Exception exception,
        IReadOnlyList<INhAiModelProviderFailureReader> readers)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(readers);

        var current = exception;
        for (var depth = 0; current is not null && depth < MaxInnerExceptionDepth; depth++)
        {
            foreach (var reader in readers)
            {
                var error = reader.Read(current);
                var statusCode = SafeStatusCode(error?.StatusCode);
                var providerCode = SafeProviderCode(error?.ProviderCode);
                if (statusCode is not null || providerCode is not null)
                {
                    return new NhAiModelProviderFailure(
                        Classify(statusCode, transientWithoutStatus: false),
                        statusCode,
                        providerCode,
                        TypeName(current));
                }
            }

            switch (current)
            {
                case HttpRequestException http:
                {
                    var statusCode = SafeStatusCode((int?)http.StatusCode);
                    var providerCode = statusCode is null && http.HttpRequestError != HttpRequestError.Unknown
                        ? http.HttpRequestError.ToString()
                        : null;
                    return new NhAiModelProviderFailure(
                        Classify(statusCode, transientWithoutStatus: true),
                        statusCode,
                        providerCode,
                        TypeName(current));
                }
                case TimeoutException:
                case IOException:
                    return new NhAiModelProviderFailure(
                        NhAiModelProviderFailureKind.Transient,
                        null,
                        null,
                        TypeName(current));
            }

            current = current.InnerException;
        }

        return new NhAiModelProviderFailure(
            NhAiModelProviderFailureKind.Unknown,
            null,
            null,
            TypeName(exception));
    }

    private static NhAiModelProviderFailureKind Classify(int? statusCode, bool transientWithoutStatus)
    {
        if (statusCode is not { } status)
        {
            return transientWithoutStatus
                ? NhAiModelProviderFailureKind.Transient
                : NhAiModelProviderFailureKind.Unknown;
        }

        if (status == (int)HttpStatusCode.TooManyRequests
            || status == (int)HttpStatusCode.RequestTimeout
            || status >= 500)
        {
            return NhAiModelProviderFailureKind.Transient;
        }

        if (status == (int)HttpStatusCode.Unauthorized || status == (int)HttpStatusCode.Forbidden)
        {
            return NhAiModelProviderFailureKind.Authentication;
        }

        return status >= 400
            ? NhAiModelProviderFailureKind.Request
            : NhAiModelProviderFailureKind.Unknown;
    }

    private static int? SafeStatusCode(int? statusCode)
    {
        return statusCode is >= 100 and <= 599 ? statusCode : null;
    }

    private static string? SafeProviderCode(string? providerCode)
    {
        if (string.IsNullOrWhiteSpace(providerCode))
        {
            return null;
        }

        var safe = new string(providerCode
            .Trim()
            .Take(MaxProviderCodeLength)
            .Select(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'
                ? character
                : '-')
            .ToArray());
        return safe;
    }

    private static string TypeName(Exception exception)
    {
        var type = exception.GetType();
        var name = type.FullName ?? type.Name;
        return name.Length <= MaxExceptionTypeLength ? name : name[..MaxExceptionTypeLength];
    }
}

/// <summary>
/// The status and code a provider SDK put on an exception it threw, as an
/// <see cref="INhAiModelProviderFailureReader"/> reads them.
/// </summary>
/// <param name="StatusCode">The HTTP status of the provider's answer; values outside 100 to 599 count as none.</param>
/// <param name="ProviderCode">
/// The provider's own error code, such as <c>invalid_api_key</c>; the executor keeps at most 64 letters,
/// digits, dashes, underscores and dots of it.
/// </param>
public sealed record NhAiModelProviderError(
    int? StatusCode,
    string? ProviderCode = null);

/// <summary>
/// Reads the content-free status and code from an exception type of one provider SDK, such as
/// <c>System.ClientModel.ClientResultException</c> of the OpenAI client, which NewHeap does not
/// reference. <see cref="INhAiChatExecutor"/> asks every reader about each exception of a failed
/// call's chain, outermost first, and keeps the first answer with a status or code; it reads
/// <see cref="HttpRequestException"/> itself. The status a reader returns also decides whether a
/// retry-eligible profile retries the call (429, 408 and 5xx). Register a reader with
/// <see cref="NhAiBuilder.AddModelProviderFailureReader{TReader}"/>.
/// </summary>
public interface INhAiModelProviderFailureReader
{
    /// <summary>
    /// Returns the status and code of an exception of a type this reader knows, or
    /// <see langword="null"/> for any other exception. Read only stable status and code values:
    /// never the exception message, a response body with content or headers that carry secrets.
    /// </summary>
    NhAiModelProviderError? Read(Exception exception);
}

/// <summary>
/// A failed result of the chat executor that keeps the provider failure and the exception behind it.
/// </summary>
internal interface INhAiModelProviderFailureResult
{
    NhAiModelProviderFailure ProviderFailure { get; }

    Exception ProviderException { get; }
}

/// <summary>
/// The failed <see cref="NhAiModelFailureCodes.DependencyUnavailable"/> result of a model call that
/// failed at the provider. Its code and message are those of every such result.
/// </summary>
internal sealed class NhAiModelProviderFailureResult<T> : TaskResult<T>, INhAiModelProviderFailureResult
{
    public NhAiModelProviderFailureResult(
        string code,
        string message,
        NhAiModelProviderFailure providerFailure,
        Exception providerException)
    {
        ArgumentNullException.ThrowIfNull(providerFailure);
        ArgumentNullException.ThrowIfNull(providerException);
        ProviderFailure = providerFailure;
        ProviderException = providerException;
        AddError(code, message);
    }

    public NhAiModelProviderFailure ProviderFailure { get; }

    public Exception ProviderException { get; }
}
