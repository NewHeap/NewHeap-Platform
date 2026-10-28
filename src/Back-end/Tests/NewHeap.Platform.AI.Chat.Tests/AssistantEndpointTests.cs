using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using Xunit;
using static NewHeap.Platform.AI.Chat.Tests.Infrastructure.ServerSentEventReader;

namespace NewHeap.Platform.AI.Chat.Tests;

public sealed class AssistantEndpointTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    [Fact]
    public async Task Status_agents_and_conversation_endpoints_follow_the_contract()
    {
        await using var app = await AssistantWebApplication.StartAsync(database, new NhAiScriptedChatClient());
        using var client = app.CreateClient();

        using var status = await client.GetAsync("/api/assistant/status");
        var statusJson = await ReadJsonAsync(status);
        Assert.True(statusJson.GetProperty("enabled").GetBoolean());
        Assert.Equal(8_000, statusJson.GetProperty("limits").GetProperty("maxMessageChars").GetInt32());
        var agent = Assert.Single(statusJson.GetProperty("agents").EnumerateArray());
        Assert.Equal("project-assistant", agent.GetProperty("id").GetString());
        Assert.True(agent.GetProperty("canMutate").GetBoolean());

        using var agents = await client.GetAsync("/api/assistant/agents");
        Assert.Single((await ReadJsonAsync(agents)).EnumerateArray());

        using var created = await client.PostAsync(
            "/api/assistant/conversations",
            Json(new { agentId = "project-assistant", title = "Roadmap" }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var conversation = await ReadJsonAsync(created);
        var id = conversation.GetProperty("id").GetString();
        Assert.EndsWith($"/api/assistant/conversations/{id}", created.Headers.Location!.ToString());
        Assert.Equal("idle", conversation.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, conversation.GetProperty("pendingApproval").ValueKind);
        Assert.Empty(conversation.GetProperty("messages").EnumerateArray());

        using var list = await client.GetAsync("/api/assistant/conversations?page=1&itemsPerPage=10");
        var listJson = await ReadJsonAsync(list);
        Assert.Equal(1, listJson.GetProperty("total").GetInt32());
        Assert.Equal("Roadmap", listJson.GetProperty("items")[0].GetProperty("title").GetString());

        using var otherUser = app.CreateClient("user-2");
        Assert.Equal(HttpStatusCode.NotFound, (await otherUser.GetAsync($"/api/assistant/conversations/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherUser.DeleteAsync($"/api/assistant/conversations/{id}")).StatusCode);

        using var forbiddenAgent = await client.PostAsync(
            "/api/assistant/conversations",
            Json(new { agentId = "project-manager" }));
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenAgent.StatusCode);
        Assert.Equal("assistant-agent-forbidden", (await ReadJsonAsync(forbiddenAgent)).GetProperty("code").GetString());
        using var unknownAgent = await client.PostAsync("/api/assistant/conversations", Json(new { agentId = "unknown" }));
        Assert.Equal(HttpStatusCode.NotFound, unknownAgent.StatusCode);
        using var badPaging = await client.GetAsync("/api/assistant/conversations?page=0");
        Assert.Equal(HttpStatusCode.BadRequest, badPaging.StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/assistant/conversations/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/assistant/conversations/{id}")).StatusCode);
    }

    [Fact]
    public async Task Endpoints_require_authentication_and_the_access_policy()
    {
        await using var app = await AssistantWebApplication.StartAsync(database, new NhAiScriptedChatClient());
        using var anonymous = app.CreateClient(user: null, access: false);
        using var withoutAccess = app.CreateClient(access: false);

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/assistant/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await withoutAccess.GetAsync("/api/assistant/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await withoutAccess.GetAsync("/api/assistant/conversations")).StatusCode);
    }

    [Fact]
    public async Task Routing_and_the_application_wide_endpoints_share_the_endpoints_built_once()
    {
        await using var app = await AssistantWebApplication.StartAsync(database, new NhAiScriptedChatClient());

        // The authorization middleware's policy cache read the application-wide endpoints while the
        // pipeline was built; the routing middleware reads a composite of its own on the first request.
        var applicationWide = app.Services.GetRequiredService<EndpointDataSource>();
        using var routing = new CompositeEndpointDataSource(app.EndpointDataSources);

        Assert.Same(FindEndpoint(applicationWide, "NhAssistantGetStatus"), FindEndpoint(routing, "NhAssistantGetStatus"));
        Assert.Same(
            FindEndpoint(applicationWide, "NhAssistantFireWebhookTrigger"),
            FindEndpoint(routing, "NhAssistantFireWebhookTrigger"));
    }

    [Fact]
    public async Task A_disabled_assistant_reports_disabled_status_and_hides_every_other_endpoint()
    {
        await using var app = await AssistantWebApplication.StartAsync(database, new NhAiScriptedChatClient(), enabled: false);
        using var client = app.CreateClient();

        var status = await ReadJsonAsync(await client.GetAsync("/api/assistant/status"));
        Assert.False(status.GetProperty("enabled").GetBoolean());
        Assert.Empty(status.GetProperty("agents").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/assistant/agents")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/assistant/conversations")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.PostAsync("/api/assistant/conversations", Json(new { agentId = "project-assistant" }))).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.PostAsync($"/api/assistant/conversations/{Guid.NewGuid()}/cancel", null)).StatusCode);
    }

    [Fact]
    public async Task A_full_turn_streams_contract_events_and_approval_resumes_over_http()
    {
        var projectId = Guid.NewGuid();
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("projects_search_v1", new { input = new { query = "roadmap" } })
            .RespondWithFunctionCall("projects_change_status_v1", new { input = new { projectId, status = "Active" } })
            .RespondWithText("Done: the project is active.");
        await using var app = await AssistantWebApplication.StartAsync(database, model);
        using var client = app.CreateClient();
        var id = await CreateConversationAsync(client);

        using var turn = await client.PostAsync(
            $"/api/assistant/conversations/{id}/messages",
            Json(new { text = "Activate the roadmap project.", clientMessageId = "c-1" }));

        Assert.Equal(HttpStatusCode.OK, turn.StatusCode);
        Assert.Equal("text/event-stream", turn.Content.Headers.ContentType!.MediaType);
        var (events, _) = await ReadAllAsync(turn);
        Assert.Equal(
            ["turn.started", "tool.started", "tool.completed", "tool.started", "approval.required", "turn.completed"],
            events.Select(evt => evt.Name));
        AssertNames(events[0].Data, "turnId", "userMessageId", "assistantMessageId");
        AssertNames(events[1].Data, "invocationId", "toolId", "toolVersion", "displayName", "argumentsPreview");
        AssertNames(events[2].Data, "invocationId", "status", "resultCode", "resultPreview");
        var approval = events[4].Data;
        AssertNames(approval, "type", "approvalId", "proposalId", "proposalHash", "toolId", "summary", "argumentsPreview", "presentation", "targets", "expiresAt", "status");
        Assert.Equal("approval", approval.GetProperty("type").GetString());
        var paused = events[5].Data;
        AssertNames(paused, "turnId", "status", "usage", "errorCode");
        AssertNames(paused.GetProperty("usage"), "inputTokens", "outputTokens", "toolCalls");
        Assert.Equal("waiting-for-approval", paused.GetProperty("status").GetString());

        using var busy = await client.PostAsync(
            $"/api/assistant/conversations/{id}/messages",
            Json(new { text = "Are you there?", clientMessageId = "c-2" }));
        Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);

        var conversation = await ReadJsonAsync(await client.GetAsync($"/api/assistant/conversations/{id}"));
        Assert.Equal("waiting-for-approval", conversation.GetProperty("status").GetString());
        Assert.Equal("approval", conversation.GetProperty("pendingApproval").GetProperty("type").GetString());
        var approvalId = approval.GetProperty("approvalId").GetString();
        var hash = approval.GetProperty("proposalHash").GetString();

        using var mismatch = await client.PostAsync(
            $"/api/assistant/conversations/{id}/approvals/{approvalId}/decide",
            Json(new { decision = "approve", expectedProposalHash = new string('0', 64) }));
        Assert.Equal(HttpStatusCode.Conflict, mismatch.StatusCode);
        using var invalid = await client.PostAsync(
            $"/api/assistant/conversations/{id}/approvals/{approvalId}/decide",
            Json(new { decision = "maybe", expectedProposalHash = hash }));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        using var decided = await client.PostAsync(
            $"/api/assistant/conversations/{id}/approvals/{approvalId}/decide",
            Json(new { decision = "approve", expectedProposalHash = hash }));
        var (resumed, _) = await ReadAllAsync(decided);
        Assert.Equal("turn.started", resumed[0].Name);
        Assert.Contains(resumed, evt => evt.Name == "tool.completed" && evt.Data.GetProperty("status").GetString() == "succeeded");
        Assert.Equal(
            "Done: the project is active.",
            string.Concat(resumed.Where(evt => evt.Name == "message.delta").Select(evt => evt.Data.GetProperty("text").GetString())));
        AssertNames(resumed.First(evt => evt.Name == "message.delta").Data, "messageId", "text");
        Assert.Equal("completed", resumed[^1].Data.GetProperty("status").GetString());
        Assert.Single(app.Tools.StatusChanges);

        var final = await ReadJsonAsync(await client.GetAsync($"/api/assistant/conversations/{id}"));
        AssertNames(
            final,
            "id", "agentId", "title", "status", "createdAt", "updatedAt", "agentVersion", "messages", "pendingApproval",
            "role", "lastReadSequence", "lastMessageSequence", "activeActorId", "members", "shareToken", "currentActorId",
            "kind", "thread");
        Assert.Equal("chat", final.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, final.GetProperty("thread").ValueKind);
        var message = final.GetProperty("messages")[1];
        AssertNames(message, "id", "role", "createdAt", "parts", "sequence", "authorActorId", "authorKind");
        Assert.Equal("person", final.GetProperty("messages")[0].GetProperty("authorKind").GetString());
        Assert.Equal(JsonValueKind.Null, message.GetProperty("authorKind").ValueKind);
        var toolPart = message.GetProperty("parts").EnumerateArray().First(part => part.GetProperty("type").GetString() == "tool-call");
        AssertNames(toolPart, "type", "invocationId", "toolId", "toolVersion", "displayName", "status", "argumentsPreview", "resultPreview", "resultCode", "grantSource", "restricted");
        var textPart = final.GetProperty("messages")[0].GetProperty("parts")[0];
        AssertNames(textPart, "type", "text");
    }

    [Fact]
    public async Task Keep_alive_comments_flow_and_cancel_ends_the_stream()
    {
        var model = new BlockingChatClient();
        await using var app = await AssistantWebApplication.StartAsync(
            database,
            model,
            configure: services => services.AddSingleton(new NhAssistantServerSentEventsTiming
            {
                KeepAliveInterval = TimeSpan.FromMilliseconds(200)
            }));
        using var client = app.CreateClient();
        var id = await CreateConversationAsync(client);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/assistant/conversations/{id}/messages")
        {
            Content = Json(new { text = "Take your time.", clientMessageId = "slow" })
        };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        var keptAlive = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reading = ReadAllAsync(response, firstComment: keptAlive);
        await model.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        // The model blocks, so the stream stays silent until a keep-alive comment arrives.
        await keptAlive.Task.WaitAsync(TimeSpan.FromSeconds(30));
        using var cancel = await client.PostAsync($"/api/assistant/conversations/{id}/cancel", null);
        var (events, comments) = await reading.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode);
        Assert.Contains(": keep-alive", comments);
        Assert.Equal("cancelled", events[^1].Data.GetProperty("status").GetString());
        var conversation = await ReadJsonAsync(await client.GetAsync($"/api/assistant/conversations/{id}"));
        Assert.Equal("idle", conversation.GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_client_disconnect_cancels_the_turn_and_releases_the_conversation()
    {
        var model = new BlockingChatClient();
        await using var app = await AssistantWebApplication.StartAsync(database, model);
        using var client = app.CreateClient();
        var id = await CreateConversationAsync(client);
        using var abort = new CancellationTokenSource();

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/assistant/conversations/{id}/messages")
        {
            Content = Json(new { text = "Take your time.", clientMessageId = "abort" })
        };
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, abort.Token);
        await model.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        abort.Cancel();
        response.Dispose();

        var status = "running";
        for (var attempt = 0; attempt < 50 && status == "running"; attempt++)
        {
            await Task.Delay(100);
            status = (await ReadJsonAsync(await client.GetAsync($"/api/assistant/conversations/{id}")))
                .GetProperty("status").GetString()!;
        }
        Assert.Equal("idle", status);
    }

    [Fact]
    public async Task Client_context_is_passed_to_the_model_and_an_invalid_shape_is_ignored_without_400()
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithText("First.")
            .RespondWithText("Second.")
            .RespondWithText("Third.");
        await using var app = await AssistantWebApplication.StartAsync(database, model);
        using var client = app.CreateClient();
        var id = await CreateConversationAsync(client);

        using var valid = await client.PostAsync(
            $"/api/assistant/conversations/{id}/messages",
            Json(new
            {
                text = "What is open?",
                clientMessageId = "ctx-1",
                clientContext = new { route = "/projects/7", title = "Project 7", entities = new[] { new { type = "project", id = "7", label = "Roadmap" } } }
            }));
        await ReadAllAsync(valid);
        using var invalid = await client.PostAsync(
            $"/api/assistant/conversations/{id}/messages",
            Json(new { text = "And now?", clientMessageId = "ctx-2", clientContext = new { route = 42, entities = "none" } }));
        await ReadAllAsync(invalid);
        using var wrongType = await client.PostAsync(
            $"/api/assistant/conversations/{id}/messages",
            Json(new { text = "Still?", clientMessageId = "ctx-3", clientContext = "just text" }));
        await ReadAllAsync(wrongType);

        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        Assert.Equal(HttpStatusCode.OK, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.OK, wrongType.StatusCode);
        Assert.Contains("<page-data>\n- Route: /projects/7\n- Title: Project 7\n- Entity: project 7 (Roadmap)\n</page-data>", model.Requests[0].Options!.Instructions);
        Assert.DoesNotContain("<page-data>", model.Requests[1].Options!.Instructions);
        Assert.DoesNotContain("<page-data>", model.Requests[2].Options!.Instructions);
        Assert.Contains("<situation-data>", model.Requests[2].Options!.Instructions);
    }

    [Fact]
    public void The_json_contract_uses_the_exact_property_names()
    {
        var context = NhAssistantJsonSerializerContext.Default;
        var status = JsonSerializer.SerializeToElement(
            new NhAssistantStatusDto(true, [new NhAssistantAgentSummaryDto("a", 1, "n", "d", true)], new NhAssistantLimitsDto(1, 2), false),
            context.NhAssistantStatusDto);
        var summary = JsonSerializer.SerializeToElement(
            new NhAssistantConversationListDto(
                [new NhAssistantConversationSummaryDto(Guid.NewGuid(), "a", null, "idle", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)],
                1),
            context.NhAssistantConversationListDto);
        var error = JsonSerializer.SerializeToElement(new NhAssistantErrorDto("code", "key"), context.NhAssistantErrorDto);
        var validation = JsonSerializer.SerializeToElement(
            new NhAssistantErrorDto("code", "key", new Dictionary<string, string[]> { ["displayName"] = ["required"] }),
            context.NhAssistantErrorDto);

        AssertNames(status, "enabled", "agents", "limits", "canAdminister", "collaboration");
        AssertNames(status.GetProperty("agents")[0], "id", "version", "displayNameKey", "descriptionKey", "canMutate", "uploads");
        Assert.Equal(JsonValueKind.Null, status.GetProperty("agents")[0].GetProperty("uploads").ValueKind);
        var agents = JsonSerializer.SerializeToElement(
            new[]
            {
                new NhAssistantAgentSummaryDto("a", 1, "n", "d", true)
                {
                    Uploads = new NhAssistantAgentUploadsDto([NhAiMediaTypes.Pdf, "application/acad"], 1_024, 2)
                    {
                        MappedExtensions = [new NhAssistantUploadExtensionDto(".dwg", "application/acad")]
                    }
                }
            },
            context.NhAssistantAgentSummaryDtoArray);
        AssertNames(agents[0].GetProperty("uploads"), "acceptedMediaTypes", "maxFileBytes", "maxFiles", "mappedExtensions");
        AssertNames(agents[0].GetProperty("uploads").GetProperty("mappedExtensions")[0], "extension", "mediaType");
        // Without mappings the list is empty, never missing, so a client can read it as it is.
        var withoutMappings = JsonSerializer.SerializeToElement(new NhAssistantAgentUploadsDto([NhAiMediaTypes.Pdf], 1_024, 2), context.NhAssistantAgentUploadsDto);
        Assert.Equal(0, withoutMappings.GetProperty("mappedExtensions").GetArrayLength());
        AssertNames(status.GetProperty("limits"), "maxMessageChars", "maxToolCallsPerTurn");
        AssertNames(summary, "items", "total");
        AssertNames(
            summary.GetProperty("items")[0],
            "id", "agentId", "title", "status", "createdAt", "updatedAt",
            "role", "participantCount", "lastMessageSequence", "lastReadSequence", "activeActorId");
        AssertNames(error, "code", "messageKey");
        AssertNames(validation, "code", "messageKey", "errors");
        AssertNames(validation.GetProperty("errors"), "displayName");

        var clientContext = JsonSerializer.SerializeToElement(
            new NhAssistantClientContextDto("/projects/7", "Project 7", [new NhAssistantClientEntityDto("project", "7", "Roadmap")]),
            context.NhAssistantClientContextDto);
        var message = JsonSerializer.SerializeToElement(
            new NhAssistantSendMessageRequest("Hi", "c-1", clientContext),
            context.NhAssistantSendMessageRequest);
        AssertNames(clientContext, "route", "title", "entities");
        AssertNames(clientContext.GetProperty("entities")[0], "type", "id", "label");
        AssertNames(message, "text", "clientMessageId", "clientContext", "files");
        var read = JsonSerializer.Deserialize(
            """{ "text": "Hi", "clientMessageId": "c-1", "clientContext": { "route": "/x" }, "files": [{ "id": "f-1", "name": "Plan.pdf", "mediaType": "application/pdf", "size": 12, "hash": null }] }""",
            context.NhAssistantSendMessageRequest)!;
        Assert.Equal("/x", read.ClientContext!.Value.GetProperty("route").GetString());
        Assert.Equal(new NhAiFileReference("f-1", "Plan.pdf", NhAiMediaTypes.Pdf, 12), Assert.Single(read.Files!));

        // The upload answers the file's reference; a message shows an attachment as a `file` part.
        var reference = JsonSerializer.SerializeToElement(
            new NhAiFileReference("f-1", "Plan.pdf", NhAiMediaTypes.Pdf, 12, "sha256:abc"),
            context.NhAiFileReference);
        var file = JsonSerializer.SerializeToElement<NhAssistantMessagePartDto>(
            new NhAssistantFilePartDto("Plan.pdf", NhAiMediaTypes.Pdf, 12),
            context.NhAssistantMessagePartDto);
        AssertNames(reference, "id", "name", "mediaType", "size", "hash");
        AssertNames(file, "type", "name", "mediaType", "size");
        Assert.Equal("file", file.GetProperty("type").GetString());

        // A message the application posted carries its details, with whether the assistant follows it.
        var posted = JsonSerializer.SerializeToElement(
            new NhAssistantMessageDto(Guid.NewGuid(), NhAssistantMessageRoles.Application, DateTimeOffset.UtcNow, [new NhAssistantTextPartDto("Done.")], 3, null, NhAssistantMessageAuthorKinds.Application)
            {
                Application = new NhAssistantApplicationMessageDto("Export finished", "Exports", new NhAssistantApplicationMessageLinkDto("/exports/42", null), Trusted: true)
            },
            context.NhAssistantMessageDto);
        AssertNames(posted, "id", "role", "createdAt", "parts", "sequence", "authorActorId", "authorKind", "application");
        AssertNames(posted.GetProperty("application"), "title", "senderName", "link", "trusted");
        AssertNames(posted.GetProperty("application").GetProperty("link"), "url", "label");
        Assert.True(posted.GetProperty("application").GetProperty("trusted").GetBoolean());
    }

    private static async Task<string> CreateConversationAsync(HttpClient client)
    {
        using var created = await client.PostAsync(
            "/api/assistant/conversations",
            Json(new { agentId = "project-assistant" }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await ReadJsonAsync(created)).GetProperty("id").GetString()!;
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    [Fact]
    public void Agent_metadata_keeps_its_json_names()
    {
        var entry = new NewHeap.Platform.AI.Chat.AspNet.AgentRuns.NhAssistantAgentMetadataDto(
            "cost-center",
            Editable: true,
            MaxBytes: 200,
            Source: "override",
            Value: JsonSerializer.SerializeToElement(new { code = "CC-2000" }),
            Version: 2,
            AgentVersion: 1)
        {
            CodeValue = JsonSerializer.SerializeToElement(new { code = "CC-1000" }),
            Indexed = true,
            IndexValue = "cc-2000",
            IndexedByEach = false,
            IndexValues = [],
            ChangedAt = DateTimeOffset.UnixEpoch,
            ChangedBy = "admin-1"
        };
        var context = NewHeap.Platform.AI.Chat.AspNet.AgentRuns.NhAssistantAgentRunJsonSerializerContext.Default;

        var json = JsonSerializer.SerializeToElement(entry, context.NhAssistantAgentMetadataDto);
        var request = JsonSerializer.SerializeToElement(
            new NewHeap.Platform.AI.Chat.AspNet.AgentRuns.NhAssistantAgentMetadataRequest(json.GetProperty("value"), 2),
            context.NhAssistantAgentMetadataRequest);
        var catalogEntry = JsonSerializer.SerializeToElement(
            new NhAssistantAgentMetadataEntryDto("cost-center", true, 200, true),
            context.NhAssistantRunCatalogDto.Options);

        AssertNames(
            json,
            "name", "editable", "maxBytes", "source", "value", "version", "agentVersion", "codeValue", "indexed", "indexValue",
            "indexedByEach", "indexValues", "changedAt", "changedBy");
        AssertNames(request, "value", "expectedVersion");
        AssertNames(catalogEntry, "name", "editable", "maxBytes", "indexed", "indexedByEach");
        Assert.Equal("CC-2000", json.GetProperty("value").GetProperty("code").GetString());
    }

    [Fact]
    public void Shadow_variants_and_their_promotion_keep_their_json_names()
    {
        var variants = JsonSerializer.SerializeToElement(
            new NewHeap.Platform.AI.Chat.AspNet.AgentRuns.NhAssistantShadowVariantsDto(
                "project-planner",
                true,
                2,
                [new NewHeap.Platform.AI.Chat.AspNet.AgentRuns.NhAssistantShadowVariantDto("planner-trial", "Planner trial", "data", true, true, null) { Version = 3 }]),
            NewHeap.Platform.AI.Chat.AspNet.AgentRuns.NhAssistantHandOffsAsDataJsonSerializerContext.Default.NhAssistantShadowVariantsDto);
        var request = JsonSerializer.SerializeToElement(
            new NhAssistantShadowVariantPromotionInputDto(2, true, 3, "The trial did better"),
            NhAssistantJsonSerializerContext.Default.NhAssistantShadowVariantPromotionInputDto);
        var agent = new NhAssistantAdminAgentDto(
            "project-planner", "Planner", "Plans.", "Plan.", ["projects.*"], [], null, "execute", true, 3, "code", true, new string('a', 64), DateTimeOffset.UnixEpoch);
        var promoted = JsonSerializer.SerializeToElement(
            new NhAssistantShadowVariantPromotedDto(agent, "planner-trial", ["progress-review"]),
            NhAssistantJsonSerializerContext.Default.NhAssistantShadowVariantPromotedDto);

        AssertNames(variants, "agentId", "allowsDataVariants", "maxDataVariants", "variants");
        AssertNames(variants.GetProperty("variants")[0], "agentId", "displayName", "source", "hasRunSettings", "inEffect", "problem", "version", "packageId", "packageVersion");
        AssertNames(request, "expectedVersion", "moveSkills", "expectedVariantVersion", "reason", "asPackageVersion", "uncarriedParts");
        AssertNames(promoted, "agent", "variantId", "movedSkills");
        Assert.Equal(("project-planner", 3), (promoted.GetProperty("agent").GetProperty("id").GetString(), promoted.GetProperty("agent").GetProperty("version").GetInt32()));
    }

    private static Endpoint FindEndpoint(EndpointDataSource source, string name)
    {
        return source.Endpoints.Single(endpoint =>
            endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == name);
    }

    private static void AssertNames(JsonElement element, params string[] expected)
    {
        Assert.Equal(
            expected.Order(StringComparer.Ordinal),
            element.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
    }

    private sealed class BlockingChatClient : IChatClient
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return null;
        }

        public void Dispose()
        {
        }
    }
}
