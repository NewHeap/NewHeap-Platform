using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Media.Modules;
using NewHeap.Platform.AI;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Test;
using SampleProjectManagement.Api.Composition;
using SampleProjectManagement.Api.Controllers;
using SampleProjectManagement.Api.Models;
using SampleProjectManagement.Api.Services;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// SPM-358: the messaging app of SPM-332 lists the person's chats and agents and attaches a file to the
/// linked chat through the platform's <c>INhAssistantConversations</c> (<c>ListAsync</c>,
/// <c>ListAgentsAsync</c>, <c>UploadAsync</c>), with the checks and codes of the portal's endpoints; the
/// person's next message carries the file to the model and the file's name never reaches a log.
/// SPM-359: an archived chat takes its uploads along through the sample's
/// <see cref="ProjectMediaUploadRetention"/>, and the quick answers agent, whose profile may receive
/// internal data only, offers no attachments. Program.cs's composition with the media library.
/// </summary>
public sealed class AssistantChannelFileSamplesTests(AssistantChannelFileSampleHost host)
    : IClassFixture<AssistantChannelFileSampleHost>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly byte[] ProjectPlanPdf = Encoding.UTF8.GetBytes(
        "%PDF-1.4\n% Project plan: the launch moves to May.\n1 0 obj << /Type /Catalog >> endobj\n%%EOF");

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D];

    [Fact]
    public async Task SPM_358_the_messaging_app_lists_the_persons_chats_and_agents_and_attaches_a_file_to_the_linked_chat()
    {
        host.Model.Use(new SampleAssistantChatClient());
        var personId = await host.CreatePersonAsync();
        using var person = host.CreateClient(personId.ToString());
        using var outsider = host.CreateClient(host.OutsiderId.ToString());
        using var messaging = host.CreateAnonymousClient();
        var first = await CreateConversationAsync(person, SampleAssistantComposition.AgentId, "Launch plan");
        var second = await CreateConversationAsync(person, SampleStatusAdvisorComposition.AgentId, "Status");
        var token = await LinkAsync(person, first);

        // The person's chats, newest first, each with a link token of its own.
        var chats = await ReadAsync<AssistantChannelChatListViewModel>(await PostAsync(messaging, "chat-list", new { token }));
        Assert.Equal(2, chats.Total);
        Assert.Equal(["Status", "Launch plan"], chats.Items.Select(chat => chat.Title).ToArray());
        var statusChat = await ReadAsync<AssistantChannelChatStateViewModel>(await PostAsync(messaging, "chat", new { token = chats.Items[0].Token }));
        Assert.Equal("Status", statusChat.Title);
        using (var invalidPage = await PostAsync(messaging, "chat-list", new { token, page = 0 }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, invalidPage.StatusCode);
        }

        // The agents the person may chat with, and what they may attach to each, as the panel shows them.
        var agents = (await ReadAsync<AssistantChannelAgentViewModel[]>(await PostAsync(messaging, "agents", new { token })))
            .ToDictionary(agent => agent.AgentId);
        var assistant = agents[SampleAssistantComposition.AgentId];
        Assert.True(assistant.AcceptsFiles);
        Assert.Equal((20 * 1024 * 1024, true), (assistant.MaxFileBytes, assistant.AcceptedMediaTypes.Contains(NhAiMediaTypes.Pdf)));
        Assert.False(agents[SampleStatusAdvisorComposition.AgentId].AcceptsFiles);

        // The messaging app uploads the attachment, and the person's next message carries it.
        var plan = await UploadAsync(messaging, token, "Project plan.pdf", NhAiMediaTypes.Pdf, ProjectPlanPdf);
        Assert.Equal(("Project plan.pdf", NhAiMediaTypes.Pdf, (long)ProjectPlanPdf.Length), (plan.Name, plan.MediaType, plan.Size));
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var stored = await scope.ServiceProvider.GetRequiredService<IFileStructureStorage>().GetByIdAsync(Guid.Parse(plan.Id));
            Assert.Equal(ProjectMediaFolders.UploadsOf(first), stored!.Folder.FullPath);
        }
        var answer = await AnswerOfAsync(await PostAsync(messaging, "messages", new { deliveryId = "delivery-358", token, text = "What does the plan say?", files = new[] { plan } }));
        Assert.Equal("completed", answer.Status);
        Assert.Contains($"\"Project plan.pdf\" reached me as a application/pdf file of {ProjectPlanPdf.Length} bytes", answer.Text, StringComparison.Ordinal);

        // The refusals of the portal's upload endpoint: a forged token, content of another type, a file
        // without content, an agent that takes no files and someone else's chat.
        await AssertRefusedAsync(await PostUploadAsync(messaging, "not-a-token", "Plan.pdf", NhAiMediaTypes.Pdf, ProjectPlanPdf), HttpStatusCode.BadRequest, SampleAssistantMessagingChannel.DeliveryInvalidCode);
        await AssertRefusedAsync(await PostUploadAsync(messaging, token, "Fake.pdf", NhAiMediaTypes.Pdf, Png), HttpStatusCode.UnsupportedMediaType, NhAssistantErrorCodes.FileTypeNotAccepted);
        await AssertRefusedAsync(await PostUploadAsync(messaging, token, "Empty.pdf", NhAiMediaTypes.Pdf, []), HttpStatusCode.BadRequest, NhAssistantErrorCodes.FileUploadInvalid);
        await AssertRefusedAsync(await PostUploadAsync(messaging, chats.Items[0].Token, "Plan.pdf", NhAiMediaTypes.Pdf, ProjectPlanPdf), HttpStatusCode.Conflict, NhAssistantErrorCodes.FilesNotAccepted);
        var foreign = await LinkAsync(outsider, second);
        await AssertRefusedAsync(await PostUploadAsync(messaging, foreign, "Plan.pdf", NhAiMediaTypes.Pdf, ProjectPlanPdf), HttpStatusCode.NotFound, NhAssistantErrorCodes.ConversationNotFound);
        // The outsider's own list holds none of the person's chats.
        var outsiderChats = await ReadAsync<AssistantChannelChatListViewModel>(await PostAsync(messaging, "chat-list", new { token = foreign }));
        Assert.DoesNotContain(outsiderChats.Items, chat => chat.Title is "Launch plan" or "Status");

        // The upload's name never reached a log or the platform audit.
        host.AssertNotLogged("Project plan.pdf", "Fake.pdf", "Empty.pdf");
    }

    [Fact]
    public async Task SPM_359_an_archived_chat_takes_its_uploads_along_and_the_quick_answers_agent_offers_none()
    {
        host.Model.Use(new SampleAssistantChatClient());
        using var owner = host.CreateClient(host.OwnerId.ToString());
        using var messaging = host.CreateAnonymousClient();
        var archived = await CreateConversationAsync(owner, SampleAssistantComposition.AgentId, "Archive with files");
        var kept = await CreateConversationAsync(owner, SampleAssistantComposition.AgentId, "Kept with files");
        var gone = await UploadAsync(messaging, await LinkAsync(owner, archived), "Minutes.pdf", NhAiMediaTypes.Pdf, ProjectPlanPdf);
        var stays = await UploadAsync(messaging, await LinkAsync(owner, kept), "Charter.pdf", NhAiMediaTypes.Pdf, ProjectPlanPdf);

        // The owner archives the chat in the portal; its uploads go with it, those of another chat stay.
        using (var deleted = await owner.DeleteAsync($"/api/assistant/conversations/{archived}", TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var files = scope.ServiceProvider.GetRequiredService<IFileStructureStorage>();
            Assert.Null(await files.GetByIdAsync(Guid.Parse(gone.Id)));
            Assert.NotNull(await files.GetByIdAsync(Guid.Parse(stays.Id)));
            // Forgetting the archived chat again finds nothing and succeeds.
            var retention = scope.ServiceProvider.GetRequiredService<INhAssistantFileRetention>();
            Assert.IsType<ProjectMediaUploadRetention>(retention);
            var again = await retention.ForgetConversationAsync(
                new NhAssistantForgottenConversation(archived, SampleAssistantComposition.AgentId, NhAssistantFileRetentionReasons.Archived),
                TestContext.Current.CancellationToken);
            Assert.True(again.Success);
            Assert.NotNull(await files.GetByIdAsync(Guid.Parse(stays.Id)));
        }

        // The quick answers agent runs on a profile for internal data only, while uploads are
        // confidential: the portal and the messaging app offer it no attachments, and both refuse one.
        var portalAgents = (await owner.GetFromJsonAsync<JsonElement>("/api/assistant/agents", Json, TestContext.Current.CancellationToken))
            .EnumerateArray()
            .ToDictionary(agent => agent.GetProperty("id").GetString()!, agent => agent.GetProperty("uploads").ValueKind);
        Assert.Equal(JsonValueKind.Null, portalAgents[SampleQuickAnswersComposition.AgentId]);
        Assert.Equal(JsonValueKind.Object, portalAgents[SampleAssistantComposition.AgentId]);
        var quick = await CreateConversationAsync(owner, SampleQuickAnswersComposition.AgentId, "Quick question");
        var quickToken = await LinkAsync(owner, quick);
        var channelAgents = await ReadAsync<AssistantChannelAgentViewModel[]>(await PostAsync(messaging, "agents", new { token = quickToken }));
        Assert.False(channelAgents.Single(agent => agent.AgentId == SampleQuickAnswersComposition.AgentId).AcceptsFiles);
        await AssertRefusedAsync(await PostUploadAsync(messaging, quickToken, "Plan.pdf", NhAiMediaTypes.Pdf, ProjectPlanPdf), HttpStatusCode.Conflict, NhAssistantErrorCodes.FilesNotAccepted);
        using var portalUpload = await PostPortalUploadAsync(owner, quick, "Plan.pdf", NhAiMediaTypes.Pdf, ProjectPlanPdf);
        await AssertRefusedAsync(portalUpload, HttpStatusCode.Conflict, NhAssistantErrorCodes.FilesNotAccepted);
        // The agent itself still answers in the chat, with internal data.
        var answered = await host.SendAsync(owner, quick, "Which projects are active?");
        Assert.Equal("completed", answered[^1].Data.GetProperty("status").GetString());
    }

    private static async Task<Guid> CreateConversationAsync(HttpClient client, string agentId, string title)
    {
        using var response = await client.PostAsync(
            "/api/assistant/conversations",
            AssistantSampleHost.Json(new { agentId, title }),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await AssistantSampleHost.ReadJsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task<string> LinkAsync(HttpClient client, Guid conversationId)
    {
        using var response = await client.PostAsync(
            "/assistant-channel/links",
            AssistantSampleHost.Json(new { conversationId }),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AssistantChannelLinkViewModel>(Json, TestContext.Current.CancellationToken))!.Token;
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string path, object body)
    {
        return client.PostAsync("/assistant-channel/" + path, AssistantSampleHost.Json(body), TestContext.Current.CancellationToken);
    }

    private static async Task<AssistantChannelFileViewModel> UploadAsync(HttpClient client, string token, string name, string mediaType, byte[] content)
    {
        return await ReadAsync<AssistantChannelFileViewModel>(await PostUploadAsync(client, token, name, mediaType, content));
    }

    /// <summary>
    /// An attachment as the messaging provider delivers it: the link token and one file part.
    /// </summary>
    private static Task<HttpResponseMessage> PostUploadAsync(HttpClient client, string token, string name, string mediaType, byte[] content)
    {
        var part = new ByteArrayContent(content);
        part.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        var form = new MultipartFormDataContent
        {
            { new StringContent(token), "token" },
            { part, "file", name }
        };
        return client.PostAsync("/assistant-channel/files", form, TestContext.Current.CancellationToken);
    }

    private static Task<HttpResponseMessage> PostPortalUploadAsync(HttpClient client, Guid conversationId, string name, string mediaType, byte[] content)
    {
        var part = new ByteArrayContent(content);
        part.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/assistant/conversations/{conversationId}/files")
        {
            Content = new MultipartFormDataContent { { part, "file", name } }
        };
        request.Headers.Add("X-NH-Assistant-Upload", "1");
        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task AssertRefusedAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        using (response)
        {
            Assert.Equal(status, response.StatusCode);
            Assert.Equal(code, (await AssistantSampleHost.ReadJsonAsync(response)).GetProperty("code").GetString());
        }
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        using (response)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<T>(Json, TestContext.Current.CancellationToken))!;
        }
    }

    private static async Task<AssistantChannelAnswerViewModel> AnswerOfAsync(HttpResponseMessage response)
    {
        return await ReadAsync<AssistantChannelAnswerViewModel>(response);
    }
}

/// <summary>
/// SPM-358 and SPM-359: the file host of Program.cs with the messaging channel, its controller and the
/// quick answers agent.
/// </summary>
public sealed class AssistantChannelFileSampleHost(SamplePostgreSqlServer postgreSql) : AssistantFileSampleHost(postgreSql)
{
    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);
        services.AddControllers().ConfigureApplicationPartManager(manager =>
            manager.FeatureProviders.Add(new ChannelController()));
    }

    protected override void ConfigureAssistant(IServiceCollection services)
    {
        base.ConfigureAssistant(services);
        services.AddSampleQuickAnswers();
        services.AddScoped<SampleAssistantMessagingChannel>();
    }

    private sealed class ChannelController : ControllerFeatureProvider
    {
        protected override bool IsController(System.Reflection.TypeInfo typeInfo)
        {
            return typeInfo.AsType() == typeof(AssistantChannelController);
        }
    }
}
