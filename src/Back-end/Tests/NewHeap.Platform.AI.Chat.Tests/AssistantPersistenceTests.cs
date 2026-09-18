using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

[Collection(AssistantDatabaseCollection.Name)]
public sealed class AssistantPersistenceTests(AssistantDatabaseFixture database)
{
    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Migrations_apply_in_the_default_schema_and_leave_no_pending_model_changes(
        AssistantTestProvider provider)
    {
        await using var services = await database.CreateMigratedStorageAsync(
            provider,
            NhAssistantDbContextOptions.DefaultSchema);
        await using var context = services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();

        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.False(context.Database.HasPendingModelChanges());
        Assert.Equal("nhai", context.Model.GetDefaultSchema());
        await AssertAllTablesQueryableAsync(context);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Migrations_route_the_checked_in_schema_to_a_configured_schema(
        AssistantTestProvider provider)
    {
        var schema = AssistantDatabaseFixture.UniqueSchema();
        await using var services = await database.CreateMigratedStorageAsync(provider, schema);
        await using var context = services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        var script = context.GetService<IMigrator>().GenerateScript();
        var quotedSchema = context.GetService<ISqlGenerationHelper>().DelimitIdentifier(schema);

        Assert.Equal(schema, context.Model.GetDefaultSchema());
        Assert.Contains($"{quotedSchema}.", script);
        Assert.DoesNotContain("[nhai]", script);
        Assert.DoesNotContain("\"nhai\"", script);
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        await AssertAllTablesQueryableAsync(context);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Conversation_messages_invocations_and_approvals_round_trip(
        AssistantTestProvider provider)
    {
        await using var services = await database.CreateMigratedStorageAsync(provider);
        var store = services.GetRequiredService<INhAssistantStore>();
        var conversation = CreateConversation("owner-1");
        await store.AddConversationAsync(conversation, CancellationToken.None);
        var turnId = Guid.NewGuid();
        var userMessage = new AssistantMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            TurnId = turnId,
            Role = NhAssistantMessageRoles.User,
            PartsJson = """[{"type":"text","text":"Find roadmap projects"}]""",
            ClientMessageId = "client-1",
            CreatedAt = DateTimeOffset.UtcNow
        };
        var assistantMessage = new AssistantMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            TurnId = turnId,
            Role = NhAssistantMessageRoles.Assistant,
            CreatedAt = DateTimeOffset.UtcNow
        };
        await store.AddMessageAsync(userMessage, CancellationToken.None);
        await store.AddMessageAsync(assistantMessage, CancellationToken.None);
        await store.UpdateMessageAsync(
            assistantMessage.Id,
            """[{"type":"text","text":"Two projects."}]""",
            12,
            7,
            1,
            CancellationToken.None);
        var invocation = new AssistantToolInvocation
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            TurnId = turnId,
            MessageId = assistantMessage.Id,
            CallId = "call-1",
            FunctionName = "projects_search_v1",
            ToolId = "projects.search",
            ToolVersion = 1,
            ContractHash = new string('a', 64),
            DisplayName = "projects.search",
            ArgumentsJson = new string('x', NhAssistantLimits.MaxStoredJsonCharacters),
            DataClassification = NhAiDataClassification.Confidential,
            RetentionCategory = NhAiRetentionCategory.ConversationContent,
            StartedAt = DateTimeOffset.UtcNow
        };
        await store.AddToolInvocationAsync(invocation, CancellationToken.None);
        invocation.Status = NhAssistantToolCallStatuses.Succeeded;
        invocation.CompletedAt = DateTimeOffset.UtcNow;
        await store.UpdateToolInvocationAsync(invocation, CancellationToken.None);
        var approval = new AssistantApproval
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            TurnId = turnId,
            ToolInvocationId = invocation.Id,
            ProposalId = Guid.NewGuid(),
            ProposalHash = new string('b', 64),
            ProposalJson = "{}",
            ToolId = "projects.change-status",
            Summary = "Change one project status.",
            ArgumentsPreview = "{}",
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            ConcurrencyStamp = Guid.NewGuid()
        };
        await store.AddApprovalAsync(approval, CancellationToken.None);

        var messages = await store.GetMessagesAsync(conversation.Id, 10, CancellationToken.None);
        var storedInvocation = await store.FindToolInvocationAsync(invocation.Id, CancellationToken.None);
        var pending = await store.FindPendingApprovalAsync(conversation.Id, CancellationToken.None);
        var decided = await store.TryDecideApprovalAsync(
            approval.Id,
            NhAssistantApprovalStatuses.Approved,
            "owner-1",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(1),
            null,
            CancellationToken.None);
        var decidedTwice = await store.TryDecideApprovalAsync(
            approval.Id,
            NhAssistantApprovalStatuses.Rejected,
            "owner-1",
            DateTimeOffset.UtcNow,
            null,
            null,
            CancellationToken.None);

        Assert.Equal([1, 2], messages.Select(message => message.Sequence));
        Assert.Equal(12, messages[1].InputTokens);
        Assert.Contains("Two projects.", messages[1].PartsJson);
        Assert.True(await store.ClientMessageExistsAsync(conversation.Id, "client-1", CancellationToken.None));
        Assert.NotNull(storedInvocation);
        Assert.Equal(NhAssistantToolCallStatuses.Succeeded, storedInvocation.Status);
        Assert.Equal(NhAiDataClassification.Confidential, storedInvocation.DataClassification);
        Assert.Equal(NhAssistantLimits.MaxStoredJsonCharacters, storedInvocation.ArgumentsJson!.Length);
        Assert.Equal(approval.Id, pending?.Id);
        Assert.True(decided);
        Assert.False(decidedTwice);
        Assert.Equal(
            NhAssistantApprovalStatuses.Approved,
            (await store.FindApprovalAsync(approval.Id, CancellationToken.None))!.Status);
        Assert.Null(await store.FindConversationAsync(conversation.Id, "other-owner", CancellationToken.None));
        var (items, total) = await store.ListConversationsAsync("owner-1", 1, 20, CancellationToken.None);
        Assert.Equal(1, total);
        Assert.Equal(conversation.Id, Assert.Single(items).Id);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Only_one_concurrent_status_transition_can_start_a_turn(
        AssistantTestProvider provider)
    {
        await using var services = await database.CreateMigratedStorageAsync(provider);
        var store = services.GetRequiredService<INhAssistantStore>();
        var conversation = CreateConversation("owner-1");
        await store.AddConversationAsync(conversation, CancellationToken.None);
        var staleBefore = DateTimeOffset.UtcNow.AddHours(-1);

        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => store.TryBeginTurnAsync(
            conversation.Id,
            "owner-1",
            [NhAssistantConversationStatuses.Idle],
            Guid.NewGuid(),
            staleBefore,
            CancellationToken.None)));

        Assert.Single(attempts, started => started);
        var running = await store.FindConversationAsync(conversation.Id, "owner-1", CancellationToken.None);
        Assert.Equal(NhAssistantConversationStatuses.Running, running!.Status);
        Assert.False(await store.TryEndTurnAsync(
            conversation.Id,
            Guid.NewGuid(),
            NhAssistantConversationStatuses.Idle,
            CancellationToken.None));
        Assert.True(await store.TryEndTurnAsync(
            conversation.Id,
            running.ActiveTurnId!.Value,
            NhAssistantConversationStatuses.Idle,
            CancellationToken.None));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_stale_tracked_status_change_is_rejected_by_the_concurrency_token(
        AssistantTestProvider provider)
    {
        await using var services = await database.CreateMigratedStorageAsync(provider);
        var factory = services.GetRequiredService<NhAssistantDbContextFactory>();
        var conversation = CreateConversation("owner-1");
        await using (var setup = factory.CreateDbContext())
        {
            setup.Conversations.Add(conversation);
            await setup.SaveChangesAsync();
        }

        await using var first = factory.CreateDbContext();
        await using var second = factory.CreateDbContext();
        var firstCopy = await first.Conversations.SingleAsync(item => item.Id == conversation.Id);
        var secondCopy = await second.Conversations.SingleAsync(item => item.Id == conversation.Id);
        firstCopy.Status = NhAssistantConversationStatuses.Running;
        firstCopy.ConcurrencyStamp = Guid.NewGuid();
        await first.SaveChangesAsync();
        secondCopy.Status = NhAssistantConversationStatuses.Failed;
        secondCopy.ConcurrencyStamp = Guid.NewGuid();

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }

    private static AssistantConversation CreateConversation(string owner)
    {
        var now = DateTimeOffset.UtcNow;
        return new AssistantConversation
        {
            Id = Guid.NewGuid(),
            OwnerActorId = owner,
            AgentId = "project-assistant",
            AgentVersion = 1,
            Status = NhAssistantConversationStatuses.Idle,
            CreatedAt = now,
            UpdatedAt = now,
            ConcurrencyStamp = Guid.NewGuid()
        };
    }

    private static async Task AssertAllTablesQueryableAsync(NhAssistantDbContext context)
    {
        Assert.Equal(0, await context.Conversations.CountAsync());
        Assert.Equal(0, await context.Messages.CountAsync());
        Assert.Equal(0, await context.ToolInvocations.CountAsync());
        Assert.Equal(0, await context.Approvals.CountAsync());
        Assert.Equal(0, await context.BudgetLedgers.CountAsync());
        Assert.Equal(0, await context.IdempotencyLeases.CountAsync());
    }
}
