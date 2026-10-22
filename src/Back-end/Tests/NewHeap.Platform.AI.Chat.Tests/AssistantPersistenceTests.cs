using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

public sealed class AssistantPersistenceTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
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
    public async Task A_host_that_runs_migrations_migrates_a_new_schema_before_it_seeds_the_code_agents(
        AssistantTestProvider provider)
    {
        // Other hosts lease schemas that are migrated already; this one starts on an empty schema.
        var schema = AssistantDatabaseFixture.UniqueSchema();
        await using var app = await AssistantWebApplication.StartAsync(
            database,
            new NhAiScriptedChatClient(),
            provider: provider,
            unmigratedSchema: schema);
        await using var context = app.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();

        Assert.Equal(schema, context.Model.GetDefaultSchema());
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.Contains(
            AssistantTestData.Agent().Id,
            await context.Agents.Select(agent => agent.Id).ToListAsync());
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Conversation_messages_invocations_and_approvals_round_trip(
        AssistantTestProvider provider)
    {
        await using var services = await database.CreateStorageAsync(provider);
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
        var (items, total) = await store.ListConversationsAsync("owner-1", null, 1, 20, CancellationToken.None);
        Assert.Equal(1, total);
        Assert.Equal(conversation.Id, Assert.Single(items).Conversation.Id);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Only_one_concurrent_status_transition_can_start_a_turn(
        AssistantTestProvider provider)
    {
        await using var services = await database.CreateStorageAsync(provider);
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
        await using var services = await database.CreateStorageAsync(provider);
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

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task The_McpReaders_migration_moves_the_reader_setting_of_a_server_to_a_reader_of_its_own_and_back(
        AssistantTestProvider provider)
    {
        // A schema of its own, in the shape before the readers had a table: the setting on the server.
        var schema = AssistantDatabaseFixture.UniqueSchema();
        var connectionString = await database.ConnectionStringAsync(provider);
        await using var services = AssistantDatabaseFixture.CreateStorageServices(provider, connectionString, schema, null).BuildServiceProvider();
        var factory = services.GetRequiredService<NhAssistantDbContextFactory>();
        await using (var context = factory.CreateDbContext())
        {
            await context.GetService<IMigrator>().MigrateAsync("McpServerReaders");
            await InsertServerAsync(context, "documents", ("read_document", "application/pdf application/msword", "confidential"));
            await InsertServerAsync(context, "plain", null);
        }

        await using (var context = factory.CreateDbContext())
        {
            await context.GetService<IMigrator>().MigrateAsync("McpReaders");
        }

        // The setting became the server's only reader, at priority 0; a server without one has none.
        await using (var context = factory.CreateDbContext())
        {
            var moved = Assert.Single(await context.McpReaders.AsNoTracking().ToListAsync());
            Assert.Equal(
                ("documents", "read_document", "application/pdf application/msword", "confidential", 0),
                (moved.ServerId, moved.ToolName, moved.MediaTypes, moved.MaxClassification, moved.Priority));

            // A second reader of the same server, asked first.
            context.McpReaders.Add(new AssistantMcpReader
            {
                ServerId = "documents",
                ToolName = "convert",
                MediaTypes = "application/msword",
                MaxClassification = "internal",
                Priority = 5
            });
            await context.SaveChangesAsync();
        }

        // Back down, the server keeps the reader the assistant asks first.
        await using (var context = factory.CreateDbContext())
        {
            await context.GetService<IMigrator>().MigrateAsync("McpServerReaders");
            Assert.Equal(
                [("documents", "convert", "application/msword", "internal"), ("plain", (string?)null, (string?)null, (string?)null)],
                await ReadReaderSettingsAsync(context));
        }

        // And up again to the current model, the readers deleted with their server.
        await using (var context = factory.CreateDbContext())
        {
            await context.Database.MigrateAsync();
            Assert.Empty(await context.Database.GetPendingMigrationsAsync());
            Assert.Equal(["documents"], await context.McpReaders.Select(reader => reader.ServerId).ToListAsync());
            await context.McpServers.Where(server => server.Id == "documents").ExecuteDeleteAsync();
            Assert.Empty(await context.McpReaders.ToListAsync());
        }
    }

    /// <summary>
    /// Inserts a server as the schema before the <c>McpReaders</c> migration stored it, with the reader
    /// setting in its own columns.
    /// </summary>
    private static async Task InsertServerAsync(
        NhAssistantDbContext context,
        string id,
        (string ToolName, string MediaTypes, string MaxClassification)? reader)
    {
        var sql = context.GetService<ISqlGenerationHelper>();
        var columns = new List<string> { "Id", "DisplayName", "Url", "AuthMode", "IsEnabled", "CreatedAt", "UpdatedAt" };
        var now = DateTimeOffset.UtcNow;
        var values = new List<object> { id, "Server " + id, "https://" + id + ".example.com/mcp", NhAssistantMcpAuthModes.None, true, now, now };
        if (reader is { } setting)
        {
            columns.AddRange(["ReaderToolName", "ReaderMediaTypes", "ReaderMaxClassification"]);
            values.AddRange([setting.ToolName, setting.MediaTypes, setting.MaxClassification]);
        }

        var names = string.Join(", ", columns.Select(column => sql.DelimitIdentifier(column)));
        var parameters = string.Join(", ", values.Select((_, index) => "{" + index + "}"));
        await context.Database.ExecuteSqlRawAsync(
            $"INSERT INTO {sql.DelimitIdentifier("AssistantMcpServer", context.Schema)} ({names}) VALUES ({parameters})",
            values);
    }

    /// <summary>
    /// The reader setting of every server, by id, from the columns before the <c>McpReaders</c>
    /// migration.
    /// </summary>
    private static async Task<(string Id, string? ToolName, string? MediaTypes, string? MaxClassification)[]> ReadReaderSettingsAsync(
        NhAssistantDbContext context)
    {
        var sql = context.GetService<ISqlGenerationHelper>();
        var settings = await context.Database
            .SqlQueryRaw<ReaderSetting>(
                $"SELECT {sql.DelimitIdentifier("Id")} AS {sql.DelimitIdentifier(nameof(ReaderSetting.Id))}, "
                + $"{sql.DelimitIdentifier("ReaderToolName")} AS {sql.DelimitIdentifier(nameof(ReaderSetting.ToolName))}, "
                + $"{sql.DelimitIdentifier("ReaderMediaTypes")} AS {sql.DelimitIdentifier(nameof(ReaderSetting.MediaTypes))}, "
                + $"{sql.DelimitIdentifier("ReaderMaxClassification")} AS {sql.DelimitIdentifier(nameof(ReaderSetting.MaxClassification))} "
                + $"FROM {sql.DelimitIdentifier("AssistantMcpServer", context.Schema)}")
            .ToListAsync();
        return settings
            .OrderBy(setting => setting.Id, StringComparer.Ordinal)
            .Select(setting => (setting.Id, setting.ToolName, setting.MediaTypes, setting.MaxClassification))
            .ToArray();
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
        Assert.Equal(0, await context.Agents.CountAsync());
        Assert.Equal(0, await context.AgentMcpServers.CountAsync());
        Assert.Equal(0, await context.ApplicationContexts.CountAsync());
        Assert.Equal(0, await context.McpServers.CountAsync());
        Assert.Equal(0, await context.McpTools.CountAsync());
        Assert.Equal(0, await context.McpReaders.CountAsync());
        Assert.Equal(0, await context.UserPreferences.CountAsync());
        Assert.Equal(0, await context.Participants.CountAsync());
        Assert.Equal(0, await context.PushSubscriptions.CountAsync());
        Assert.Equal(0, await context.NotificationSettings.CountAsync());
    }

    private sealed class ReaderSetting
    {
        public string Id { get; set; } = string.Empty;

        public string? ToolName { get; set; }

        public string? MediaTypes { get; set; }

        public string? MaxClassification { get; set; }
    }
}
