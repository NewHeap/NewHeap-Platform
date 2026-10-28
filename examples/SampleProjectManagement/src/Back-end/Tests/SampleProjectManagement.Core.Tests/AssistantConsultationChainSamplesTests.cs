using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Mcp;
using NewHeap.Platform.AI.Test;
using SampleProjectManagement.Api.Composition;
using SampleProjectManagement.DAL.Entities;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// Executable evidence for SPM-366: a reviewed partner application consults the intake reviewer over
/// MCP and the API continues the partner's consultation chain, so a loop between the two applications
/// is refused before the reviewer runs, while the same chain from any other MCP caller is ignored.
/// </summary>
public sealed class AssistantConsultationChainSamplesTests(ConsultationChainSampleHost host)
    : IClassFixture<ConsultationChainSampleHost>
{
    private const string ConsultTool = "consult_sample-project-intake-reviewer_project_v1";
    private const string PartnerDesk = "partner-desk";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task SPM_366_a_reviewed_partner_continues_its_consultation_chain_and_a_loop_is_refused_before_the_reviewer_runs()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var project = await host.CreateProjectAsync("CHAIN", ownerId: host.OwnerId);
        await host.AddMemberAsync(project, host.ParticipantId, ProjectMemberRole.Participant);
        var reviewer = new NhAiScriptedChatClient()
            .RespondWithText("The owner is set; the review board has not answered yet.")
            .RespondWithText("The review board has not answered yet.");
        host.Model.Use(new RoutingChatClient(
            new SampleAssistantChatClient(),
            ((messages, options) => RoutingChatClient.Mentions(messages, options, "You are being consulted"), reviewer)));
        try
        {
            // The partner's assistant consults the reviewer for a project participant, with its own
            // desk agent as the one that asks.
            await using var partner = await ConnectAsync(ConsultationChainSampleHost.PartnerIssuer, cancellationToken);
            var consult = Assert.Single(await partner.ListToolsAsync(cancellationToken: cancellationToken), tool => tool.Name == ConsultTool);
            var answered = await consult.CallAsync(
                Question(project),
                options: new RequestOptions { Meta = Context(Chain(0, (ConsultationChainSampleHost.Partner, PartnerDesk))) },
                cancellationToken: cancellationToken);
            Assert.NotEqual(true, answered.IsError);

            // The reviewer is consulted by the partner's desk again, which this time was consulted by
            // the reviewer itself: the loop is refused before the reviewer runs.
            var loop = Context(Chain(
                1,
                (ConsultationChainSampleHost.Application, SampleIntakeReviewerComposition.AgentId),
                (ConsultationChainSampleHost.Partner, PartnerDesk)));
            var refused = await consult.CallAsync(Question(project), options: new RequestOptions { Meta = loop }, cancellationToken: cancellationToken);
            Assert.True(refused.IsError);
            Assert.Equal(NhAssistantErrorCodes.ConsultDepthExceeded, refused.Meta![NhAiMcpResultMetadata.CodeKey]!.GetValue<string>());
            Assert.Single(reviewer.Requests);

            // The same chain from an MCP client of another issuer is ignored, not refused: it consults
            // as any MCP client does.
            await using var stranger = await ConnectAsync("https://elsewhere.sample.test", cancellationToken);
            var direct = await Assert.Single(await stranger.ListToolsAsync(cancellationToken: cancellationToken), tool => tool.Name == ConsultTool)
                .CallAsync(Question(project), options: new RequestOptions { Meta = loop }, cancellationToken: cancellationToken);
            Assert.NotEqual(true, direct.IsError);
            Assert.Equal(2, reviewer.Requests.Count);

            // The reviewer's thread lists both consultations one level deep, for the participant; an
            // MCP call names no asking agent of the API.
            using var owner = host.CreateClient(host.OwnerId.ToString());
            var thread = await owner.GetFromJsonAsync<NhAssistantThreadLookupDto>(
                $"/api/assistant/threads?agentId={SampleIntakeReviewerComposition.AgentId}&subjectType=project&subjectId={project:D}",
                Json,
                cancellationToken);
            var consultations = await owner.GetFromJsonAsync<NhAssistantConsultationListDto>(
                $"/api/assistant/threads/{thread!.Thread!.ConversationId}/consultations",
                Json,
                cancellationToken);
            Assert.Equal(2, consultations!.Total);
            Assert.All(consultations.Items, consultation =>
            {
                Assert.Equal(1, consultation.Depth);
                Assert.Null(consultation.AskingAgentId);
                Assert.Equal(host.ParticipantId.ToString(), consultation.AskerActorId);
            });
        }
        finally
        {
            host.Model.Use(new SampleAssistantChatClient());
        }
    }

    /// <summary>
    /// An MCP client of the API for the project participant, whose token carries <paramref name="issuer"/>.
    /// </summary>
    private async Task<McpClient> ConnectAsync(string issuer, CancellationToken cancellationToken)
    {
        var http = host.CreateClient(host.ParticipantId.ToString());
        http.DefaultRequestHeaders.Add(SampleTestAuthenticationHandler.IssuerHeader, issuer);
        return await McpClient.CreateAsync(
            new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Endpoint = new Uri(host.BaseAddress, "mcp"),
                    TransportMode = HttpTransportMode.StreamableHttp
                },
                http,
                ownsHttpClient: true),
            new McpClientOptions { ProtocolVersion = "2025-11-25" },
            cancellationToken: cancellationToken);
    }

    private static Dictionary<string, object?> Question(Guid project)
    {
        return new Dictionary<string, object?>
        {
            ["input"] = new Dictionary<string, object?>
            {
                ["subjectId"] = project.ToString("D"),
                ["question"] = "What is still missing for the intake?"
            }
        };
    }

    /// <summary>
    /// The consultation chain as the partner's assistant sends it: its agents with the application they
    /// belong to, outermost first, and the consultation depth of the calling turn.
    /// </summary>
    private static JsonObject Chain(int depth, params (string Application, string AgentId)[] agents)
    {
        return new JsonObject
        {
            ["agents"] = new JsonArray(agents
                .Select(agent => (JsonNode)new JsonObject { ["application"] = agent.Application, ["agentId"] = agent.AgentId })
                .ToArray()),
            ["consultationDepth"] = depth
        };
    }

    private static JsonObject Context(JsonObject chain)
    {
        return new JsonObject
        {
            [NhAssistantMcpContext.MetaKey] = new JsonObject
            {
                ["version"] = NhAssistantMcpContext.Version,
                ["invocation"] = new JsonObject { ["invocationId"] = Guid.NewGuid().ToString() },
                ["chain"] = chain
            }
        };
    }
}

/// <summary>
/// The agent-run sample host with the API's MCP endpoint, as Program.cs maps it, named in consultation
/// chains and reviewing the partner application's issuer (SampleConsultationChain).
/// </summary>
public sealed class ConsultationChainSampleHost(SamplePostgreSqlServer postgreSql) : AssistantAgentRunSampleHost(postgreSql)
{
    public const string Application = "https://projects.sample.test";
    public const string Partner = "https://partner-desk.sample.test";
    public const string PartnerIssuer = "https://login.partner-desk.sample.test";

    protected override void ConfigureSettings(IDictionary<string, string?> settings)
    {
        base.ConfigureSettings(settings);
        settings[SampleConsultationChain.ApplicationSetting] = Application;
        settings[SampleConsultationChain.PartnerApplicationSetting] = Partner;
        settings[SampleConsultationChain.PartnerIssuerSetting] = PartnerIssuer;
    }

    protected override void ConfigureAssistant(IServiceCollection services)
    {
        base.ConfigureAssistant(services);
        services.AddMcpServer()
            .WithHttpTransport(options => options.Stateless = true)
            .WithNewHeapPlatformAITools();
    }

    protected override void MapEndpoints(WebApplication app)
    {
        base.MapEndpoints(app);
        app.MapMcp("/mcp").RequireAuthorization();
    }
}
