using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Collaboration;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// FB29 and FB30: a request through another assistant that answers in the chat it was asked in, once,
/// readable by the person who asked and with a link to the subject, never for MCP callers, shadow runs
/// or restricted results; and a consultation that takes requests only.
/// </summary>
public sealed class AssistantRequestAnswerTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string TargetAgentId = "project-assistant";
    private const string AskingAgentId = "consulting-assistant";
    private const string RequestTool = "request_project-assistant_project_v1";
    private const string RequestToolId = "assistant.request.project-assistant.project";
    private const string ConsultToolId = "assistant.consult.project-assistant.project";
    private const string Member = "member-1";
    private const string ProjectId = "project-0001";
    private const string Answer = "The intake review found two open checks: the owner and the budget.";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_completed_request_answers_once_in_the_asking_chat_for_the_person_who_asked_with_a_link_to_the_subject(AssistantTestProvider provider)
    {
        // The answer's title is in the person's culture; without the application's directory, in the
        // culture the run ends in.
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
        await using var host = await CreateHostAsync(provider, AskOnce(), consult => consult.AnswerRequests(NhAssistantRequestAnswerRoute.AskingConversation));
        var conversation = await host.CreateConversationAsync(Member, AskingAgentId);

        var turn = await host.SendAsync(conversation.Id, "Ask for a new review.", Member);

        var run = await SingleRunAsync(host);
        Assert.Equal(conversation.Id, run.AskingConversationId);
        Assert.Equal(turn.Single<NhAssistantTurnCompletedEvent>().TurnId, run.AskingTurnId);
        // The thread of the agent keeps the request as before; the asking chat has no answer yet.
        Assert.Empty(await ApplicationMessagesAsync(host, conversation.Id));

        Assert.True(await CompleteAsync(host, run, Answer));

        var message = Assert.Single(await ApplicationMessagesAsync(host, conversation.Id));
        Assert.Equal(NhAssistantSpeakers.ApplicationActorId, message.AuthorActorId);
        Assert.Equal(NhAssistantConversationMessages.IdempotencyPrefix + NhAssistantRequestAnswers.IdempotencyPrefix + run.Id.ToString("N"), message.ClientMessageId);
        var parts = NhAssistantContent.DeserializeParts(message.PartsJson);
        Assert.Equal(Answer, NhAssistantContent.TextOf(parts));
        var details = parts.Single(part => part.Type == NhAssistantStoredPart.ApplicationType);
        Assert.Equal("Answer to your request about Project project-", details.Title);
        Assert.Equal("/projects/" + ProjectId, details.LinkUrl);
        // An answer is the model's output: information for later turns, never the application's instruction.
        Assert.Null(details.Trusted);
        Assert.Single(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.ApplicationMessagePosted && evt.ConversationId == conversation.Id);

        // Once: a later end loses, and a repeated post finds the earlier message by the run's key.
        var thread = await ThreadOfAsync(host, run);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            Assert.False(await scope.ServiceProvider.GetRequiredService<NhAssistantRunEnding>()
                .EndAsync(run, thread, NhAssistantRunStatuses.Completed, null, null));
            var stored = (await Runs(host).FindRunAsync(run.Id, CancellationToken.None))!;
            Assert.False(await scope.ServiceProvider.GetRequiredService<NhAssistantRequestAnswers>()
                .PostAsync(stored, thread, CancellationToken.None));
        }

        Assert.Single(await ApplicationMessagesAsync(host, conversation.Id));
        Assert.Single(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.ApplicationMessagePosted && evt.ConversationId == conversation.Id);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Only_the_person_who_asked_and_may_still_open_the_chat_gets_the_answer(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider, AskOnce(), consult => consult.AnswerRequests(NhAssistantRequestAnswerRoute.AskingConversation));
        var conversation = await host.CreateConversationAsync(Member, AskingAgentId);
        await host.SendAsync(conversation.Id, "Ask for a new review.", Member);
        var run = await SingleRunAsync(host);

        // The chat now belongs to someone else, as if the person lost it: the answer is not posted
        // there, the run still completes and the agent's thread keeps the answer.
        await using (var context = Context(host))
        {
            var stored = await context.Conversations.SingleAsync(item => item.Id == conversation.Id);
            stored.OwnerActorId = "someone-else";
            await context.SaveChangesAsync();
        }

        Assert.True(await CompleteAsync(host, run, Answer));
        Assert.Empty(await ApplicationMessagesAsync(host, conversation.Id));
        Assert.DoesNotContain(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.ApplicationMessagePosted);
        Assert.Equal(NhAssistantRunStatuses.Completed, (await Runs(host).FindRunAsync(run.Id, CancellationToken.None))!.Status);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Without_the_opt_in_and_outside_a_chat_turn_a_request_answers_only_in_the_agents_thread(AssistantTestProvider provider)
    {
        // Without AnswerRequests the run keeps no asking conversation.
        await using (var host = await CreateHostAsync(provider, AskOnce()))
        {
            var conversation = await host.CreateConversationAsync(Member, AskingAgentId);
            await host.SendAsync(conversation.Id, "Ask for a new review.", Member);
            var run = await SingleRunAsync(host);
            Assert.Null(run.AskingConversationId);
            Assert.Null(run.AskingTurnId);
            Assert.True(await CompleteAsync(host, run, Answer));
            Assert.Empty(await ApplicationMessagesAsync(host, conversation.Id));
        }

        // An MCP client's request has no turn and no conversation, so it answers in the thread only.
        await using (var host = await CreateHostAsync(provider, new NhAiScriptedChatClient(), consult => consult.AnswerRequests(NhAssistantRequestAnswerRoute.AskingConversation)))
        {
            await using var scope = host.Services.CreateAsyncScope();
            var options = scope.ServiceProvider.GetRequiredService<NhAssistantRegistrationState>().AgentRuns[TargetAgentId].Consultations.Single();
            var requested = await scope.ServiceProvider.GetRequiredService<NhAssistantConsultationToolHandler>().RequestAsync(
                options,
                JsonSerializer.SerializeToElement(new { subjectId = ProjectId, message = "Review the intake again." }),
                new NhAiInvocationContext(Member, "project-assistance", new Dictionary<string, string>()) { ActorKind = NhAiActorKind.Human },
                CancellationToken.None);
            Assert.True(requested.Success);
            var run = await SingleRunAsync(host);
            Assert.Equal(Member, run.InitiatorActorId);
            Assert.Null(run.AskingConversationId);
        }
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Shadow_runs_restricted_results_and_restricted_consultations_post_nothing(AssistantTestProvider provider)
    {
        await using (var host = await CreateHostAsync(provider, AskOnce(), consult => consult.AnswerRequests(NhAssistantRequestAnswerRoute.AskingConversation)))
        {
            var conversation = await host.CreateConversationAsync(Member, AskingAgentId);
            await host.SendAsync(conversation.Id, "Ask for a new review.", Member);
            var run = await SingleRunAsync(host);
            var thread = await ThreadOfAsync(host, run);

            // Shadow work stays with administrators.
            await using (var scope = host.Services.CreateAsyncScope())
            {
                var answers = scope.ServiceProvider.GetRequiredService<NhAssistantRequestAnswers>();
                var shadow = new AssistantRun { Id = run.Id, AskingConversationId = conversation.Id, InitiatorActorId = Member, Shadow = true, Result = Answer };
                Assert.False(await answers.PostAsync(shadow, thread, CancellationToken.None));
            }

            // A run that read what people without the tool's policy may not see keeps its answer in
            // its thread.
            await using (var context = Context(host))
            {
                var stored = await context.Runs.SingleAsync(item => item.Id == run.Id);
                stored.UsedRestrictedResults = true;
                await context.SaveChangesAsync();
            }

            Assert.True(await CompleteAsync(host, run, Answer));
            Assert.Empty(await ApplicationMessagesAsync(host, conversation.Id));
        }

        // A consultation whose answers are restricted never keeps the asking conversation.
        await using (var host = await CreateHostAsync(
            provider,
            AskOnce(),
            consult => consult
                .AnswerRequests(NhAssistantRequestAnswerRoute.AskingConversation)
                .DataClassification(NhAiDataClassification.Restricted),
            permitted: [NhAiDataClassification.Internal, NhAiDataClassification.Restricted]))
        {
            var conversation = await host.CreateConversationAsync(Member, AskingAgentId);
            await host.SendAsync(conversation.Id, "Ask for a new review.", Member);
            var run = await SingleRunAsync(host);
            Assert.Null(run.AskingConversationId);
        }
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_consultation_that_takes_requests_only_generates_only_the_request_tool(AssistantTestProvider provider)
    {
        var asker = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(RequestTool, new { input = new { subjectId = ProjectId, message = "Review the intake again." } }, "call-1")
            .RespondWithText("The reviewer will answer here.");
        await using var host = await CreateHostAsync(
            provider,
            asker,
            consult => consult.RequestsOnly().AnswerRequests(NhAssistantRequestAnswerRoute.AskingConversation),
            allowRequests: false);
        var catalog = host.Services.GetServices<INhAiToolCatalog>().OfType<NhAssistantConsultationToolCatalog>().Single();

        var request = Assert.Single(catalog.Descriptors);
        Assert.Equal(RequestToolId, request.Id);
        Assert.StartsWith("Knows the intake review of a project. Ask the agent 'project-assistant'", request.Description, StringComparison.Ordinal);
        Assert.Contains("also in the chat the request was made in", request.Description, StringComparison.Ordinal);
        NhAiToolCatalogAttestation.Validate(catalog, host.Services);
        var options = host.Services.GetRequiredService<NhAssistantRegistrationState>().AgentRuns[TargetAgentId].Consultations.Single();
        // A request's run proposes under the agent's own policies: no consultation proposal routes.
        Assert.Empty(options.AllowedRoutes);

        // The asking model sees the request tool only, and the request answers in its chat.
        var conversation = await host.CreateConversationAsync(Member, AskingAgentId);
        await host.SendAsync(conversation.Id, "Ask for a new review.", Member);
        var offered = asker.Requests[0].Options?.Tools?.Select(tool => tool.Name).ToArray() ?? [];
        Assert.Contains(RequestTool, offered);
        Assert.DoesNotContain(offered, name => name.StartsWith("consult_", StringComparison.Ordinal));
        var run = await SingleRunAsync(host);
        Assert.Equal(conversation.Id, run.AskingConversationId);

        // The administration shows it as a consultation that takes requests only.
        await using var scope = host.Services.CreateAsyncScope();
        var configuration = await scope.ServiceProvider.GetRequiredService<INhAssistantAgentConfiguration>()
            .GetAsync(TargetAgentId, CancellationToken.None);
        var shown = Assert.Single(configuration!.CodeConsultations);
        Assert.Equal((true, true, string.Empty, RequestToolId), (shown.RequestsOnly, shown.AnswerRequestsInAskingConversation, shown.ConsultToolId, shown.RequestToolId));
        Assert.Equal([AskingAgentId], shown.AskingAgents);
        Assert.Empty(shown.AllowedRoutes);
    }

    [Fact]
    public void Settings_of_a_consultations_own_turn_or_without_requests_refuse_startup()
    {
        var state = new NhAssistantRegistrationState();
        state.AddAgent(AssistantTestData.Agent(TargetAgentId));
        var options = state.GetOrAddAgentRun(TargetAgentId);
        var agent = new NhAssistantAgentRunBuilder(options);

        // Every setting of a consultation's own turn, named in the message.
        var refused = Assert.Throws<InvalidOperationException>(() => agent.AllowConsultation("project", consult => consult
            .RequestsOnly()
            .Limits(maxToolCalls: 2)
            .History(5)
            .Tools("projects.*")
            .MinimumRole(NhAiSubjectRole.Participant)
            .Proposals(false)
            .Instructions(AssistantTestData.Instructions)));
        Assert.Contains("History, Instructions, Limits, MinimumRole, Proposals, Tools", refused.Message, StringComparison.Ordinal);
        Assert.Contains("AllowRequests(false)", Assert.Throws<InvalidOperationException>(() => agent.AllowConsultation("order", consult => consult
            .RequestsOnly()
            .AllowRequests(false))).Message, StringComparison.Ordinal);
        Assert.Contains("takes no requests", Assert.Throws<InvalidOperationException>(() => agent.AllowConsultation("task", consult => consult
            .AnswerRequests(NhAssistantRequestAnswerRoute.AskingConversation))).Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => agent.AllowConsultation("note", consult => consult
            .AnswerRequests((NhAssistantRequestAnswerRoute)7)));

        // The settings every tool shares are allowed: description, policy, exposure and classification.
        agent.AllowConsultation("ticket", consult => consult
            .Description("Works on tickets.")
            .Policy("app.assistant")
            .Exposure(NhAiToolExposure.Local | NhAiToolExposure.Mcp)
            .DataClassification(NhAiDataClassification.Confidential)
            .RequestsOnly()
            .AnswerRequests(NhAssistantRequestAnswerRoute.AskingConversation));
        var ticket = options.Consultations.Single(consultation => consultation.SubjectType == "ticket");
        Assert.Equal((true, true, NhAssistantRequestAnswerRoute.AskingConversation), (ticket.RequestsOnly, ticket.AllowRequests, ticket.RequestAnswerRoute));
    }

    private async Task<AssistantTestHost> CreateHostAsync(
        AssistantTestProvider provider,
        NhAiScriptedChatClient asker,
        Action<NhAssistantConsultationBuilder>? consult = null,
        bool allowRequests = true,
        IReadOnlyList<NhAiDataClassification>? permitted = null)
    {
        return await AssistantTestHost.CreateAsync(
            database,
            provider,
            new NhAiScriptedChatClient(),
            configure: services =>
            {
                services.AddSingleton(new TestSubjects());
                services.AddSingleton<RecordingOperations>();
                services.AddSingleton(services => services.GetRequiredService<RecordingOperations>().Service);
                services.AddSingleton<ConsultationPrincipals>();
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory>(services => services.GetRequiredService<ConsultationPrincipals>()));
                services.AddKeyedSingleton<IChatClient>("asking-chat-model", asker);
                services.AddNewHeapPlatformAI(ai => ai.AddChatProfile("asking-chat", profile => profile
                    .UseKeyedClient("asking-chat-model")
                    .RequireCapabilities(NhAiModelCapability.FunctionCalling)
                    .WithStreaming(NhAiStreamingPolicy.Allowed)
                    .PermitDataClassifications((permitted ?? [NhAiDataClassification.Internal]).ToArray())
                    .PermitExecutionRegions("local")
                    .WithBudget(maxInputTokens: 8_192, maxOutputTokens: 1_024, maxCalls: 16)));
            },
            assistantBuilder: assistant => assistant
                .AddAgent(AssistantTestData.Agent(AskingAgentId) with
                {
                    ProfileName = "asking-chat",
                    ToolSelectors = ["assistant.consult.*", "assistant.request.*"]
                })
                // The link people follow to the project, where the application shows the agent's work.
                .AddSubjectType<TestSubjectResolver>(type => type.OpenAt("/projects/{id}"))
                .ConfigureAgent(TargetAgentId, agent => agent
                    .Grant("projects.*")
                    .AllowConsultation("project", builder =>
                    {
                        builder.Description("Knows the intake review of a project.");
                        if (allowRequests)
                        {
                            builder.AllowRequests();
                        }

                        consult?.Invoke(builder);
                    })));
    }

    /// <summary>
    /// The asking model requests a new review once and answers the person.
    /// </summary>
    private static NhAiScriptedChatClient AskOnce()
    {
        return new NhAiScriptedChatClient()
            .RespondWithFunctionCall(RequestTool, new { input = new { subjectId = ProjectId, message = "Review the intake again." } }, "call-1")
            .RespondWithText("The reviewer will answer when it is done.");
    }

    /// <summary>
    /// Ends the run completed with <paramref name="result"/> as its answer, as its operation does.
    /// </summary>
    private static async Task<bool> CompleteAsync(AssistantTestHost host, AssistantRun run, string result)
    {
        await using (var context = Context(host))
        {
            var stored = await context.Runs.SingleAsync(item => item.Id == run.Id);
            stored.Result = result;
            await context.SaveChangesAsync();
        }

        var thread = await ThreadOfAsync(host, run);
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NhAssistantRunEnding>()
            .EndAsync(run, thread, NhAssistantRunStatuses.Completed, null, null);
    }

    private static async Task<AssistantRun> SingleRunAsync(AssistantTestHost host)
    {
        await using var context = Context(host);
        var run = await context.Runs.SingleAsync();
        Assert.Equal(NhAssistantTriggerIds.AgentRequest, run.TriggerId);
        return run;
    }

    private static async Task<AssistantThread> ThreadOfAsync(AssistantTestHost host, AssistantRun run)
    {
        return (await Runs(host).FindThreadByConversationAsync(run.ConversationId, CancellationToken.None))!;
    }

    private static async Task<IReadOnlyList<AssistantMessage>> ApplicationMessagesAsync(AssistantTestHost host, Guid conversationId)
    {
        return (await host.Services.GetRequiredService<INhAssistantStore>().GetMessagesAsync(conversationId, 100, CancellationToken.None))
            .Where(message => message.Role == NhAssistantMessageRoles.Application)
            .ToArray();
    }

    private static NhAssistantDbContext Context(AssistantTestHost host)
    {
        return host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
    }

    private static NhAssistantRunStore Runs(AssistantTestHost host)
    {
        return host.Services.GetRequiredService<NhAssistantRunStore>();
    }
}
