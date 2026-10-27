using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// Who may consult an agent: the asker passes the agent's required policy as it is in effect now when
/// the consultation names no asker policy of its own, in code and as data, and about the whole
/// application only people who pass the agent's view policy consult it, on both relational providers.
/// </summary>
public sealed class AssistantConsultationPolicyTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string Reviewer = "project-reviewer";
    private const string Operations = "ops-reporter";
    private const string ReviewersPolicy = "project.reviewers";
    private const string FinancePolicy = "finance.agents";
    private const string OperationsViewPolicy = "ops.view";
    private const string Admin = "admin-1";
    private const string Member = "member-1";
    private const string Finance = "finance-1";
    private const string Watcher = "watcher-1";
    private const string ProjectId = "project-0001";
    private const string OrderId = "order-0001";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Consult_and_request_tools_follow_the_agents_required_policy_as_an_administrator_overrides_it(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithText("The project review is open.")
            .RespondWithText("The order review is open.")
            .RespondWithText("The project review is open.");
        await using var app = await StartAsync(provider, model);
        Assert.True((await SaveOrderConsultationAsync(app)).Success);

        // The administrator restricts the reviewer to the finance team; the member passes its policy in
        // code only.
        await OverrideRequiredPolicyAsync(app, Reviewer, FinancePolicy);
        await using var scope = app.Services.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<NhAssistantConsultationToolHandler>();
        var consultations = scope.ServiceProvider.GetRequiredService<NhAssistantRegistrationState>().AgentRuns[Reviewer].Consultations;
        var aboutProject = consultations.Single(consultation => consultation.SubjectType == "project");
        var aboutOrder = consultations.Single(consultation => consultation.SubjectType == "order");
        Assert.True(aboutOrder.IsData);
        var question = Input(new { subjectId = ProjectId, question = "What is still open?" });
        var orderQuestion = Input(new { subjectId = OrderId, question = "What is still open?" });
        var request = Input(new { subjectId = ProjectId, message = "Review the project again." });

        var consulted = await handler.ConsultAsync(aboutProject, question, Person(Member), CancellationToken.None);
        var consultedAsData = await handler.ConsultAsync(aboutOrder, orderQuestion, Person(Member), CancellationToken.None);
        var requested = await handler.RequestAsync(aboutProject, request, Person(Member), CancellationToken.None);
        Assert.Equal(NhAssistantErrorCodes.AgentForbidden, CodeOf(consulted));
        Assert.Equal(NhAssistantErrorCodes.AgentForbidden, CodeOf(consultedAsData));
        Assert.Equal(NhAssistantErrorCodes.AgentForbidden, CodeOf(requested));
        Assert.Empty(model.Requests);
        await using (var context = Context(app))
        {
            Assert.False(await context.Consultations.AnyAsync());
            Assert.False(await context.Runs.AnyAsync());
            Assert.False(await context.QueuedInputs.AnyAsync());
        }

        // Someone who passes the overridden policy consults and requests, in code and as data.
        var answered = await handler.ConsultAsync(aboutProject, question, Person(Finance), CancellationToken.None);
        Assert.True(answered.Success, CodeOf(answered));
        var answeredAsData = await handler.ConsultAsync(aboutOrder, orderQuestion, Person(Finance), CancellationToken.None);
        Assert.True(answeredAsData.Success, CodeOf(answeredAsData));
        var started = await handler.RequestAsync(aboutProject, request, Person(Finance), CancellationToken.None);
        Assert.True(started.Success, CodeOf(started));
        await using (var context = Context(app))
        {
            Assert.Equal([Finance, Finance], await context.Consultations.Select(consultation => consultation.AskerActorId).ToListAsync());
            Assert.Equal(Finance, (await context.Runs.SingleAsync()).InitiatorActorId);
        }
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_consultation_names_its_own_asker_policy_instead_of_the_agents_required_policy(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient().RespondWithText("The order review is open.");
        await using var app = await StartAsync(provider, model);
        Assert.True((await SaveOrderConsultationAsync(app, ReviewersPolicy)).Success);
        await OverrideRequiredPolicyAsync(app, Reviewer, FinancePolicy);
        await using var scope = app.Services.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<NhAssistantConsultationToolHandler>();
        var aboutOrder = scope.ServiceProvider.GetRequiredService<NhAssistantRegistrationState>().AgentRuns[Reviewer].Consultations
            .Single(consultation => consultation.SubjectType == "order");

        // The consultation's asker policy, which the member passes, decides who consults.
        var answered = await handler.ConsultAsync(aboutOrder, Input(new { subjectId = OrderId, question = "What is open?" }), Person(Member), CancellationToken.None);

        Assert.True(answered.Success, CodeOf(answered));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Only_people_who_pass_the_agents_view_policy_consult_it_about_the_whole_application(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient().RespondWithText("Nothing waits for the whole application.");
        await using var app = await StartAsync(provider, model);
        await using var scope = app.Services.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<NhAssistantConsultationToolHandler>();
        var aboutApplication = scope.ServiceProvider.GetRequiredService<NhAssistantRegistrationState>().AgentRuns[Operations].Consultations.Single();
        var question = Input(new { subjectId = NhAiSystemSubjects.GlobalId, question = "What waits?" });

        // The member reads the whole application but not the agent's work there: the consultation is
        // refused as for an unknown subject, before a consultation is recorded or the agent's model runs.
        var refused = await handler.ConsultAsync(aboutApplication, question, Person(Member), CancellationToken.None);
        Assert.Equal(NhAssistantErrorCodes.ConsultSubjectUnavailable, CodeOf(refused));
        Assert.Empty(model.Requests);
        await using (var context = Context(app))
        {
            Assert.False(await context.Consultations.AnyAsync());
        }

        // Someone who passes the view policy consults it.
        var answered = await handler.ConsultAsync(aboutApplication, question, Person(Watcher), CancellationToken.None);
        Assert.True(answered.Success, CodeOf(answered));
        Assert.Equal("Nothing waits for the whole application.", answered.Data!.Answer);
    }

    private Task<AssistantWebApplication> StartAsync(AssistantTestProvider provider, NhAiScriptedChatClient model)
    {
        return AssistantWebApplication.StartAsync(
            database,
            model,
            configure: services =>
            {
                var subjects = new TestSubjects();
                subjects.Roles[Finance] = NhAiSubjectRole.Participant;
                services.AddSingleton(subjects);
                services.AddSingleton<RecordingOperations>();
                services.AddSingleton(provider => provider.GetRequiredService<RecordingOperations>().Service);
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, PolicyPrincipals>());
                services.AddAuthorization(options =>
                {
                    options.AddPolicy(ReviewersPolicy, policy => policy.RequireClaim("permission", ReviewersPolicy));
                    options.AddPolicy(FinancePolicy, policy => policy.RequireClaim("permission", FinancePolicy));
                    options.AddPolicy(OperationsViewPolicy, policy => policy.RequireClaim("permission", OperationsViewPolicy));
                });
                // Runs are recorded, not executed: the startup check would require their background operations.
                services.Remove(services.Single(descriptor => descriptor.ImplementationType == typeof(NhAssistantStartupValidator)));
            },
            assistantBuilder: assistant => assistant
                .AddSubjectType<TestSubjectResolver>()
                .AddSubjectType<TestOrderResolver>()
                .UseConsultationsAsData(data => data.AllowRequests())
                .AddAgentPolicy(ReviewersPolicy, "Project reviewers.")
                .AddAgent(AssistantTestData.Agent(Reviewer, requiredPolicy: ReviewersPolicy))
                .ConfigureAgent(Reviewer, run => run
                    .WorksOn("project", "order")
                    .AllowConsultation("project", consult => consult
                        .Description("Knows the review of a project.")
                        .Proposals(false)
                        .AllowRequests())
                    .ConsultableAsData())
                .AddAgent(AssistantTestData.Agent(Operations))
                .ConfigureAgent(Operations, run => run
                    .WorksOnApplication()
                    .ViewPolicy(OperationsViewPolicy)
                    .AllowConsultation(NhAiSubjectRef.SystemType, consult => consult
                        .Description("Knows the operations of the whole application.")
                        .Proposals(false))),
            provider: provider);
    }

    /// <summary>
    /// A consultation of the reviewer about orders, defined as data, with or without an asker policy.
    /// </summary>
    private static async Task<NewHeap.Platform.Common.Models.TaskResult<NhAssistantDataConsultationDto>> SaveOrderConsultationAsync(
        AssistantWebApplication app,
        string? askerPolicy = null)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INhAssistantAgentConfiguration>().SaveConsultationAsync(
            Reviewer,
            "order",
            new NhAssistantDataConsultationRequest("Knows the review of an order.", AskerPolicy: askerPolicy, Routes: []),
            Admin);
    }

    /// <summary>
    /// Changes the required policy of an agent in code, as an administrator does in its editor.
    /// </summary>
    private static async Task OverrideRequiredPolicyAsync(AssistantWebApplication app, string agentId, string requiredPolicy)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var agent = (await services.GetRequiredService<NhAssistantAgentCatalog>().FindAsync(agentId, includeDisabled: true, CancellationToken.None))!;
        var definition = agent.Definition;
        var overridden = await services.GetRequiredService<NhAssistantAgentAdministration>().UpdateAsync(
            new NhAssistantAgentInput(
                agentId,
                definition.DisplayNameKey,
                definition.DescriptionKey,
                definition.Instructions.Content,
                definition.ToolSelectors,
                [],
                requiredPolicy,
                definition.Autonomy),
            definition.Version,
            Admin,
            CancellationToken.None);
        Assert.True(overridden.Success, CodeOf(overridden));
        var inEffect = await services.GetRequiredService<NhAssistantAgentCatalog>().FindAsync(agentId, includeDisabled: true, CancellationToken.None);
        Assert.Equal(requiredPolicy, inEffect!.Definition.RequiredPolicy);
    }

    private static NhAssistantDbContext Context(AssistantWebApplication app)
    {
        return app.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
    }

    /// <summary>
    /// The person as an MCP client calls the tools: outside a turn, with their own rights.
    /// </summary>
    private static NhAiInvocationContext Person(string actorId)
    {
        return new NhAiInvocationContext(actorId, "project-assistance", new Dictionary<string, string>())
        {
            ActorKind = NhAiActorKind.Human
        };
    }

    private static JsonElement Input(object value)
    {
        return JsonSerializer.SerializeToElement(value);
    }

    private static string? CodeOf(NewHeap.Platform.Common.Models.TaskResult result)
    {
        return result.GetResultItems().Select(item => item.Name).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
    }

    /// <summary>
    /// The people as their principals are rebuilt: the member passes the reviewer's policy in code, the
    /// finance member also the finance policy, the watcher the operations agent's view policy; everyone
    /// uses the assistant and so reads the whole application.
    /// </summary>
    private sealed class PolicyPrincipals : INhAiPrincipalFactory
    {
        public ValueTask<ClaimsPrincipal?> CreateAsync(string actorId, NhAiSubjectInfo subject, CancellationToken cancellationToken = default)
        {
            var permissions = actorId switch
            {
                Member => new[] { "assistant", ReviewersPolicy },
                Finance => new[] { "assistant", ReviewersPolicy, FinancePolicy },
                Watcher => new[] { "assistant", OperationsViewPolicy },
                _ => null
            };
            return ValueTask.FromResult(permissions is null
                ? null
                : new ClaimsPrincipal(new ClaimsIdentity(
                    permissions.Select(value => new Claim("permission", value)).Append(new Claim(ClaimTypes.NameIdentifier, actorId)),
                    "test")));
        }
    }
}
