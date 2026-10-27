using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.Tests.Infrastructure;

/// <summary>
/// One file of the test application's storage.
/// </summary>
public sealed record TestFile(
    string Id,
    string Name,
    string MediaType,
    byte[] Content,
    NhAiDataClassification Classification,
    int? PageCount)
{
    /// <summary>
    /// The actors that may not read the file.
    /// </summary>
    public ConcurrentDictionary<string, bool> ForbiddenActors { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The storage accepts a request for the file and never answers: only the turn's cancellation
    /// ends it.
    /// </summary>
    public bool Silent { get; set; }

    public NhAiFileReference Reference => new(Id, Name, MediaType, Content.Length, "app-hash-" + Id);
}

/// <summary>
/// The test application's files and every time the assistant opened one, with the actor it asked for.
/// </summary>
public sealed class TestFiles
{
    public ConcurrentDictionary<string, TestFile> Files { get; } = new(StringComparer.Ordinal);

    public ConcurrentQueue<(NhAiFileReference Reference, NhAssistantFileActor Actor)> Opened { get; } = new();

    /// <summary>
    /// Set when a request for a silent file arrived.
    /// </summary>
    public TaskCompletionSource SilentOpen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TestFile Add(
        string id,
        string name,
        string mediaType,
        string content,
        NhAiDataClassification classification = NhAiDataClassification.Internal,
        int? pageCount = 1)
    {
        var file = new TestFile(id, name, mediaType, System.Text.Encoding.UTF8.GetBytes(content), classification, pageCount);
        Files[id] = file;
        return file;
    }

    /// <summary>
    /// The reference the application gives a tool for <paramref name="id"/>; a reference to a file it
    /// does not keep for an unknown id.
    /// </summary>
    public NhAiFileReference ReferenceOf(string id)
    {
        return Files.TryGetValue(id, out var file)
            ? file.Reference
            : new NhAiFileReference(id, id + ".pdf", NhAiMediaTypes.Pdf, 1);
    }
}

/// <summary>
/// The application's file source over <see cref="TestFiles"/>: it opens a file for the actor of the
/// turn unless that actor may not read it, and takes no uploads.
/// </summary>
public sealed class TestFileSource(TestFiles files) : INhAssistantFileSource
{
    public async Task<TaskResult<NhAssistantFile>> OpenAsync(
        NhAiFileReference reference,
        NhAssistantFileActor actor,
        CancellationToken cancellationToken)
    {
        files.Opened.Enqueue((reference, actor));
        if (!files.Files.TryGetValue(reference.Id, out var file))
        {
            return TaskResult<NhAssistantFile>.Failed(NhAssistantErrorCodes.FileNotFound, "The file does not exist.");
        }
        if (file.ForbiddenActors.ContainsKey(actor.ActorId))
        {
            return TaskResult<NhAssistantFile>.Failed(NhAssistantErrorCodes.FileForbidden, "The actor may not read the file.");
        }
        if (file.Silent)
        {
            files.SilentOpen.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        return TaskResult<NhAssistantFile>.Succeeded(
            new NhAssistantFile(new MemoryStream(file.Content), file.MediaType, file.Content.Length, file.Classification)
            {
                PageCount = file.PageCount
            });
    }

    public Task<TaskResult<NhAiFileReference>> StoreUploadAsync(
        NhAssistantFileUpload upload,
        NhAssistantFileActor actor,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(TaskResult<NhAiFileReference>.Failed(NhAssistantErrorCodes.FilesUnavailable, "The test application takes no uploads."));
    }
}

public sealed record DocumentOpenInput(IReadOnlyList<string> Ids);

/// <summary>
/// What the document tool returns: how many documents it found, and their references as the result's
/// files.
/// </summary>
public sealed record DocumentOpenResult(int Count, [property: JsonIgnore] IReadOnlyList<NhAiFileReference> Files) : INhAiToolResultFiles;

/// <summary>
/// A read-only tool that returns the references of the application's documents, never their content.
/// </summary>
public sealed class TestDocumentToolCatalog : INhAiToolCatalog
{
    public static readonly NhAiToolDescriptor Open = new(
        "documents.open",
        1,
        "Open project documents by id.",
        typeof(DocumentOpenInput),
        typeof(DocumentOpenResult),
        NhAiToolEffect.ReadOnly,
        NhAiToolExposure.Local | NhAiToolExposure.Agent,
        true,
        [TestProjectToolCatalog.ReadPolicy])
    {
        ExportName = "documents_open_v1",
        CatalogId = "documents",
        ContractHash = new string('4', 64),
        SchemaHash = new string('4', 64),
        AllowAgentGrant = true
    };

    public NhAiToolCatalogGovernance Governance => NhAiToolCatalogGovernance.SharedInvoker;

    public IReadOnlyList<NhAiToolDescriptor> Descriptors { get; } = [Open];

    public NhAiToolCatalogManifest Manifest { get; } = new(
        "documents",
        1,
        new string('5', 64),
        [new NhAiToolManifestEntry(Open.Id, 1, Open.SchemaHash, Open.ContractHash) { ExportName = Open.ExportName }]);

    public IReadOnlyList<AIFunction> CreateFunctions(IServiceProvider services)
    {
        var invoker = services.GetRequiredService<INhAiToolInvoker>();
        var files = services.GetRequiredService<TestFiles>();
        Func<DocumentOpenInput, CancellationToken, Task<TaskResult<DocumentOpenResult>>> open =
            (input, cancellationToken) => invoker.InvokeAsync(
                Open,
                input,
                (_, _) =>
                {
                    IReadOnlyList<NhAiFileReference> references = input.Ids.Select(files.ReferenceOf).ToArray();
                    return Task.FromResult(TaskResult<DocumentOpenResult>.Succeeded(new DocumentOpenResult(references.Count, references)));
                },
                cancellationToken);

        return
        [
            NhAiGovernedAIFunction.Create(
                Open,
                AIFunctionFactory.Create(open, new AIFunctionFactoryOptions
                {
                    Name = Open.ExportName,
                    Description = Open.Description,
                    MarshalResult = NhAiToolResult.CreateMarshaller<DocumentOpenResult>(),
                    ExcludeResultSchema = true
                }),
                services)
        ];
    }
}
