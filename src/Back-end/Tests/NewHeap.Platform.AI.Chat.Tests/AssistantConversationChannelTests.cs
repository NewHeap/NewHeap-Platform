using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// FB23 and FB24: an application's own channel lists a person's chats and agents and uploads a file to
/// a chat through <see cref="INhAssistantConversations"/>, with the checks and codes of
/// <c>GET conversations</c>, <c>GET agents</c> and <c>POST conversations/{id}/files</c>, which share them;
/// the upload reads a re-readable stream and never logs the file's name. Uploads go with their
/// conversation: the application's <see cref="INhAssistantFileRetention"/> forgets them after the
/// owner's archive committed and after subject retention deleted a thread, best effort, and an agent
/// whose profile chain may not receive the uploads' classification offers none. On both relational
/// providers where conversations persist.
/// </summary>
public sealed class AssistantConversationChannelTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string Channel = "messaging-chat";
    private const string FileAgent = "file-assistant";
    private const string ClosedAgent = "closed-agent";
    private const string InternalAgent = "internal-assistant";
    private const string FallbackAgent = "fallback-assistant";
    private const string RestrictedAgent = "restricted-assistant";
    private const string ElsewhereAgent = "elsewhere-assistant";
    private const string ManagedFileAgent = "managed-file-assistant";
    private const string UploadProfile = "upload-chat";
    private const string InternalWithFallbackProfile = "internal-with-fallback";
    private const string ElsewhereProfile = "upload-elsewhere";

    /// <summary>
    /// A name that must never reach a log message, whatever becomes of the upload.
    /// </summary>
    private const string SecretName = "Board-minutes-7f3a9c.pdf";

    private const string PlanText = "%PDF-1.7\nThe launch moves to May.\n%%EOF";

    private static readonly byte[] PlanPdf = Encoding.UTF8.GetBytes(PlanText);

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D];

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_channel_lists_the_persons_chats_and_agents_as_the_endpoints_do(AssistantTestProvider provider)
    {
        await using var app = await StartAsync(provider);
        using var client = app.CreateClient();
        var owner = Person("user-1");
        var first = await CreateAsync(app, owner, FileAgent, "First");
        var second = await CreateAsync(app, owner, InternalAgent, "Second");
        // A chat of someone else that the person was invited to, a thread and an archived chat.
        var shared = await CreateAsync(app, Person("user-2"), FileAgent, "Shared");
        await AddParticipantAsync(app.Services, shared, "user-1");
        var thread = await AddThreadAsync(app, FileAgent, Guid.NewGuid().ToString("N"));
        await AddParticipantAsync(app.Services, thread, "user-1");
        var archived = await CreateAsync(app, owner, FileAgent, "Archived");
        using (var deleted = await client.DeleteAsync($"/api/assistant/conversations/{archived}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        var listed = await ListAsync(app, owner, null);

        Assert.True(listed.Success);
        Assert.Equal(3, listed.Data!.Total);
        Assert.Equal(
            [(shared, NhAssistantParticipantRoles.Participant), (second, NhAssistantParticipantRoles.Owner), (first, NhAssistantParticipantRoles.Owner)],
            listed.Data.Items.Select(item => (item.Id, item.Role)).ToArray());
        // The endpoint gives the same list, page and refusal.
        Assert.Equal(
            JsonSerializer.Serialize(listed.Data, NhAssistantJsonSerializerContext.Default.NhAssistantConversationListDto),
            await client.GetStringAsync("/api/assistant/conversations"));
        var page = await ListAsync(app, owner, new NhAssistantConversationPage(2, 2));
        Assert.Equal(3, page.Data!.Total);
        Assert.Equal([first], page.Data.Items.Select(item => item.Id).ToArray());
        Assert.Equal(
            JsonSerializer.Serialize(page.Data, NhAssistantJsonSerializerContext.Default.NhAssistantConversationListDto),
            await client.GetStringAsync("/api/assistant/conversations?page=2&itemsPerPage=2"));
        foreach (var invalid in new[] { new NhAssistantConversationPage(0, 20), new NhAssistantConversationPage(1, 0), new NhAssistantConversationPage(1, 101) })
        {
            Assert.Equal(NhAssistantErrorCodes.MessageInvalid, CodeOf(await ListAsync(app, owner, invalid)));
            using var refused = await client.GetAsync($"/api/assistant/conversations?page={invalid.Page}&itemsPerPage={invalid.ItemsPerPage}");
            Assert.Equal((HttpStatusCode.BadRequest, NhAssistantErrorCodes.MessageInvalid), (refused.StatusCode, await CodeOfAsync(refused)));
        }

        // The agents: those the person may use, with what they may attach; the endpoint's list.
        var agents = await ListAgentsAsync(app, owner);
        Assert.True(agents.Success);
        Assert.Equal(
            JsonSerializer.Serialize(agents.Data!.ToArray(), NhAssistantJsonSerializerContext.Default.NhAssistantAgentSummaryDtoArray),
            await client.GetStringAsync("/api/assistant/agents"));
        Assert.DoesNotContain(agents.Data!, agent => agent.Id == ManagedFileAgent);
        Assert.NotNull(agents.Data!.Single(agent => agent.Id == FileAgent).Uploads);
        var forManager = await ListAgentsAsync(app, Person("user-1", manager: true));
        Assert.NotNull(forManager.Data!.Single(agent => agent.Id == ManagedFileAgent).Uploads);

        // Authorization negatives: without the access policy, and a principal of another person.
        Assert.Equal(NhAssistantErrorCodes.AgentForbidden, CodeOf(await ListAsync(app, Person("user-1", access: false), null)));
        Assert.Equal(NhAssistantErrorCodes.AgentForbidden, CodeOf(await ListAgentsAsync(app, Person("user-1", access: false))));
        var someoneElse = new NhAssistantDecidingPerson(owner.User, "user-2", null);
        Assert.Equal(NhAssistantErrorCodes.ContextUnavailable, CodeOf(await ListAsync(app, someoneElse, null)));
        Assert.Equal(NhAssistantErrorCodes.ContextUnavailable, CodeOf(await ListAgentsAsync(app, someoneElse)));
        // Someone who takes part in nothing lists nothing.
        Assert.Equal(0, (await ListAsync(app, Person("user-3"), null)).Data!.Total);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_channel_uploads_a_rereadable_stream_that_the_next_message_carries_to_the_model(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient().RespondWithText("The plan moves the launch to May.");
        var logs = new CapturingLoggerProvider();
        await using var app = await StartAsync(provider, model, logs);
        var owner = Person("user-1");
        var chat = await CreateAsync(app, owner, FileAgent, "Launch");
        var store = app.Services.GetRequiredService<TestUploads>();
        // The file starts after what the channel already read of its delivery.
        var delivery = new MemoryStream([.. Encoding.ASCII.GetBytes("HEADER"), .. PlanPdf]) { Position = 6 };

        var uploaded = await UploadAsync(app, owner, chat, new NhAssistantConversationUpload(@"C:\Inbox\" + SecretName, NhAiMediaTypes.Pdf, delivery));

        Assert.True(uploaded.Success);
        var reference = uploaded.Data!;
        var stored = Assert.Single(store.Stored.Values);
        Assert.Equal(stored.Reference.Id, reference.Id);
        // The content check, the scanner and the file source each read the file from its start.
        var scanned = Assert.Single(store.Scanned);
        Assert.Equal((SecretName, NhAiMediaTypes.Pdf, "user-1"), (scanned.Name, scanned.MediaType, scanned.ActorId));
        Assert.Equal(PlanPdf, scanned.Content);
        Assert.Equal(PlanPdf, stored.Content);
        Assert.Equal(
            (SecretName, (long)PlanPdf.Length, NhAiDataClassification.Confidential, chat, NhAssistantFileOrigins.Attachment),
            (stored.Upload.Name, stored.Upload.Length, stored.Upload.Classification, stored.Upload.ConversationId, stored.Actor.Origin));
        // The caller keeps its stream: open and where the file ended.
        Assert.True(delivery.CanRead);

        // The channel's next message carries the reference, and the model reads the file.
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var sent = await scope.ServiceProvider.GetRequiredService<INhAssistantConversationTurns>().SendAsync(
                owner,
                chat,
                new NhAssistantConversationMessage("What does the plan say?") { Files = [reference] },
                new NhAssistantConversationTurnOptions(Channel));
            Assert.Equal(NhAssistantTurnStatuses.Completed, sent.Data!.Status);
        }
        Assert.Equal(PlanPdf, model.Requests[0].Messages[^1].Contents.OfType<DataContent>().Single().Data.ToArray());

        // A stream the checks cannot read again is the caller's mistake.
        await using var forwardOnly = new ForwardOnlyStream(PlanPdf);
        await Assert.ThrowsAsync<ArgumentException>(() => UploadAsync(app, owner, chat, new NhAssistantConversationUpload("Plan.pdf", NhAiMediaTypes.Pdf, forwardOnly)));
        Assert.DoesNotContain(logs.Messages, message => message.Contains(SecretName, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task The_service_and_the_endpoint_refuse_uploads_alike_and_never_log_the_file_name(AssistantTestProvider provider)
    {
        var logs = new CapturingLoggerProvider();
        await using var app = await StartAsync(provider, logs: logs);
        using var client = app.CreateClient();
        using var participantClient = app.CreateClient("user-2");
        using var outsiderClient = app.CreateClient("user-3");
        var owner = Person("user-1");
        var participant = Person("user-2");
        var store = app.Services.GetRequiredService<TestUploads>();
        var chat = await CreateAsync(app, owner, FileAgent, "Files");
        await AddParticipantAsync(app.Services, chat, "user-2");
        var closed = await CreateAsync(app, owner, ClosedAgent, "Closed");
        var internalChat = await CreateAsync(app, owner, InternalAgent, "Internal");
        var managed = await CreateAsync(app, Person("user-1", manager: true), ManagedFileAgent, "Managed");
        await AddParticipantAsync(app.Services, managed, "user-2");

        // The content, the type, the size and the name.
        await AssertBothRefuseAsync(app, client, owner, chat, SecretName, NhAiMediaTypes.Pdf, Encoding.UTF8.GetBytes("%PDF-1.7\n" + new string('x', 4_096)), HttpStatusCode.RequestEntityTooLarge, NhAssistantErrorCodes.FileTooLarge);
        await AssertBothRefuseAsync(app, client, owner, chat, SecretName, "text/plain", Encoding.UTF8.GetBytes("Notes"), HttpStatusCode.UnsupportedMediaType, NhAssistantErrorCodes.FileTypeNotAccepted);
        await AssertBothRefuseAsync(app, client, owner, chat, SecretName, NhAiMediaTypes.Pdf, Png, HttpStatusCode.UnsupportedMediaType, NhAssistantErrorCodes.FileTypeNotAccepted);
        await AssertBothRefuseAsync(app, client, owner, chat, SecretName, NhAiMediaTypes.Pdf, [], HttpStatusCode.BadRequest, NhAssistantErrorCodes.FileUploadInvalid);
        // The agent: one that takes no files, one whose profile may not receive them.
        await AssertBothRefuseAsync(app, client, owner, closed, SecretName, NhAiMediaTypes.Pdf, PlanPdf, HttpStatusCode.Conflict, NhAssistantErrorCodes.FilesNotAccepted);
        await AssertBothRefuseAsync(app, client, owner, internalChat, SecretName, NhAiMediaTypes.Pdf, PlanPdf, HttpStatusCode.Conflict, NhAssistantErrorCodes.FilesNotAccepted);
        // Participant roles: someone outside the chat finds nothing, and a participant must satisfy
        // the agent's policy, as must the owner who lost it.
        await AssertBothRefuseAsync(app, outsiderClient, Person("user-3"), chat, SecretName, NhAiMediaTypes.Pdf, PlanPdf, HttpStatusCode.NotFound, NhAssistantErrorCodes.ConversationNotFound);
        await AssertBothRefuseAsync(app, participantClient, participant, managed, SecretName, NhAiMediaTypes.Pdf, PlanPdf, HttpStatusCode.Forbidden, NhAssistantErrorCodes.AgentForbidden);
        Assert.Equal(NhAssistantErrorCodes.AgentForbidden, CodeOf(await UploadAsync(app, owner, managed, Upload(SecretName, PlanPdf))));
        Assert.Equal(NhAssistantErrorCodes.ConversationNotFound, CodeOf(await UploadAsync(app, owner, Guid.NewGuid(), Upload(SecretName, PlanPdf))));
        // The access policy, and a principal that is not the person named.
        Assert.Equal(NhAssistantErrorCodes.AgentForbidden, CodeOf(await UploadAsync(app, Person("user-1", access: false), chat, Upload(SecretName, PlanPdf))));
        Assert.Equal(NhAssistantErrorCodes.ContextUnavailable, CodeOf(await UploadAsync(app, new NhAssistantDecidingPerson(owner.User, "user-2", null), chat, Upload(SecretName, PlanPdf))));
        // The scanner and the file source keep their codes.
        store.ScanOutcome = TestScanOutcome.Refuse;
        await AssertBothRefuseAsync(app, client, owner, chat, SecretName, NhAiMediaTypes.Pdf, PlanPdf, HttpStatusCode.UnprocessableEntity, NhAssistantErrorCodes.FileScanRefused);
        store.ScanOutcome = TestScanOutcome.Throw;
        await AssertBothRefuseAsync(app, client, owner, chat, SecretName, NhAiMediaTypes.Pdf, PlanPdf, HttpStatusCode.ServiceUnavailable, NhAssistantErrorCodes.FileUploadFailed);
        store.ScanOutcome = TestScanOutcome.Pass;
        store.RefuseWith = "application-quota-reached";
        await AssertBothRefuseAsync(app, client, owner, chat, SecretName, NhAiMediaTypes.Pdf, PlanPdf, HttpStatusCode.UnprocessableEntity, NhAssistantErrorCodes.FileUploadRefused);
        store.RefuseWith = null;
        store.Fail = true;
        await AssertBothRefuseAsync(app, client, owner, chat, SecretName, NhAiMediaTypes.Pdf, PlanPdf, HttpStatusCode.ServiceUnavailable, NhAssistantErrorCodes.FileUploadFailed);
        store.Fail = false;
        Assert.Empty(store.Stored);

        // A participant who may use the agent uploads, through either.
        Assert.True((await UploadAsync(app, participant, chat, Upload("Notes.pdf", PlanPdf))).Success);
        using (var uploaded = await UploadHttpAsync(participantClient, chat, "Plan.pdf", NhAiMediaTypes.Pdf, PlanPdf))
        {
            Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
        }
        Assert.Equal(["user-2", "user-2"], store.Stored.Values.Select(item => item.Actor.ActorId).ToArray());

        // The failures were logged, never with the file's name.
        Assert.Contains(logs.Messages, message => message.Contains("upload scanner failed", StringComparison.Ordinal));
        Assert.Contains(logs.Messages, message => message.Contains("file source failed", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Messages, message => message.Contains(SecretName, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task In_an_agents_thread_only_the_endpoint_uploads_and_only_for_a_participant(AssistantTestProvider provider)
    {
        await using var app = await StartAsync(provider);
        var subjects = app.Services.GetRequiredService<TestSubjects>();
        subjects.Roles["reader-1"] = NhAiSubjectRole.Reader;
        var thread = await AddThreadAsync(app, FileAgent, Guid.NewGuid().ToString("N"));
        using var reader = app.CreateClient("reader-1");
        using var member = app.CreateClient("member-1");

        using (var refused = await UploadHttpAsync(reader, thread, "Plan.pdf", NhAiMediaTypes.Pdf, PlanPdf))
        {
            Assert.Equal((HttpStatusCode.Forbidden, NhAssistantErrorCodes.SubjectAccessDenied), (refused.StatusCode, await CodeOfAsync(refused)));
        }
        using (var uploaded = await UploadHttpAsync(member, thread, "Plan.pdf", NhAiMediaTypes.Pdf, PlanPdf))
        {
            Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
        }
        var stored = Assert.Single(app.Services.GetRequiredService<TestUploads>().Stored.Values);
        Assert.Equal(("member-1", "project"), (stored.Actor.ActorId, stored.Actor.Subject!.Type));

        // The services serve chats only: a thread answers like an unknown chat, even to its member.
        Assert.Equal(NhAssistantErrorCodes.ConversationNotFound, CodeOf(await UploadAsync(app, Person("member-1"), thread, Upload("Plan.pdf", PlanPdf))));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task An_archive_forgets_the_chats_uploads_once_and_a_failing_retention_only_warns(AssistantTestProvider provider)
    {
        var logs = new CapturingLoggerProvider();
        await using var app = await StartAsync(provider, logs: logs, retention: true);
        using var client = app.CreateClient();
        using var participantClient = app.CreateClient("user-2");
        var owner = Person("user-1");
        var retention = app.Services.GetRequiredService<TestFileRetention>();
        var store = app.Services.GetRequiredService<TestUploads>();
        var chat = await CreateAsync(app, owner, FileAgent, "Archive me");
        var kept = await CreateAsync(app, owner, FileAgent, "Keep me");
        await AddParticipantAsync(app.Services, chat, "user-2");
        Assert.True((await UploadAsync(app, owner, chat, Upload(SecretName, PlanPdf))).Success);
        Assert.True((await UploadAsync(app, owner, kept, Upload("Kept.pdf", PlanPdf))).Success);

        // A participant who leaves forgets nothing; the owner's archive forgets the chat's uploads.
        using (var left = await participantClient.DeleteAsync($"/api/assistant/conversations/{chat}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, left.StatusCode);
        }
        Assert.Empty(retention.Calls);
        using (var archived = await client.DeleteAsync($"/api/assistant/conversations/{chat}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, archived.StatusCode);
        }

        var call = Assert.Single(retention.Calls);
        Assert.Equal(
            (chat, FileAgent, NhAssistantFileRetentionReasons.Archived, (string?)null, (NhAiSubjectRef?)null),
            (call.ConversationId, call.AgentId, call.Reason, call.TenantId, call.Subject));
        Assert.Equal(["Kept.pdf"], store.Stored.Values.Select(item => item.Upload.Name).ToArray());
        // The archive happens once: archiving again finds nothing and forgets nothing.
        using (var again = await client.DeleteAsync($"/api/assistant/conversations/{chat}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        }
        Assert.Single(retention.Calls);

        // A retention that fails or throws only warns: the archive stays.
        retention.Outcome = TestRetentionOutcome.Fail;
        using (var archived = await client.DeleteAsync($"/api/assistant/conversations/{kept}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, archived.StatusCode);
        }
        retention.Outcome = TestRetentionOutcome.Throw;
        var thrown = await CreateAsync(app, owner, FileAgent, "Thrown");
        using (var archived = await client.DeleteAsync($"/api/assistant/conversations/{thrown}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, archived.StatusCode);
        }

        Assert.Equal([chat, kept, thrown], retention.Calls.Select(item => item.ConversationId).ToArray());
        Assert.Equal(0, (await ListAsync(app, owner, null)).Data!.Total);
        using (var gone = await client.GetAsync($"/api/assistant/conversations/{kept}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        }
        var warnings = logs.Entries
            .Where(entry => entry.Level == LogLevel.Warning && entry.Message.Contains("file retention", StringComparison.Ordinal))
            .Select(entry => entry.Message)
            .ToArray();
        Assert.Equal(2, warnings.Length);
        Assert.Contains(warnings, message => message.Contains(kept.ToString(), StringComparison.Ordinal) && message.Contains("storage-offline", StringComparison.Ordinal));
        Assert.Contains(warnings, message => message.Contains(thrown.ToString(), StringComparison.Ordinal) && message.Contains(nameof(IOException), StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Messages, message => message.Contains(SecretName, StringComparison.OrdinalIgnoreCase));

        // The application's retention is idempotent: forgetting the archived chat again succeeds and
        // removes nothing.
        retention.Outcome = TestRetentionOutcome.Forget;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            Assert.True((await scope.ServiceProvider.GetRequiredService<INhAssistantFileRetention>().ForgetConversationAsync(call, CancellationToken.None)).Success);
        }
        Assert.Equal(["Kept.pdf"], store.Stored.Values.Select(item => item.Upload.Name).ToArray());
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Subject_retention_forgets_the_uploads_of_every_thread_it_deletes_and_only_warns_on_failure(AssistantTestProvider provider)
    {
        var logs = new CapturingLoggerProvider();
        await using var app = await StartAsync(provider, logs: logs, retention: true);
        var retention = app.Services.GetRequiredService<TestFileRetention>();
        var subjectId = Guid.NewGuid().ToString("N");
        var subject = new NhAiSubjectRef("project", subjectId);
        var scopeKey = NhAiSubjectInfo.CreateScopeKey(null, null);
        var first = await AddThreadAsync(app, FileAgent, subjectId);
        var second = await AddThreadAsync(app, InternalAgent, subjectId);
        var other = await AddThreadAsync(app, FileAgent, Guid.NewGuid().ToString("N"));
        retention.Outcome = TestRetentionOutcome.Throw;

        int forgotten;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            forgotten = (await scope.ServiceProvider.GetRequiredService<INhAiSubjectRetention>().ForgetSubjectAsync(subject, scopeKey)).Data;
        }

        // Both threads are gone although the retention threw, and each was forgotten once.
        Assert.Equal(2, forgotten);
        Assert.Equal(
            new[] { (first, FileAgent), (second, InternalAgent) }.Order().ToArray(),
            retention.Calls.Select(call => (call.ConversationId, call.AgentId)).Order().ToArray());
        Assert.All(retention.Calls, call => Assert.Equal((NhAssistantFileRetentionReasons.SubjectForgotten, subject), (call.Reason, call.Subject)));
        Assert.DoesNotContain(retention.Calls, call => call.ConversationId == other);
        Assert.Equal(2, logs.Entries.Count(entry => entry.Level == LogLevel.Warning && entry.Message.Contains("file retention", StringComparison.Ordinal)));
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var runs = scope.ServiceProvider.GetRequiredService<NhAssistantRunStore>();
            Assert.Null(await runs.FindThreadByConversationAsync(first, CancellationToken.None));
            Assert.NotNull(await runs.FindThreadByConversationAsync(other, CancellationToken.None));
            // Forgetting the subject again deletes and forgets nothing.
            Assert.Equal(0, (await scope.ServiceProvider.GetRequiredService<INhAiSubjectRetention>().ForgetSubjectAsync(subject, scopeKey)).Data);
        }
        Assert.Equal(2, retention.Calls.Count);
    }

    [Fact]
    public async Task Agents_offer_uploads_only_where_their_profile_chain_may_receive_the_upload_classification()
    {
        await using (var app = await StartAsync(AssistantTestProvider.PostgreSql))
        {
            using var client = app.CreateClient();
            var agents = await UploadsByAgentAsync(client, "/api/assistant/agents");
            var status = await UploadsByAgentAsync(client, "/api/assistant/status");

            // Confidential uploads: the upload profile permits them, a fallback does, the internal
            // profile does not, an agent's own restricted data needs more than the profile permits, and a
            // profile that permits them elsewhere does not serve the execution region.
            Assert.True(agents[FileAgent]);
            Assert.True(agents[FallbackAgent]);
            Assert.False(agents[InternalAgent]);
            Assert.False(agents[RestrictedAgent]);
            Assert.False(agents[ElsewhereAgent]);
            Assert.False(agents[ClosedAgent]);
            Assert.Equal(agents, status);
            var chat = await CreateAsync(app, Person("user-1"), RestrictedAgent, "Restricted");
            Assert.Equal(NhAssistantErrorCodes.FilesNotAccepted, CodeOf(await UploadAsync(app, Person("user-1"), chat, Upload("Plan.pdf", PlanPdf))));
        }

        // Internal uploads reach the internal profile; the restricted agent still needs more.
        await using (var app = await StartAsync(AssistantTestProvider.PostgreSql, configureUploads: uploads => uploads.DefaultClassification = NhAiDataClassification.Internal))
        {
            using var client = app.CreateClient();
            var agents = await UploadsByAgentAsync(client, "/api/assistant/agents");

            Assert.True(agents[InternalAgent]);
            Assert.True(agents[FileAgent]);
            Assert.False(agents[RestrictedAgent]);
            var chat = await CreateAsync(app, Person("user-1"), InternalAgent, "Internal");
            Assert.True((await UploadAsync(app, Person("user-1"), chat, Upload("Plan.pdf", PlanPdf))).Success);
        }
    }

    private Task<AssistantWebApplication> StartAsync(
        AssistantTestProvider provider,
        NhAiScriptedChatClient? model = null,
        CapturingLoggerProvider? logs = null,
        bool retention = false,
        Action<NhAssistantUploadOptions>? configureUploads = null)
    {
        return AssistantWebApplication.StartAsync(
            database,
            new NhAiScriptedChatClient(),
            limits: limits => limits.MaxFileBytes = 4_096,
            configure: services =>
            {
                if (logs is not null)
                {
                    services.AddSingleton<ILoggerProvider>(logs);
                }
                services.AddSingleton<TestUploads>();
                services.AddSingleton<TestFileRetention>();
                services.AddSingleton<TestSubjects>();
                services.AddKeyedSingleton<IChatClient>("upload-model", model ?? new NhAiScriptedChatClient());
                // The configured agents never run here; they stand for agents with run settings.
                services.Remove(services.Single(descriptor => descriptor.ImplementationType == typeof(NhAssistantStartupValidator)));
                services.AddNewHeapPlatformAI(ai => ai
                    .AddChatProfile(UploadProfile, profile => UploadChat(profile)
                        .PermitDataClassifications(NhAiDataClassification.Internal, NhAiDataClassification.Confidential)
                        .PermitExecutionRegions("local"))
                    .AddChatProfile(InternalWithFallbackProfile, profile => UploadChat(profile)
                        .PermitDataClassifications(NhAiDataClassification.Internal)
                        .PermitExecutionRegions("local")
                        .WithFallbackProfiles(UploadProfile))
                    .AddChatProfile(ElsewhereProfile, profile => UploadChat(profile)
                        .PermitDataClassifications(NhAiDataClassification.Internal, NhAiDataClassification.Confidential)
                        .PermitExecutionRegions("eu-west")));
            },
            assistantBuilder: assistant =>
            {
                assistant
                    .AddSubjectType<TestSubjectResolver>()
                    .UseFileSource<TestUploadFileSource>()
                    .ConfigureUploads(uploads =>
                    {
                        uploads.UseScanner<TestUploadScanner>();
                        configureUploads?.Invoke(uploads);
                    })
                    .AddAgent(AssistantTestData.Agent(FileAgent) with { ProfileName = UploadProfile })
                    .AddAgent(AssistantTestData.Agent(ClosedAgent) with { ProfileName = UploadProfile })
                    .AddAgent(AssistantTestData.Agent(InternalAgent))
                    .AddAgent(AssistantTestData.Agent(FallbackAgent) with { ProfileName = InternalWithFallbackProfile })
                    .AddAgent(AssistantTestData.Agent(RestrictedAgent) with { ProfileName = UploadProfile })
                    .AddAgent(AssistantTestData.Agent(ElsewhereAgent) with { ProfileName = ElsewhereProfile })
                    .AddAgent(AssistantTestData.Agent(ManagedFileAgent, requiredPolicy: TestAuthenticationHandler.ManagerPolicy) with { ProfileName = UploadProfile })
                    .ConfigureAgent(ClosedAgent, _ => { })
                    .ConfigureAgent(RestrictedAgent, agent => agent
                        .AcceptsFiles()
                        .DataClassification(NhAiDataClassification.Restricted));
                if (retention)
                {
                    assistant.UseFileRetention<TestFileRetentionService>();
                }
            },
            provider: provider);
    }

    private static NhAiModelProfileBuilder UploadChat(NhAiModelProfileBuilder profile)
    {
        return profile
            .UseKeyedClient("upload-model")
            .RequireCapabilities(NhAiModelCapability.FunctionCalling)
            .WithStreaming(NhAiStreamingPolicy.Allowed)
            .AcceptMediaTypes(NhAiMediaTypes.Pdf, NhAiMediaTypes.Png)
            .WithBudget(maxInputTokens: 32_000, maxOutputTokens: 1_024, maxCalls: 16);
    }

    /// <summary>
    /// Uploads through the endpoint and through the service, and asserts that both refuse with
    /// <paramref name="code"/>, the endpoint with <paramref name="status"/>.
    /// </summary>
    private static async Task AssertBothRefuseAsync(
        AssistantWebApplication app,
        HttpClient client,
        NhAssistantDecidingPerson person,
        Guid conversationId,
        string name,
        string mediaType,
        byte[] content,
        HttpStatusCode status,
        string code)
    {
        using (var response = await UploadHttpAsync(client, conversationId, name, mediaType, content))
        {
            Assert.Equal((status, code), (response.StatusCode, await CodeOfAsync(response)));
        }

        await using var stream = new MemoryStream(content);
        Assert.Equal(code, CodeOf(await UploadAsync(app, person, conversationId, new NhAssistantConversationUpload(name, mediaType, stream))));
    }

    private static NhAssistantConversationUpload Upload(string name, byte[] content)
    {
        return new NhAssistantConversationUpload(name, NhAiMediaTypes.Pdf, new MemoryStream(content));
    }

    private static async Task<Guid> CreateAsync(AssistantWebApplication app, NhAssistantDecidingPerson person, string agentId, string title)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var created = await scope.ServiceProvider.GetRequiredService<INhAssistantConversations>().CreateAsync(
            person,
            new NhAssistantNewConversation(agentId) { Title = title });
        Assert.True(created.Success, CodeOf(created));
        return created.Data!.Id;
    }

    private static async Task<TaskResult<NhAssistantConversationListDto>> ListAsync(
        AssistantWebApplication app,
        NhAssistantDecidingPerson person,
        NhAssistantConversationPage? page)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INhAssistantConversations>().ListAsync(person, page);
    }

    private static async Task<TaskResult<IReadOnlyList<NhAssistantAgentSummaryDto>>> ListAgentsAsync(
        AssistantWebApplication app,
        NhAssistantDecidingPerson person)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INhAssistantConversations>().ListAgentsAsync(person);
    }

    private static async Task<TaskResult<NhAiFileReference>> UploadAsync(
        AssistantWebApplication app,
        NhAssistantDecidingPerson person,
        Guid conversationId,
        NhAssistantConversationUpload upload)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INhAssistantConversations>().UploadAsync(person, conversationId, upload);
    }

    private static async Task<HttpResponseMessage> UploadHttpAsync(
        HttpClient client,
        Guid conversationId,
        string name,
        string mediaType,
        byte[] content)
    {
        var part = new ByteArrayContent(content);
        part.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        var form = new MultipartFormDataContent { { part, NhAssistantUploadEndpoints.FileField, name } };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/assistant/conversations/{conversationId}/files")
        {
            Content = form
        };
        request.Headers.Add(NhAssistantUploadEndpoints.UploadHeader, "1");
        return await client.SendAsync(request);
    }

    /// <summary>
    /// Whether each agent of <paramref name="path"/> offers uploads.
    /// </summary>
    private static async Task<Dictionary<string, bool>> UploadsByAgentAsync(HttpClient client, string path)
    {
        using var document = JsonDocument.Parse(await client.GetStringAsync(path));
        var agents = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement
            : document.RootElement.GetProperty("agents");
        return agents.EnumerateArray().ToDictionary(
            agent => agent.GetProperty("id").GetString()!,
            agent => agent.GetProperty("uploads").ValueKind == JsonValueKind.Object);
    }

    private static async Task AddParticipantAsync(IServiceProvider services, Guid conversationId, string actorId)
    {
        await using var scope = services.CreateAsyncScope();
        var added = await scope.ServiceProvider.GetRequiredService<INhAssistantStore>().TryAddParticipantAsync(
            new AssistantConversationParticipant
            {
                ConversationId = conversationId,
                ActorId = actorId,
                DisplayName = actorId,
                JoinedVia = NhAssistantParticipantSources.Link,
                JoinedAt = DateTimeOffset.UtcNow
            },
            5,
            CancellationToken.None);
        Assert.Equal(NhAssistantParticipantAddResult.Added, added);
    }

    /// <summary>
    /// An agent's thread about a project of the whole application.
    /// </summary>
    private static async Task<Guid> AddThreadAsync(AssistantWebApplication app, string agentId, string subjectId)
    {
        var now = DateTimeOffset.UtcNow;
        var conversation = new AssistantConversation
        {
            Id = Guid.NewGuid(),
            Kind = NhAssistantConversationKinds.Thread,
            OwnerActorId = "agent:" + agentId,
            AgentId = agentId,
            AgentVersion = 1,
            Title = "Project",
            Status = NhAssistantConversationStatuses.Idle,
            CreatedAt = now,
            UpdatedAt = now,
            ConcurrencyStamp = Guid.NewGuid()
        };
        var thread = new AssistantThread
        {
            ConversationId = conversation.Id,
            ScopeKey = NhAiSubjectInfo.CreateScopeKey(null, null),
            AgentId = agentId,
            SubjectType = "project",
            SubjectId = subjectId,
            Accountability = NhAssistantAccountabilities.Agent,
            AccountableOwnerId = "agent:" + agentId,
            MemoryConcurrencyStamp = Guid.NewGuid(),
            CreatedAt = now,
            UpdatedAt = now
        };
        await new NhAssistantRunStore(app.Services.GetRequiredService<NhAssistantDbContextFactory>())
            .AddThreadAsync(conversation, thread, CancellationToken.None);
        return conversation.Id;
    }

    /// <summary>
    /// The person as the application's channel authenticated them.
    /// </summary>
    private static NhAssistantDecidingPerson Person(string user, bool access = true, bool manager = false)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, user) };
        if (access)
        {
            claims.Add(new Claim("permission", "assistant"));
        }
        if (manager)
        {
            claims.Add(new Claim("permission", "manager"));
        }

        return new NhAssistantDecidingPerson(new ClaimsPrincipal(new ClaimsIdentity(claims, Channel)), user, null);
    }

    private static string? CodeOf(TaskResult result)
    {
        return result.GetResultItems().Select(item => item.Name).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
    }

    private static async Task<string?> CodeOfAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        using var document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    /// <summary>
    /// A stream that can be read once, as a network stream.
    /// </summary>
    private sealed class ForwardOnlyStream(byte[] content) : MemoryStream(content)
    {
        public override bool CanSeek => false;
    }
}

/// <summary>
/// What <see cref="TestFileRetentionService"/> does with the next conversation it forgets.
/// </summary>
public enum TestRetentionOutcome
{
    Forget,
    Fail,
    Throw
}

/// <summary>
/// The conversations the test application's file retention was asked to forget, and how it answers.
/// </summary>
public sealed class TestFileRetention
{
    public ConcurrentQueue<NhAssistantForgottenConversation> Calls { get; } = new();

    public TestRetentionOutcome Outcome { get; set; }
}

/// <summary>
/// The test application's file retention: it removes the uploads of the conversation from
/// <see cref="TestUploads"/>, so forgetting a conversation again removes nothing, and fails or throws
/// with the file's name in its messages when <see cref="TestFileRetention.Outcome"/> says so.
/// </summary>
public sealed class TestFileRetentionService(TestFileRetention retention, TestUploads uploads) : INhAssistantFileRetention
{
    public Task<TaskResult> ForgetConversationAsync(
        NhAssistantForgottenConversation conversation,
        CancellationToken cancellationToken)
    {
        retention.Calls.Enqueue(conversation);

        var names = string.Join(", ", uploads.Stored.Values
            .Where(item => item.Upload.ConversationId == conversation.ConversationId)
            .Select(item => item.Upload.Name));
        switch (retention.Outcome)
        {
            case TestRetentionOutcome.Fail:
                return Task.FromResult(TaskResult.Failed("storage-offline", $"The uploads {names} stay for now."));
            case TestRetentionOutcome.Throw:
                throw new IOException($"The storage lost {names}.");
        }

        foreach (var stored in uploads.Stored.Where(item => item.Value.Upload.ConversationId == conversation.ConversationId).ToArray())
        {
            uploads.Stored.TryRemove(stored.Key, out _);
        }

        return Task.FromResult(TaskResult.Succeeded());
    }
}
