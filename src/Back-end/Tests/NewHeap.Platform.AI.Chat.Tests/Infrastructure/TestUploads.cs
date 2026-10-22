using System.Collections.Concurrent;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.Tests.Infrastructure;

/// <summary>
/// One upload the test application stored: what the assistant handed its file source, the content
/// and the actor it stored the file for.
/// </summary>
public sealed record TestUpload(
    NhAiFileReference Reference,
    NhAssistantFileUpload Upload,
    byte[] Content,
    NhAssistantFileActor Actor);

/// <summary>
/// The test application's storage of uploads, and how its file source answers the next upload.
/// </summary>
public sealed class TestUploads
{
    private int _next;

    public ConcurrentDictionary<string, TestUpload> Stored { get; } = new(StringComparer.Ordinal);

    public ConcurrentQueue<(NhAiFileReference Reference, NhAssistantFileActor Actor)> Opened { get; } = new();

    /// <summary>
    /// The code the file source refuses the next uploads with, or <see langword="null"/> to store them.
    /// </summary>
    public string? RefuseWith { get; set; }

    /// <summary>
    /// Whether the file source throws while it stores an upload, with the file's name in the message.
    /// </summary>
    public bool Fail { get; set; }

    internal string NextId()
    {
        return "upload-" + Interlocked.Increment(ref _next);
    }
}

/// <summary>
/// A file source that stores uploads in <see cref="TestUploads"/> with the classification the
/// assistant gives them, and opens them again for any actor.
/// </summary>
public sealed class TestUploadFileSource(TestUploads uploads) : INhAssistantFileSource
{
    public Task<TaskResult<NhAssistantFile>> OpenAsync(
        NhAiFileReference reference,
        NhAssistantFileActor actor,
        CancellationToken cancellationToken)
    {
        uploads.Opened.Enqueue((reference, actor));
        if (!uploads.Stored.TryGetValue(reference.Id, out var stored))
        {
            return Task.FromResult(TaskResult<NhAssistantFile>.Failed(NhAssistantErrorCodes.FileNotFound, "The file does not exist."));
        }

        return Task.FromResult(TaskResult<NhAssistantFile>.Succeeded(new NhAssistantFile(
            new MemoryStream(stored.Content),
            stored.Upload.MediaType,
            stored.Content.Length,
            stored.Upload.Classification)
        {
            PageCount = 1
        }));
    }

    public async Task<TaskResult<NhAiFileReference>> StoreUploadAsync(
        NhAssistantFileUpload upload,
        NhAssistantFileActor actor,
        CancellationToken cancellationToken)
    {
        if (uploads.Fail)
        {
            throw new IOException($"The disk is full while storing {upload.Name}.");
        }
        if (uploads.RefuseWith is { } code)
        {
            return TaskResult<NhAiFileReference>.Failed(code, $"The scan refused {upload.Name}.");
        }

        using var buffer = new MemoryStream();
        await upload.Content.CopyToAsync(buffer, cancellationToken);
        var reference = new NhAiFileReference(uploads.NextId(), upload.Name, upload.MediaType, upload.Length, "app-hash");
        uploads.Stored[reference.Id] = new TestUpload(reference, upload with { Content = Stream.Null }, buffer.ToArray(), actor);
        return TaskResult<NhAiFileReference>.Succeeded(reference);
    }
}
