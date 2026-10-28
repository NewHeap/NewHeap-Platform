using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Net.Http.Headers;
using NewHeap.Platform.AI.Chat.Runtime;

namespace NewHeap.Platform.AI.Chat.AspNet;

/// <summary>
/// <c>POST {prefix}/conversations/{id}/files</c>: a person uploads one file to a conversation, as
/// <c>multipart/form-data</c> with one file part named <c>file</c>. The endpoint runs under the
/// assistant's access policy like every conversation endpoint and checks the conversation, then
/// <see cref="NhAssistantConversationUploads"/> checks the agent's policy and acceptance of files, the
/// accepted media types and the agent's file limits before the application's file source stores the
/// file, as <see cref="INhAssistantConversations.UploadAsync"/> does. It returns the file's
/// <see cref="NhAiFileReference"/>, which the message then carries in <c>files</c>.
/// </summary>
internal static class NhAssistantUploadEndpoints
{
    /// <summary>
    /// The request header an upload carries. A browser cannot send it with a cross-site form, so the
    /// endpoint keeps the posture of the JSON endpoints, which a cross-site form cannot call either.
    /// </summary>
    public const string UploadHeader = "X-NH-Assistant-Upload";

    /// <summary>
    /// The field of the file part.
    /// </summary>
    public const string FileField = "file";

    /// <summary>
    /// The bytes a multipart request may carry beyond the file itself: boundaries and part headers.
    /// </summary>
    internal const int MultipartOverheadBytes = 16 * 1024;

    public static void Map(RouteGroupBuilder group)
    {
        group.MapPost("conversations/{id:guid}/files", UploadAsync)
            .WithName("NhAssistantUploadFile")
            .WithSummary("Upload a file to a conversation")
            .WithDescription("Stores one file the caller attaches to a later message, through the application's file source, and returns its reference for the message's `files`. Send `multipart/form-data` with one file part named `file` and the header X-NH-Assistant-Upload: 1. The agent must accept files and its model profile, or a fallback, must permit the upload's classification in the execution region (409 assistant-files-not-accepted otherwise, as GET agents then offers no uploads), the media type must be one the application accepts and match the content, and the file must fit the agent's MaxFileBytes. A file part without a media type or with a generic one (application/octet-stream, or a type devices send for some extensions, such as application/vnd.ms-excel for a .csv file) takes the type its file name's extension names, when the application accepts that type; a specific type the application does not accept is refused whatever the extension. The application's upload scanner, when it registered one, scans the file before it is stored; a file it refuses answers 422 assistant-file-scan-refused. An upload gets the application's default classification, Confidential unless it configured another.")
            .Produces<NhAiFileReference>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status403Forbidden)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status409Conflict)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status413PayloadTooLarge)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status415UnsupportedMediaType)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status422UnprocessableEntity)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task<IResult> UploadAsync(
        Guid id,
        HttpContext httpContext,
        NhAssistantConversationUploads uploads)
    {
        var caller = await NhAssistantEndpointRouteBuilderExtensions.ResolveCallerAsync(httpContext);
        if (!caller.Success)
        {
            return NhAssistantEndpointRouteBuilderExtensions.ContextUnavailable();
        }

        // The conversation first; the uploads service then checks the agent's policy, which everyone
        // who uploads must satisfy, the role in an agent's thread and the agent's rules, as
        // INhAssistantConversations.UploadAsync does.
        var resolved = await NhAssistantEndpointRouteBuilderExtensions.ResolveAccessAsync(
            httpContext,
            id,
            caller.Data,
            NhAssistantAgentCheck.None);
        if (resolved.Error is not null)
        {
            return resolved.Error;
        }

        var prepared = await uploads.PrepareAsync(
            httpContext.User,
            resolved.Access!,
            NhAssistantEndpointRouteBuilderExtensions.ThreadAccessOf(httpContext),
            httpContext.RequestAborted);
        if (!prepared.Success)
        {
            return NhAssistantEndpointRouteBuilderExtensions.FromFailure(prepared);
        }

        var target = prepared.Data!;
        var request = httpContext.Request;
        if (string.IsNullOrWhiteSpace(request.Headers[UploadHeader]) || !IsMultipart(request))
        {
            return Error(NhAssistantErrorCodes.FileUploadInvalid);
        }

        var maxRequestBytes = (long)target.Rules.MaxFileBytes + MultipartOverheadBytes;
        if (request.ContentLength > maxRequestBytes)
        {
            return Error(NhAssistantErrorCodes.FileTooLarge);
        }

        var form = await ReadFormAsync(httpContext, maxRequestBytes);
        if (form.Code is { } formCode)
        {
            return Error(formCode);
        }

        var files = form.Form!.Files;
        var file = files.GetFile(FileField);
        if (files.Count != 1 || file is null)
        {
            return Error(NhAssistantErrorCodes.FileUploadInvalid);
        }

        var stored = await uploads.StoreAsync(
            target,
            new NhAssistantUploadInput(file.FileName, file.ContentType, file.Length, file.OpenReadStream),
            httpContext.RequestAborted);
        return stored.Success
            ? NhAssistantEndpointRouteBuilderExtensions.Json(stored.Data, NhAssistantJsonSerializerContext.Default.NhAiFileReference)
            : NhAssistantEndpointRouteBuilderExtensions.FromFailure(stored);
    }

    private static bool IsMultipart(HttpRequest request)
    {
        return MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType)
            && contentType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(HeaderUtilities.RemoveQuotes(contentType.Boundary).Value);
    }

    /// <summary>
    /// Reads the form within <paramref name="maxRequestBytes"/>: a larger body is
    /// <see cref="NhAssistantErrorCodes.FileTooLarge"/>, a malformed one
    /// <see cref="NhAssistantErrorCodes.FileUploadInvalid"/>. The form buffers a large file to a
    /// temporary file of the server, never to the log.
    /// </summary>
    private static async Task<(IFormCollection? Form, string? Code)> ReadFormAsync(HttpContext httpContext, long maxRequestBytes)
    {
        if (httpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodySize
            && (bodySize.MaxRequestBodySize is null || bodySize.MaxRequestBodySize > maxRequestBytes))
        {
            bodySize.MaxRequestBodySize = maxRequestBytes;
        }

        var options = new FormOptions
        {
            MultipartBodyLengthLimit = maxRequestBytes,
            ValueCountLimit = 16,
            ValueLengthLimit = 4 * 1024,
            KeyLengthLimit = 256,
            MultipartHeadersCountLimit = 16,
            MultipartHeadersLengthLimit = 16 * 1024
        };
        httpContext.Features.Set<IFormFeature>(new FormFeature(httpContext.Request, options));
        try
        {
            return (await httpContext.Request.ReadFormAsync(httpContext.RequestAborted), null);
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return (null, NhAssistantErrorCodes.FileTooLarge);
        }
        catch (InvalidDataException exception) when (exception.Message.Contains("Multipart body length limit", StringComparison.Ordinal))
        {
            return (null, NhAssistantErrorCodes.FileTooLarge);
        }
        catch (Exception exception) when (exception is InvalidDataException or BadHttpRequestException or IOException)
        {
            return (null, NhAssistantErrorCodes.FileUploadInvalid);
        }
    }

    private static IResult Error(string code)
    {
        return NhAssistantEndpointRouteBuilderExtensions.Error(code);
    }
}
