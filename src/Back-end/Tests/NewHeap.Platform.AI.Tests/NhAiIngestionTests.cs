using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.VectorData;
using NewHeap.Platform.AI.Test;
using NSubstitute;
using Xunit;

namespace NewHeap.Platform.AI.Tests;

public sealed class NhAiIngestionTests
{
    [Fact]
    public async Task Ingestion_authorizes_before_reading_and_preserves_scope_and_provenance()
    {
        var divisionId = Guid.NewGuid();
        const string content = "first bounded project paragraph\nsecond bounded project paragraph";
        var document = new NhAiIngestionDocument(
            "project-doc-1",
            2,
            "project-documents",
            "text/plain",
            content,
            NhAiCanonicalJson.ComputeHash(content),
            NhAiDataClassification.Internal,
            NhAiContextTrust.UntrustedRetrieved,
            [new NhAiExecutionScopeEntry("division", divisionId.ToString())],
            ["project:project-1", "field:description"],
            DateTimeOffset.UtcNow);
        var source = new NhAiTestIngestionSource("project-documents", [document]);
        var vectorStore = Substitute.For<VectorStore>();
        var collection = Substitute.For<VectorStoreCollection<string, NhAiVectorRecord>>();
        vectorStore
            .GetCollection<string, NhAiVectorRecord>(
                "project-context",
                Arg.Any<VectorStoreCollectionDefinition>())
            .Returns(collection);
        collection
            .EnsureCollectionExistsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        collection
            .UpsertAsync(
                Arg.Any<IEnumerable<NhAiVectorRecord>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var embeddings = new NhAiDeterministicEmbeddingGenerator();
        var services = new ServiceCollection();
        services.AddSingleton<INhAiIngestionSource>(source);
        services.AddSingleton<INhAiIngestionAuthorizationPolicy>(
            new NhAiTestIngestionAuthorizationPolicy(true, true));
        services.AddSingleton<INhAiIngestionVersionManager, NhAiTestIngestionVersionManager>();
        services.AddSingleton<INhAiBudgetManager, NhAiTestBudgetManager>();
        services.AddKeyedSingleton<VectorStore>("project-vector", vectorStore);
        services.AddKeyedSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
            "project-embeddings-model",
            embeddings);
        services.AddNewHeapPlatformAI(ai => ai.AddEmbeddingProfile(
            "project-embeddings",
            profile => profile
                .UseKeyedClient("project-embeddings-model")
                .RequireCapabilities(NhAiModelCapability.Embeddings)
                .PermitDataClassifications(NhAiDataClassification.Internal)));
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider
            .GetRequiredService<INhAiIngestionPipeline>()
            .IngestAsync(new NhAiIngestionRequest(
                CreateContext(divisionId),
                "project-documents",
                document.Id,
                "project-embeddings",
                "project-vector",
                "project-context",
                3,
                "ingestion-1")
            {
                MaxChunkCharacters = 32,
                Replaces = new NhAiIngestionReplacement(
                    1,
                    3,
                    NhAiCanonicalJson.ComputeHash("prior content"))
            });

        Assert.True(result.Success);
        Assert.Equal(1, source.Calls);
        Assert.Equal(result.Data.ChunkCount, embeddings.Inputs.Count);
        Assert.Equal("project-context", result.Data.VectorCollectionName);
        Assert.Equal(1, result.Data.ReplacedDocumentVersion);
        Assert.Equal(3, result.Data.DeletedRecordCount);
        await collection.Received(1).UpsertAsync(
            Arg.Is<IEnumerable<NhAiVectorRecord>>(records => records.All(record =>
                record.IdempotencyKey == "ingestion-1"
                && record.ExecutionScopeKeys.Contains($"division:{divisionId}")
                && record.ProvenanceReferences.Contains("project:project-1"))),
            Arg.Any<CancellationToken>());
        await collection.Received(1).DeleteAsync(
            Arg.Is<IEnumerable<string>>(ids => ids.Count() == 3),
            Arg.Any<CancellationToken>());

        var deletion = await scope.ServiceProvider
            .GetRequiredService<INhAiIngestionPipeline>()
            .DeleteAsync(new NhAiIngestionDeletionRequest(
                CreateContext(divisionId),
                "project-documents",
                document.Id,
                document.Version,
                result.Data.ChunkCount,
                document.ContentHash,
                "project-vector",
                "project-context",
                3,
                "delete-ingestion-1"));

        Assert.True(deletion.Success);
        Assert.Equal(result.Data.ChunkCount, deletion.Data.DeletedRecordCount);
        await collection.Received(1).DeleteAsync(
            Arg.Is<IEnumerable<string>>(ids => ids.Count() == result.Data.ChunkCount),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Default_ingestion_authorization_denies_before_source_access()
    {
        var source = new NhAiTestIngestionSource("project-documents", []);
        var services = new ServiceCollection();
        services.AddSingleton<INhAiIngestionSource>(source);
        services.AddNewHeapPlatformAI();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider
            .GetRequiredService<INhAiIngestionPipeline>()
            .IngestAsync(new NhAiIngestionRequest(
                CreateContext(Guid.NewGuid()),
                "project-documents",
                "project-doc-1",
                "project-embeddings",
                "project-vector",
                "project-context",
                3,
                "ingestion-1"));

        Assert.False(result.Success);
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task Replacement_requires_delete_authorization_before_source_access()
    {
        var source = new NhAiTestIngestionSource("project-documents", []);
        var services = new ServiceCollection();
        services.AddSingleton<INhAiIngestionSource>(source);
        services.AddSingleton<INhAiIngestionAuthorizationPolicy>(
            new NhAiTestIngestionAuthorizationPolicy(allowed: true, deleteAllowed: false));
        services.AddNewHeapPlatformAI();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider
            .GetRequiredService<INhAiIngestionPipeline>()
            .IngestAsync(new NhAiIngestionRequest(
                CreateContext(Guid.NewGuid()),
                "project-documents",
                "project-doc-1",
                "project-embeddings",
                "project-vector",
                "project-context",
                2,
                "ingestion-replacement-denied")
            {
                Replaces = new NhAiIngestionReplacement(
                    1,
                    1,
                    NhAiCanonicalJson.ComputeHash("prior content"))
            });

        Assert.False(result.Success);
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task Conflicting_version_or_idempotency_key_never_writes_vectors_twice()
    {
        var divisionId = Guid.NewGuid();
        const string content = "versioned project context";
        var document = new NhAiIngestionDocument(
            "project-doc-1",
            1,
            "project-documents",
            "text/plain",
            content,
            NhAiCanonicalJson.ComputeHash(content),
            NhAiDataClassification.Internal,
            NhAiContextTrust.UntrustedRetrieved,
            [new NhAiExecutionScopeEntry("division", divisionId.ToString())],
            ["project:project-1"],
            DateTimeOffset.UtcNow);
        var source = new NhAiTestIngestionSource("project-documents", [document]);
        var vectorStore = Substitute.For<VectorStore>();
        var collection = Substitute.For<VectorStoreCollection<string, NhAiVectorRecord>>();
        vectorStore
            .GetCollection<string, NhAiVectorRecord>(
                "project-context",
                Arg.Any<VectorStoreCollectionDefinition>())
            .Returns(collection);
        collection
            .EnsureCollectionExistsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        collection
            .UpsertAsync(
                Arg.Any<IEnumerable<NhAiVectorRecord>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var services = new ServiceCollection();
        services.AddSingleton<INhAiIngestionSource>(source);
        services.AddSingleton<INhAiIngestionAuthorizationPolicy>(
            new NhAiTestIngestionAuthorizationPolicy(true));
        services.AddSingleton<INhAiIngestionVersionManager, NhAiTestIngestionVersionManager>();
        services.AddSingleton<INhAiBudgetManager, NhAiTestBudgetManager>();
        services.AddKeyedSingleton<VectorStore>("project-vector", vectorStore);
        services.AddKeyedSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
            "project-embeddings-model",
            new NhAiDeterministicEmbeddingGenerator());
        services.AddNewHeapPlatformAI(ai => ai.AddEmbeddingProfile(
            "project-embeddings",
            profile => profile
                .UseKeyedClient("project-embeddings-model")
                .RequireCapabilities(NhAiModelCapability.Embeddings)
                .PermitDataClassifications(NhAiDataClassification.Internal)));
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var pipeline = scope.ServiceProvider.GetRequiredService<INhAiIngestionPipeline>();
        var context = CreateContext(divisionId);

        var first = await pipeline.IngestAsync(new NhAiIngestionRequest(
            context,
            "project-documents",
            document.Id,
            "project-embeddings",
            "project-vector",
            "project-context",
            3,
            "ingestion-version-1-a"));
        var conflicting = await pipeline.IngestAsync(new NhAiIngestionRequest(
            context,
            "project-documents",
            document.Id,
            "project-embeddings",
            "project-vector",
            "project-context",
            3,
            "ingestion-version-1-b"));

        Assert.True(first.Success);
        Assert.False(conflicting.Success);
        var budgetManager = Assert.IsType<NhAiTestBudgetManager>(
            scope.ServiceProvider.GetRequiredService<INhAiBudgetManager>());
        Assert.Single(budgetManager.Requests);
        await collection.Received(1).UpsertAsync(
            Arg.Any<IEnumerable<NhAiVectorRecord>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Embedding_budget_denial_prevents_model_and_vector_access_and_releases_version_lease()
    {
        var divisionId = Guid.NewGuid();
        const string content = "budgeted project context";
        var document = new NhAiIngestionDocument(
            "project-doc-budget",
            1,
            "project-documents",
            "text/plain",
            content,
            NhAiCanonicalJson.ComputeHash(content),
            NhAiDataClassification.Internal,
            NhAiContextTrust.UntrustedRetrieved,
            [new NhAiExecutionScopeEntry("division", divisionId.ToString())],
            ["project:project-budget"],
            DateTimeOffset.UtcNow);
        var source = new NhAiTestIngestionSource("project-documents", [document]);
        var embeddings = new NhAiDeterministicEmbeddingGenerator();
        var versionManager = new NhAiTestIngestionVersionManager();
        var budgetManager = new NhAiTestBudgetManager(allow: false);
        var services = new ServiceCollection();
        services.AddSingleton<INhAiIngestionSource>(source);
        services.AddSingleton<INhAiIngestionAuthorizationPolicy>(
            new NhAiTestIngestionAuthorizationPolicy(true));
        services.AddSingleton<INhAiIngestionVersionManager>(versionManager);
        services.AddSingleton<INhAiBudgetManager>(budgetManager);
        services.AddKeyedSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
            "project-embeddings-model",
            embeddings);
        services.AddNewHeapPlatformAI(ai => ai.AddEmbeddingProfile(
            "project-embeddings",
            profile => profile
                .UseKeyedClient("project-embeddings-model")
                .RequireCapabilities(NhAiModelCapability.Embeddings)
                .PermitDataClassifications(NhAiDataClassification.Internal)));
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider
            .GetRequiredService<INhAiIngestionPipeline>()
            .IngestAsync(new NhAiIngestionRequest(
                CreateContext(divisionId),
                "project-documents",
                document.Id,
                "project-embeddings",
                "missing-vector-store",
                "project-context",
                3,
                "ingestion-budget-denied"));

        Assert.False(result.Success);
        Assert.Single(budgetManager.Requests);
        Assert.Empty(embeddings.Inputs);
        var reacquired = await versionManager.AcquireAsync(
            new NhAiIngestionVersionRequest(
                document.SourceId,
                document.Id,
                document.Version,
                document.ContentHash,
                "ingestion-budget-denied"));
        Assert.Equal(NhAiIngestionVersionDecisionKind.Acquired, reacquired.Decision);
        await versionManager.CompleteAsync(reacquired, NhAiOutcomeKind.TerminalFailure);
    }

    [Fact]
    public async Task Batch_ingestion_preserves_successful_documents_when_one_document_fails()
    {
        var divisionId = Guid.NewGuid();
        const string content = "bounded project context";
        var document = new NhAiIngestionDocument(
            "project-doc-1",
            1,
            "project-documents",
            "text/plain",
            content,
            NhAiCanonicalJson.ComputeHash(content),
            NhAiDataClassification.Internal,
            NhAiContextTrust.UntrustedRetrieved,
            [new NhAiExecutionScopeEntry("division", divisionId.ToString())],
            ["project:project-1"],
            DateTimeOffset.UtcNow);
        var source = new NhAiTestIngestionSource("project-documents", [document]);
        var vectorStore = Substitute.For<VectorStore>();
        var collection = Substitute.For<VectorStoreCollection<string, NhAiVectorRecord>>();
        vectorStore
            .GetCollection<string, NhAiVectorRecord>(
                "project-context",
                Arg.Any<VectorStoreCollectionDefinition>())
            .Returns(collection);
        collection
            .EnsureCollectionExistsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        collection
            .UpsertAsync(
                Arg.Any<IEnumerable<NhAiVectorRecord>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var services = new ServiceCollection();
        services.AddSingleton<INhAiIngestionSource>(source);
        services.AddSingleton<INhAiIngestionAuthorizationPolicy>(
            new NhAiTestIngestionAuthorizationPolicy(true));
        services.AddSingleton<INhAiIngestionVersionManager, NhAiTestIngestionVersionManager>();
        services.AddSingleton<INhAiBudgetManager, NhAiTestBudgetManager>();
        services.AddKeyedSingleton<VectorStore>("project-vector", vectorStore);
        services.AddKeyedSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
            "project-embeddings-model",
            new NhAiDeterministicEmbeddingGenerator());
        services.AddNewHeapPlatformAI(ai => ai.AddEmbeddingProfile(
            "project-embeddings",
            profile => profile
                .UseKeyedClient("project-embeddings-model")
                .RequireCapabilities(NhAiModelCapability.Embeddings)
                .PermitDataClassifications(NhAiDataClassification.Internal)));
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var context = CreateContext(divisionId);

        var result = await scope.ServiceProvider
            .GetRequiredService<INhAiIngestionPipeline>()
            .IngestBatchAsync(new NhAiIngestionBatchRequest(
            [
                new NhAiIngestionRequest(
                    context,
                    "project-documents",
                    document.Id,
                    "project-embeddings",
                    "project-vector",
                    "project-context",
                    3,
                    "batch-1-document-1"),
                new NhAiIngestionRequest(
                    context,
                    "project-documents",
                    "project-doc-missing",
                    "project-embeddings",
                    "project-vector",
                    "project-context",
                    3,
                    "batch-1-document-2")
            ]));

        Assert.False(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(1, result.Data.Succeeded);
        Assert.Equal(1, result.Data.Failed);
        Assert.True(result.Data.Documents[0].Result.Success);
        Assert.False(result.Data.Documents[1].Result.Success);
        await collection.Received(1).UpsertAsync(
            Arg.Any<IEnumerable<NhAiVectorRecord>>(),
            Arg.Any<CancellationToken>());
    }

    private static NhAiInvocationContext CreateContext(Guid divisionId)
    {
        return new NhAiInvocationContext(
            "ingestion-agent",
            "project-ingestion",
            new Dictionary<string, string>())
        {
            ActorKind = NhAiActorKind.Agent,
            AccountableOwnerId = "platform-owner",
            ExecutionScopes =
            [
                new NhAiExecutionScopeEntry("division", divisionId.ToString())
            ]
        };
    }
}
