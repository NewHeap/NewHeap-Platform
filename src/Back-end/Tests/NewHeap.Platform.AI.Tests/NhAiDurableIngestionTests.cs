using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NewHeap.Platform.Common.Models;
using NSubstitute;
using Xunit;

namespace NewHeap.Platform.AI.Tests;

public sealed class NhAiDurableIngestionTests
{
    [Fact]
    public async Task Durable_ingestion_replays_a_content_free_checkpoint_without_reingesting()
    {
        var operation = Substitute.For<INhBackgroundOperationContext>();
        var checkpoints = Substitute.For<INhBackgroundOperationCheckpointStore>();
        operation.OperationId.Returns(Guid.NewGuid());
        operation.AttemptId.Returns(Guid.NewGuid());
        operation.AttemptNumber.Returns(2);
        operation.FencingToken.Returns(17);
        operation.IdempotencyKey.Returns("nh-operation-stable");
        operation.Checkpoints.Returns(checkpoints);
        operation
            .ThrowIfCancellationRequestedAsync(Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        NhAiDurableIngestionCheckpoint? saved = null;
        checkpoints
            .GetAsync<NhAiDurableIngestionCheckpoint>(
                "ai-ingestion",
                Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(
                saved is null
                    ? null
                    : new NhBackgroundOperationCheckpointValue<NhAiDurableIngestionCheckpoint>(
                        saved,
                        1,
                        1)));
        checkpoints
            .SetAsync(
                "ai-ingestion",
                Arg.Any<NhAiDurableIngestionCheckpoint>(),
                1,
                Arg.Any<long?>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                saved = call.ArgAt<NhAiDurableIngestionCheckpoint>(1);
                return Task.FromResult(TaskResult.Succeeded());
            });

        var pipeline = Substitute.For<INhAiIngestionPipeline>();
        pipeline
            .IngestAsync(
                Arg.Any<NhAiIngestionRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(TaskResult<NhAiIngestionResult>.Succeeded(new(
                "project-documents",
                "project-doc-1",
                3,
                4,
                NhAiCanonicalJson.ComputeHash("document"),
                NhAiCanonicalJson.ComputeHash("batch"),
                "project-context",
                2,
                5)));
        var services = new ServiceCollection();
        services.AddSingleton(pipeline);
        services.AddSingleton<INhAiIngestionAuthorizationPolicy>(
            new NhAiTestIngestionAuthorizationPolicy(true));
        services.AddNewHeapPlatformAIAspNet(_ => { });
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var adapter = scope.ServiceProvider
            .GetRequiredService<INhAiBackgroundOperationIngestionAdapter>();
        var request = new NhAiIngestionRequest(
            new NhAiInvocationContext(
                "ingestion-agent",
                "project-ingestion",
                new Dictionary<string, string>())
            {
                ActorKind = NhAiActorKind.Agent,
                AccountableOwnerId = "platform-owner",
                ExecutionScopes = [new NhAiExecutionScopeEntry("division", Guid.NewGuid().ToString())]
            },
            "project-documents",
            "project-doc-1",
            "project-embeddings",
            "project-vector",
            "project-context",
            3,
            "caller-value-is-replaced");

        var first = await adapter.IngestAsync(operation, request);
        var replay = await adapter.IngestAsync(operation, request);

        Assert.True(first.Success);
        Assert.True(replay.Success);
        Assert.Equal(first.Data.BatchHash, replay.Data.BatchHash);
        Assert.NotNull(saved);
        Assert.Equal(64, saved.RequestHash.Length);
        await pipeline.Received(1).IngestAsync(
            Arg.Is<NhAiIngestionRequest>(value =>
                value.IdempotencyKey == "nh-operation-stable"
                && value.InvocationContext.RunId == operation.OperationId.ToString("N")
                && value.InvocationContext.FencingToken == "17"),
            Arg.Any<CancellationToken>());
    }
}
