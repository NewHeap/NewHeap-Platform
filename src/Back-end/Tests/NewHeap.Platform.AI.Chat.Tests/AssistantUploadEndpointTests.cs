using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using Xunit;
using static NewHeap.Platform.AI.Chat.Tests.Infrastructure.ServerSentEventReader;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// Chat attachments over HTTP: a person uploads a file to a conversation, the application's file
/// source stores it with the default classification, a message carries its reference to the model,
/// and the conversation shows the file by name, type and size, never its content. Uploads refuse a
/// file too large, a type not accepted, content of another type, an agent that accepts no files and a
/// caller without access, each with its own code and status.
/// </summary>
public sealed class AssistantUploadEndpointTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string FileAgent = "file-assistant";
    private const string ClosedAgent = "closed-agent";
    private const string UploadProfile = "upload-chat";
    private const string PlanText = "%PDF-1.7\nLaunch plan: the launch moves to May.\n%%EOF";

    private static readonly byte[] PlanPdf = Encoding.UTF8.GetBytes(PlanText);

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D];

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task An_upload_is_stored_confidential_and_reaches_the_model_while_the_conversation_shows_it_without_content(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient().RespondWithText("The plan moves the launch to May.");
        await using var app = await StartAsync(provider, model);
        using var client = app.CreateClient();
        var id = await CreateConversationAsync(client, FileAgent);

        using var uploaded = await UploadAsync(client, id, "Launch plan.pdf", NhAiMediaTypes.Pdf, PlanPdf);

        Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
        var reference = await ReadJsonAsync(uploaded);
        AssertNames(reference, "id", "name", "mediaType", "size", "hash");
        var stored = Assert.Single(app.Services.GetRequiredService<TestUploads>().Stored.Values);
        Assert.Equal(stored.Reference.Id, reference.GetProperty("id").GetString());
        // The upload gets the default classification and reaches the source for the person.
        Assert.Equal(
            ("Launch plan.pdf", NhAiMediaTypes.Pdf, (long)PlanPdf.Length, NhAiDataClassification.Confidential, Guid.Parse(id)),
            (stored.Upload.Name, stored.Upload.MediaType, stored.Upload.Length, stored.Upload.Classification, stored.Upload.ConversationId));
        Assert.Equal(PlanPdf, stored.Content);
        Assert.Equal(
            ("user-1", NhAiActorKind.Human, FileAgent, Guid.Parse(id), NhAssistantFileOrigins.Attachment),
            (stored.Actor.ActorId, stored.Actor.ActorKind, stored.Actor.AgentId, stored.Actor.ConversationId, stored.Actor.Origin));

        using var turn = await client.PostAsync(
            $"/api/assistant/conversations/{id}/messages",
            Json(new { text = "What does the plan say?", clientMessageId = "c-1", files = new[] { reference } }));

        Assert.Equal(HttpStatusCode.OK, turn.StatusCode);
        var (events, _) = await ReadAllAsync(turn);
        Assert.Equal("completed", events[^1].Data.GetProperty("status").GetString());
        // The model reads the attachment with the message, as file content.
        var message = model.Requests[0].Messages[^1];
        Assert.Equal(ChatRole.User, message.Role);
        Assert.Equal(PlanPdf, message.Contents.OfType<DataContent>().Single().Data.ToArray());

        using var read = await client.GetAsync($"/api/assistant/conversations/{id}");
        var json = await read.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        var user = document.RootElement.GetProperty("messages").EnumerateArray()
            .Single(item => item.GetProperty("role").GetString() == "user");
        var file = user.GetProperty("parts").EnumerateArray().Single(part => part.GetProperty("type").GetString() == "file");
        AssertNames(file, "type", "name", "mediaType", "size");
        Assert.Equal(
            ("Launch plan.pdf", NhAiMediaTypes.Pdf, (long)PlanPdf.Length),
            (file.GetProperty("name").GetString(), file.GetProperty("mediaType").GetString(), file.GetProperty("size").GetInt64()));
        // The view carries neither the content nor the application's id or hashes.
        Assert.DoesNotContain("Launch plan: the launch", json, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(PlanPdf), json, StringComparison.Ordinal);
        Assert.DoesNotContain(stored.Reference.Id, json, StringComparison.Ordinal);
        Assert.DoesNotContain("sha256:", json, StringComparison.Ordinal);
        Assert.DoesNotContain("app-hash", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Uploads_refuse_a_file_too_large_a_type_not_accepted_content_of_another_type_and_a_malformed_request()
    {
        await using var app = await StartAsync(limits: limits => limits.MaxFileBytes = 2_048);
        using var client = app.CreateClient();
        var id = await CreateConversationAsync(client, FileAgent);
        var large = Encoding.UTF8.GetBytes("%PDF-1.7\n" + new string('x', 2_048));
        var huge = Encoding.UTF8.GetBytes("%PDF-1.7\n" + new string('x', 64 * 1024));

        await AssertRefusedAsync(
            await UploadAsync(client, id, "Large.pdf", NhAiMediaTypes.Pdf, large),
            HttpStatusCode.RequestEntityTooLarge,
            NhAssistantErrorCodes.FileTooLarge);
        await AssertRefusedAsync(
            await UploadAsync(client, id, "Huge.pdf", NhAiMediaTypes.Pdf, huge),
            HttpStatusCode.RequestEntityTooLarge,
            NhAssistantErrorCodes.FileTooLarge);
        await AssertRefusedAsync(
            await UploadAsync(client, id, "Notes.txt", "text/plain", Encoding.UTF8.GetBytes("Notes")),
            HttpStatusCode.UnsupportedMediaType,
            NhAssistantErrorCodes.FileTypeNotAccepted);
        // A file that declares PDF but is not one.
        await AssertRefusedAsync(
            await UploadAsync(client, id, "Script.pdf", NhAiMediaTypes.Pdf, Encoding.UTF8.GetBytes("<script>alert(1)</script>")),
            HttpStatusCode.UnsupportedMediaType,
            NhAssistantErrorCodes.FileTypeNotAccepted);
        await AssertRefusedAsync(
            await UploadAsync(client, id, "Empty.pdf", NhAiMediaTypes.Pdf, []),
            HttpStatusCode.BadRequest,
            NhAssistantErrorCodes.FileUploadInvalid);
        await AssertRefusedAsync(
            await UploadAsync(client, id, "Plan.pdf", NhAiMediaTypes.Pdf, PlanPdf, header: false),
            HttpStatusCode.BadRequest,
            NhAssistantErrorCodes.FileUploadInvalid);
        await AssertRefusedAsync(
            await client.PostAsync($"/api/assistant/conversations/{id}/files", Json(new { name = "Plan.pdf" })),
            HttpStatusCode.BadRequest,
            NhAssistantErrorCodes.FileUploadInvalid);
        await AssertRefusedAsync(
            await UploadAsync(client, id, "Plan.pdf", NhAiMediaTypes.Pdf, PlanPdf, second: ("Scan.png", NhAiMediaTypes.Png, Png)),
            HttpStatusCode.BadRequest,
            NhAssistantErrorCodes.FileUploadInvalid);

        Assert.Empty(app.Services.GetRequiredService<TestUploads>().Stored);
    }

    [Fact]
    public async Task An_agent_that_accepts_no_files_refuses_uploads_and_the_agents_say_what_people_may_attach()
    {
        await using var app = await StartAsync(limits: limits =>
        {
            limits.MaxFileBytes = 4_096;
            limits.MaxFilesPerTurn = 3;
        });
        using var client = app.CreateClient();
        var id = await CreateConversationAsync(client, ClosedAgent);

        await AssertRefusedAsync(
            await UploadAsync(client, id, "Plan.pdf", NhAiMediaTypes.Pdf, PlanPdf),
            HttpStatusCode.Conflict,
            NhAssistantErrorCodes.FilesNotAccepted);

        Assert.Empty(app.Services.GetRequiredService<TestUploads>().Stored);
        var agents = (await ReadJsonAsync(await client.GetAsync("/api/assistant/status"))).GetProperty("agents")
            .EnumerateArray()
            .ToDictionary(agent => agent.GetProperty("id").GetString()!, agent => agent.GetProperty("uploads"));
        // A configured agent accepts no files until it is switched on; the chat assistants do.
        Assert.Equal(JsonValueKind.Null, agents[ClosedAgent].ValueKind);
        var uploads = agents[FileAgent];
        Assert.Equal(
            [NhAiMediaTypes.Pdf, NhAiMediaTypes.Png, NhAiMediaTypes.Jpeg, NhAiMediaTypes.Docx, NhAiMediaTypes.Xlsx],
            MediaTypesOf(uploads));
        Assert.Equal((4_096, 3), (uploads.GetProperty("maxFileBytes").GetInt32(), uploads.GetProperty("maxFiles").GetInt32()));
        Assert.Equal(JsonValueKind.Object, agents["project-assistant"].ValueKind);
        var listed = (await ReadJsonAsync(await client.GetAsync("/api/assistant/agents"))).EnumerateArray()
            .Single(agent => agent.GetProperty("id").GetString() == FileAgent);
        Assert.Equal(3, listed.GetProperty("uploads").GetProperty("maxFiles").GetInt32());
    }

    [Fact]
    public async Task Uploads_need_the_access_policy_the_conversation_and_a_file_source()
    {
        await using (var app = await StartAsync())
        {
            using var client = app.CreateClient();
            var id = await CreateConversationAsync(client, FileAgent);
            using var anonymous = app.CreateClient(user: null, access: false);
            using var withoutAccess = app.CreateClient(access: false);
            using var otherUser = app.CreateClient("user-2");

            Assert.Equal(HttpStatusCode.Unauthorized, (await UploadAsync(anonymous, id, "Plan.pdf", NhAiMediaTypes.Pdf, PlanPdf)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await UploadAsync(withoutAccess, id, "Plan.pdf", NhAiMediaTypes.Pdf, PlanPdf)).StatusCode);
            await AssertRefusedAsync(
                await UploadAsync(otherUser, id, "Plan.pdf", NhAiMediaTypes.Pdf, PlanPdf),
                HttpStatusCode.NotFound,
                NhAssistantErrorCodes.ConversationNotFound);
            await AssertRefusedAsync(
                await UploadAsync(client, Guid.NewGuid().ToString(), "Plan.pdf", NhAiMediaTypes.Pdf, PlanPdf),
                HttpStatusCode.NotFound,
                NhAssistantErrorCodes.ConversationNotFound);
            Assert.Empty(app.Services.GetRequiredService<TestUploads>().Stored);
        }

        // Without a file source nobody uploads, and no agent offers attachments.
        await using (var app = await StartAsync(fileSource: false))
        {
            using var client = app.CreateClient();
            var id = await CreateConversationAsync(client, FileAgent);

            await AssertRefusedAsync(
                await UploadAsync(client, id, "Plan.pdf", NhAiMediaTypes.Pdf, PlanPdf),
                HttpStatusCode.NotFound,
                NhAssistantErrorCodes.FilesUnavailable);
            var agents = (await ReadJsonAsync(await client.GetAsync("/api/assistant/status"))).GetProperty("agents").EnumerateArray();
            Assert.All(agents, agent => Assert.Equal(JsonValueKind.Null, agent.GetProperty("uploads").ValueKind));
        }
    }

    [Fact]
    public async Task The_application_chooses_the_types_and_classification_and_its_source_refusals_keep_stable_codes()
    {
        await using var app = await StartAsync(configureUploads: uploads =>
        {
            uploads.DefaultClassification = NhAiDataClassification.Internal;
            uploads.AcceptMediaTypes("Image/PNG");
        });
        using var client = app.CreateClient();
        var id = await CreateConversationAsync(client, FileAgent);
        var store = app.Services.GetRequiredService<TestUploads>();

        using var scan = await UploadAsync(client, id, @"C:\Users\someone\Scan.png", NhAiMediaTypes.Png, Png);
        await AssertRefusedAsync(
            await UploadAsync(client, id, "Plan.pdf", NhAiMediaTypes.Pdf, PlanPdf),
            HttpStatusCode.UnsupportedMediaType,
            NhAssistantErrorCodes.FileTypeNotAccepted);
        store.RefuseWith = "application-virus-found";
        await AssertRefusedAsync(
            await UploadAsync(client, id, "Scan.png", NhAiMediaTypes.Png, Png),
            HttpStatusCode.UnprocessableEntity,
            NhAssistantErrorCodes.FileUploadRefused);
        store.RefuseWith = NhAssistantErrorCodes.FileTooLarge;
        await AssertRefusedAsync(
            await UploadAsync(client, id, "Scan.png", NhAiMediaTypes.Png, Png),
            HttpStatusCode.RequestEntityTooLarge,
            NhAssistantErrorCodes.FileTooLarge);
        store.RefuseWith = null;
        store.Fail = true;
        using var failed = await UploadAsync(client, id, "Scan.png", NhAiMediaTypes.Png, Png);
        var failure = await failed.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, scan.StatusCode);
        var stored = Assert.Single(store.Stored.Values);
        // The name loses the device's path; the upload gets the application's classification.
        Assert.Equal(("Scan.png", NhAiDataClassification.Internal), (stored.Upload.Name, stored.Upload.Classification));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Contains(NhAssistantErrorCodes.FileUploadFailed, failure, StringComparison.Ordinal);
        // The source's message, which names the file, never reaches the response.
        Assert.DoesNotContain("disk is full", failure, StringComparison.Ordinal);
        var agent = (await ReadJsonAsync(await client.GetAsync("/api/assistant/agents"))).EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == FileAgent);
        Assert.Equal([NhAiMediaTypes.Png], MediaTypesOf(agent.GetProperty("uploads")));
    }

    private static string[] MediaTypesOf(JsonElement uploads)
    {
        return uploads.GetProperty("acceptedMediaTypes").EnumerateArray().Select(item => item.GetString()!).ToArray();
    }

    private Task<AssistantWebApplication> StartAsync(
        AssistantTestProvider provider = AssistantTestProvider.PostgreSql,
        NhAiScriptedChatClient? model = null,
        Action<NhAssistantLimits>? limits = null,
        Action<NhAssistantUploadOptions>? configureUploads = null,
        bool fileSource = true)
    {
        return AssistantWebApplication.StartAsync(
            database,
            new NhAiScriptedChatClient(),
            limits: limits,
            configure: services =>
            {
                services.AddSingleton<TestUploads>();
                services.AddKeyedSingleton<IChatClient>("upload-model", model ?? new NhAiScriptedChatClient());
                // The configured agent never runs here; it only stands for an agent that takes no files.
                services.Remove(services.Single(descriptor => descriptor.ImplementationType == typeof(NhAssistantStartupValidator)));
                services.AddNewHeapPlatformAI(ai => ai.AddChatProfile(UploadProfile, profile => profile
                    .UseKeyedClient("upload-model")
                    .RequireCapabilities(NhAiModelCapability.FunctionCalling)
                    .WithStreaming(NhAiStreamingPolicy.Allowed)
                    .PermitDataClassifications(NhAiDataClassification.Internal, NhAiDataClassification.Confidential)
                    .PermitExecutionRegions("local")
                    .AcceptMediaTypes(NhAiMediaTypes.Pdf, NhAiMediaTypes.Png)
                    .WithBudget(maxInputTokens: 32_000, maxOutputTokens: 1_024, maxCalls: 16)));
            },
            assistantBuilder: assistant =>
            {
                assistant
                    .AddAgent(AssistantTestData.Agent(FileAgent) with { ProfileName = UploadProfile })
                    .AddAgent(AssistantTestData.Agent(ClosedAgent) with { ProfileName = UploadProfile })
                    .ConfigureAgent(ClosedAgent, _ => { });
                if (fileSource)
                {
                    assistant.UseFileSource<TestUploadFileSource>();
                }
                if (configureUploads is not null)
                {
                    assistant.ConfigureUploads(configureUploads);
                }
            },
            provider: provider);
    }

    private static async Task<HttpResponseMessage> UploadAsync(
        HttpClient client,
        string conversationId,
        string name,
        string mediaType,
        byte[] content,
        bool header = true,
        (string Name, string MediaType, byte[] Content)? second = null)
    {
        var form = new MultipartFormDataContent();
        form.Add(FilePart(mediaType, content), NhAssistantUploadEndpoints.FileField, name);
        if (second is { } other)
        {
            form.Add(FilePart(other.MediaType, other.Content), "other", other.Name);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/assistant/conversations/{conversationId}/files")
        {
            Content = form
        };
        if (header)
        {
            request.Headers.Add(NhAssistantUploadEndpoints.UploadHeader, "1");
        }
        return await client.SendAsync(request);
    }

    private static ByteArrayContent FilePart(string mediaType, byte[] content)
    {
        var part = new ByteArrayContent(content);
        part.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return part;
    }

    private static async Task AssertRefusedAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        using (response)
        {
            Assert.Equal(status, response.StatusCode);
            Assert.Equal(code, (await ReadJsonAsync(response)).GetProperty("code").GetString());
        }
    }

    private static async Task<string> CreateConversationAsync(HttpClient client, string agentId)
    {
        using var created = await client.PostAsync("/api/assistant/conversations", Json(new { agentId }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await ReadJsonAsync(created)).GetProperty("id").GetString()!;
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static void AssertNames(JsonElement element, params string[] expected)
    {
        Assert.Equal(
            expected.Order(StringComparer.Ordinal),
            element.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
    }
}

/// <summary>
/// The checks of an upload that need no host: the name without the device's path, and the content
/// that must start as files of the declared type do.
/// </summary>
public sealed class AssistantUploadRuleTests
{
    [Theory]
    [InlineData("Plan.pdf", "Plan.pdf")]
    [InlineData(@"C:\Users\someone\Plan.pdf", "Plan.pdf")]
    [InlineData("folder/Plan.pdf", "Plan.pdf")]
    [InlineData("  Plan.pdf  ", "Plan.pdf")]
    [InlineData("folder/", null)]
    [InlineData("Plan\u0007.pdf", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void An_upload_keeps_its_name_without_the_path(string? fileName, string? expected)
    {
        Assert.Equal(expected, Runtime.NhAssistantUploads.NameOf(fileName));
    }

    [Fact]
    public void An_upload_name_longer_than_a_reference_allows_is_refused()
    {
        Assert.Null(Runtime.NhAssistantUploads.NameOf(new string('a', NhAiFileReference.MaxNameLength + 1)));
        Assert.NotNull(Runtime.NhAssistantUploads.NameOf(new string('a', NhAiFileReference.MaxNameLength)));
    }

    [Theory]
    [InlineData(NhAiMediaTypes.Pdf, "%PDF-1.7", true)]
    [InlineData(NhAiMediaTypes.Pdf, "\n\n%PDF-1.4", true)]
    [InlineData(NhAiMediaTypes.Pdf, "<html>", false)]
    [InlineData(NhAiMediaTypes.Docx, "PK\u0003\u0004word/", true)]
    [InlineData(NhAiMediaTypes.Xlsx, "%PDF-1.7", false)]
    [InlineData("text/csv", "anything", true)]
    public void The_content_must_start_as_files_of_its_declared_type_do(string mediaType, string head, bool matches)
    {
        Assert.Equal(matches, Runtime.NhAssistantUploads.MatchesSignature(Encoding.Latin1.GetBytes(head), mediaType));
    }

    [Fact]
    public void Images_are_recognized_by_their_signature()
    {
        Assert.True(Runtime.NhAssistantUploads.MatchesSignature([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], NhAiMediaTypes.Png));
        Assert.True(Runtime.NhAssistantUploads.MatchesSignature([0xFF, 0xD8, 0xFF, 0xE0], NhAiMediaTypes.Jpeg));
        Assert.False(Runtime.NhAssistantUploads.MatchesSignature([0xFF, 0xD8, 0xFF, 0xE0], NhAiMediaTypes.Png));
    }

    [Fact]
    public void The_application_replaces_the_accepted_types_with_valid_lowercase_ones()
    {
        var options = new NhAssistantUploadOptions();
        Assert.Equal(NhAiDataClassification.Confidential, options.DefaultClassification);
        Assert.Equal(5, options.AcceptedMediaTypes.Count);

        options.AcceptMediaTypes("Application/PDF; charset=binary", NhAiMediaTypes.Pdf, "text/csv");

        Assert.Equal([NhAiMediaTypes.Pdf, "text/csv"], options.AcceptedMediaTypes);
        Assert.Throws<ArgumentException>(() => options.AcceptMediaTypes("not a type"));
        Assert.Empty(options.AcceptMediaTypes().AcceptedMediaTypes);
    }
}
