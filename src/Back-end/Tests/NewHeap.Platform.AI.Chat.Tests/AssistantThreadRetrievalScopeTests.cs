using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using Xunit;
using static NewHeap.Platform.AI.Chat.Tests.Infrastructure.ServerSentEventReader;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// A person's turn in an agent's subject thread retrieves within the thread's subject and its
/// division, as the turn's tools do, never within the active division the person's request carries:
/// the thread is shared with every reader of the subject. A plain chat keeps the person's own
/// context. Proved on both relational providers over the assistant's HTTP endpoints.
/// </summary>
public sealed class AssistantThreadRetrievalScopeTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string AgentId = "project-assistant";
    private const string Member = "member-1";
    private const string DivisionAccessPolicy = "division-access";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_persons_thread_turn_retrieves_within_the_subjects_division_and_a_chat_within_the_requests(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithText("Answered in the project's thread.")
            .RespondWithText("Answered in a chat.");
        await using var app = await StartAsync(provider, model);
        var projects = app.Services.GetRequiredService<DivisionProjects>();
        var subjectDivision = Guid.NewGuid();
        var activeDivision = Guid.NewGuid();
        var project = Guid.NewGuid().ToString("N");
        projects.Divisions[project] = subjectDivision;
        var documents = app.Services.GetRequiredService<ScopedDocuments>();
        documents.SubjectDivision = subjectDivision;
        documents.ActiveDivision = activeDivision;
        documents.Project = project;

        // The member works in another division than the project's, as the client's global selection.
        using var member = app.CreateClient(Member);
        member.DefaultRequestHeaders.Add(NewHeap.Platform.AspNet.Common.Constants.HttpHeaderKeys.ActiveDivisionId, activeDivision.ToString());
        var threadId = (await CreateThreadAsync(app, project)).ConversationId;
        using var threadTurn = await member.PostAsync($"/api/assistant/conversations/{threadId}/messages", Json(new { text = "What is the plan?" }));
        var (threadEvents, _) = await ReadAllAsync(threadTurn);

        using var created = await member.PostAsync("/api/assistant/conversations", Json(new { agentId = AgentId }));
        var chatId = (await ReadJsonAsync(created)).GetProperty("id").GetString();
        using var chatTurn = await member.PostAsync($"/api/assistant/conversations/{chatId}/messages", Json(new { text = "What is the plan?" }));
        var (chatEvents, _) = await ReadAllAsync(chatTurn);

        Assert.Equal("completed", threadEvents[^1].Data.GetProperty("status").GetString());
        Assert.Equal("completed", chatEvents[^1].Data.GetProperty("status").GetString());
        var requests = documents.Requests.ToArray();
        Assert.Equal(2, requests.Length);

        // The thread turn: the member decides as the actor, within the project and its division.
        var thread = requests[0].InvocationContext;
        Assert.Equal(Member, thread.ActorId);
        Assert.Equal(NhAiActorKind.Human, thread.ActorKind);
        Assert.Contains(thread.ExecutionScopes, scope => scope.Type == "project" && scope.Id == project);
        Assert.Contains(thread.ExecutionScopes, scope => scope.Type == "division" && scope.Id == subjectDivision.ToString());
        Assert.DoesNotContain(thread.ExecutionScopes, scope => scope.Id == activeDivision.ToString());
        Assert.True(thread.TryGetScopeValue("division-id", out var threadDivision));
        Assert.Equal(subjectDivision.ToString(), threadDivision);
        Assert.Empty(thread.CapabilityGrants);
        var threadInstructions = model.Requests[0].Options!.Instructions!;
        Assert.Contains(ScopedDocuments.SubjectDivisionText, threadInstructions);
        Assert.Contains(ScopedDocuments.ProjectText, threadInstructions);
        Assert.DoesNotContain(ScopedDocuments.ActiveDivisionText, threadInstructions);

        // The plain chat keeps the member's own active division.
        var chat = requests[1].InvocationContext;
        Assert.Contains(chat.ExecutionScopes, scope => scope.Type == "division" && scope.Id == activeDivision.ToString());
        Assert.DoesNotContain(chat.ExecutionScopes, scope => scope.Type == "project");
        var chatInstructions = model.Requests[1].Options!.Instructions!;
        Assert.Contains(ScopedDocuments.ActiveDivisionText, chatInstructions);
        Assert.DoesNotContain(ScopedDocuments.SubjectDivisionText, chatInstructions);
    }

    private Task<AssistantWebApplication> StartAsync(AssistantTestProvider provider, NhAiScriptedChatClient model)
    {
        return AssistantWebApplication.StartAsync(
            database,
            model,
            configure: services =>
            {
                services.AddAuthorizationBuilder()
                    .AddPolicy(DivisionAccessPolicy, policy => policy.RequireAuthenticatedUser());
                services.AddNewHeapPlatformAIAspNet(ai => ai.AddActiveDivisionScope(DivisionAccessPolicy));
                services.AddSingleton<DivisionProjects>();
                services.AddSingleton<ScopedDocuments>();
                services.AddNewHeapPlatformAI(ai => ai
                    .AddContextSource<ScopedDocumentSource>()
                    .UseContextAuthorizationPolicy<TestDocumentPolicy>());
                services.AddSingleton<RecordingOperations>();
                services.AddSingleton(provider => provider.GetRequiredService<RecordingOperations>().Service);
                // Runs are not executed here: the startup check would require their background operations.
                services.Remove(services.Single(descriptor => descriptor.ImplementationType == typeof(NhAssistantStartupValidator)));
            },
            assistantBuilder: assistant => assistant
                .AddSubjectType<DivisionProjectResolver>()
                .ConfigureAgent(AgentId, run => run.WorksOn("project"))
                .ConfigureAgentContext(AgentId, context => context.UseRetrievedContext(retrieval => retrieval.Sources(ScopedDocumentSource.SourceId))),
            provider: provider);
    }

    private static async Task<AssistantThread> CreateThreadAsync(AssistantWebApplication app, string projectId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var info = await services.GetRequiredService<NhAssistantSubjectAccessService>()
            .DescribeAsync(new NhAiSubjectRef("project", projectId), CancellationToken.None);
        var agent = (await services.GetRequiredService<NhAssistantAgentCatalog>().FindAsync(AgentId, includeDisabled: false, CancellationToken.None))!;
        return (await services.GetRequiredService<NhAssistantThreadService>().GetOrCreateAsync(agent, info!, CancellationToken.None)).Data!;
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }
}

/// <summary>
/// The divisions of the test projects, and the member's role on each.
/// </summary>
internal sealed class DivisionProjects
{
    public ConcurrentDictionary<string, Guid> Divisions { get; } = new();
}

internal sealed class DivisionProjectResolver(DivisionProjects projects) : INhAiSubjectAccessResolver
{
    public string SubjectType => "project";

    public ValueTask<NhAiSubjectInfo?> DescribeAsync(NhAiSubjectRef subject, CancellationToken cancellationToken = default)
    {
        if (!projects.Divisions.TryGetValue(subject.Id, out var divisionId))
        {
            return ValueTask.FromResult<NhAiSubjectInfo?>(null);
        }

        return ValueTask.FromResult<NhAiSubjectInfo?>(new NhAiSubjectInfo(subject, NhAiSubjectInfo.CreateScopeKey(null, divisionId))
        {
            DivisionId = divisionId,
            Version = "1",
            DisplayName = "Project " + subject.Id[..8]
        });
    }

    public ValueTask<NhAiSubjectAccess> ResolveAsync(NhAiSubjectRef subject, string actorId, CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(actorId == "member-1" && projects.Divisions.ContainsKey(subject.Id)
            ? new NhAiSubjectAccess(NhAiSubjectRole.Participant)
            : NhAiSubjectAccess.None);
    }

    public async IAsyncEnumerable<NhAiSubjectMember> ListMembersAsync(
        NhAiSubjectRef subject,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        yield return new NhAiSubjectMember("member-1", NhAiSubjectRole.Participant) { DisplayName = "member-1" };
    }
}

/// <summary>
/// The requests the scoped document source received and the scopes of its documents.
/// </summary>
internal sealed class ScopedDocuments
{
    // Without apostrophes, which the formatter escapes in the retrieved JSON.
    public const string SubjectDivisionText = "Plan of the division of the project.";
    public const string ActiveDivisionText = "Plan of the other division of the member.";
    public const string ProjectText = "A note about the project.";

    public ConcurrentQueue<NhAiContextRequest> Requests { get; } = new();

    public Guid SubjectDivision { get; set; }

    public Guid ActiveDivision { get; set; }

    public string Project { get; set; } = "";
}

/// <summary>
/// A document of the project's division, one of the member's other division and one of the project;
/// the resolver keeps those whose scope the turn's context holds.
/// </summary>
internal sealed class ScopedDocumentSource(ScopedDocuments documents) : INhAiContextSource
{
    public const string SourceId = "test-documents";

    public NhAiContextSourceDescriptor Descriptor { get; } = new(
        SourceId,
        "Division and project documents of the test application.",
        NhAiDataClassification.Internal,
        10,
        2_000);

    public ValueTask<IReadOnlyList<NhAiContextItem>> RetrieveAsync(
        NhAiContextRequest request,
        CancellationToken cancellationToken = default)
    {
        documents.Requests.Enqueue(request);
        var created = DateTimeOffset.UtcNow.AddDays(-1);
        return ValueTask.FromResult<IReadOnlyList<NhAiContextItem>>(
        [
            Item("plan-d", ScopedDocuments.SubjectDivisionText, new NhAiExecutionScopeEntry("division", documents.SubjectDivision.ToString()), created),
            Item("plan-e", ScopedDocuments.ActiveDivisionText, new NhAiExecutionScopeEntry("division", documents.ActiveDivision.ToString()), created),
            Item("note-s", ScopedDocuments.ProjectText, new NhAiExecutionScopeEntry("project", documents.Project), created)
        ]);
    }

    private static NhAiContextItem Item(string id, string text, NhAiExecutionScopeEntry scope, DateTimeOffset created)
    {
        return new NhAiContextItem(id, id, SourceId, "text/plain", text, NhAiDataClassification.Internal, NhAiContextTrust.UntrustedRetrieved, [scope], created);
    }
}
