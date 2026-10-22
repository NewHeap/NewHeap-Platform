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

    /// <summary>
    /// What the upload scanner does with the next uploads: <see cref="TestScanOutcome.Pass"/> by default.
    /// </summary>
    public TestScanOutcome ScanOutcome { get; set; }

    /// <summary>
    /// The uploads the scanner saw: their name, media type, content and the actor of the upload.
    /// </summary>
    public ConcurrentQueue<(string Name, string MediaType, byte[] Content, string ActorId)> Scanned { get; } = new();

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

/// <summary>
/// What <see cref="TestUploadScanner"/> does with an upload.
/// </summary>
public enum TestScanOutcome
{
    Pass,
    Refuse,
    Fail,
    Throw
}

/// <summary>
/// The test application's upload scanner: it records every upload it sees and passes, refuses, fails
/// or throws as <see cref="TestUploads.ScanOutcome"/> says, with the file's name in its messages.
/// </summary>
public sealed class TestUploadScanner(TestUploads uploads) : INhAssistantUploadScanner
{
    public async Task<TaskResult> ScanAsync(
        NhAssistantFileUpload upload,
        NhAssistantFileActor actor,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await upload.Content.CopyToAsync(buffer, cancellationToken);
        uploads.Scanned.Enqueue((upload.Name, upload.MediaType, buffer.ToArray(), actor.ActorId));
        return uploads.ScanOutcome switch
        {
            TestScanOutcome.Refuse => TaskResult.Failed("eicar-found", $"The EICAR test file was found in {upload.Name}."),
            TestScanOutcome.Fail => TaskResult.Failed(NhAssistantErrorCodes.FileUploadFailed, $"The scanner was down while scanning {upload.Name}."),
            TestScanOutcome.Throw => throw new IOException($"The scanner crashed on {upload.Name}."),
            _ => TaskResult.Succeeded()
        };
    }
}
