using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// Subject types for people (W1 of the plan for agents working on application objects): names per
/// language with a fallback, the entity binding, links and the search that keeps only what the
/// searching person may read, on both relational providers.
/// </summary>
[Collection(AssistantDatabaseCollection.Name)]
public sealed class AssistantSubjectTypeTests(AssistantDatabaseFixture database)
{
    private const string AgentId = "project-assistant";
    private const string ChangeStatusFunction = "projects_change_status_v1";
    private const string Approver = "approver-1";
    private const string Member = "member-1";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_search_returns_only_the_subjects_the_caller_may_read_within_the_limit(AssistantTestProvider provider)
    {
        var projects = new SearchableProjects();
        projects.Add("alpha-1", "Alpha one");
        projects.Add("alpha-2", "Alpha two", hiddenFrom: Member);
        projects.Add("alpha-3", "Alpha three", tenantId: "other-tenant");
        projects.Add("alpha-4", "Alpha four");
        projects.Add("beta-1", "Beta one");
        // The application's search may repeat a subject; people see it once.
        projects.Repeat("alpha-1");
        await using var host = await CreateHostAsync(provider, projects, new NhAiScriptedChatClient());

        var found = await SearchAsync(host, "project", "  alpha ", null, Member);
        Assert.True(found.Success);
        Assert.Equal(["alpha-1", "alpha-4"], found.Data!.Select(info => info.Subject.Id));
        Assert.Equal("Alpha one", found.Data[0].DisplayName);
        Assert.Equal("alpha", projects.LastRequest!.Query);
        Assert.Equal(Member, projects.LastRequest.ActorId);
        Assert.Equal(NhAiSubjectSearchRequest.DefaultLimit, projects.LastRequest.Limit);

        // The approver reads alpha-2 too; the limit ends the search.
        var limited = await SearchAsync(host, "project", "alpha", 2, Approver);
        Assert.Equal(["alpha-1", "alpha-2"], limited.Data!.Select(info => info.Subject.Id));
        Assert.Equal(2, projects.LastRequest!.Limit);
        // A larger limit is reduced to the most the platform shows.
        Assert.Equal(3, (await SearchAsync(host, "project", "alpha", 500, Approver)).Data!.Count);
        Assert.Equal(NhAiSubjectSearchRequest.MaxLimit, projects.LastRequest!.Limit);

        // A stranger finds nothing.
        Assert.Empty((await SearchAsync(host, "project", "alpha", null, "stranger")).Data!);

        // Results the caller may not read are examined at most four times the limit.
        var crowded = new SearchableProjects();
        for (var index = 0; index < 100; index++)
        {
            crowded.Add($"hidden-{index:D3}", $"Hidden {index}", hiddenFrom: Member);
        }
        crowded.Add("visible-1", "Hidden but visible");
        await using var crowdedHost = await CreateHostAsync(provider, crowded, new NhAiScriptedChatClient());
        Assert.Empty((await SearchAsync(crowdedHost, "project", "hidden", 5, Member)).Data!);
        Assert.Equal(5 * NhAssistantSubjectSearchService.MaxExaminedPerResult, crowded.Pulled);

        Assert.Equal(NhAssistantAdminErrorCodes.ValidationFailed, Code(await SearchAsync(host, "project", " a ", null, Member)));
        Assert.Equal(NhAssistantAdminErrorCodes.ValidationFailed, Code(await SearchAsync(host, "project", new string('x', 101), null, Member)));
        Assert.Equal(NhAssistantErrorCodes.SubjectTypeUnknown, Code(await SearchAsync(host, "order", "alpha", null, Member)));
        Assert.Equal(NhAssistantErrorCodes.SubjectTypeUnknown, Code(await SearchAsync(host, "Not_A_Type", "alpha", null, Member)));
        Assert.Equal(NhAssistantErrorCodes.SubjectSearchUnavailable, Code(await SearchAsync(host, "ticket", "alpha", null, Member)));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Threads_proposals_and_the_catalog_name_and_link_the_subject_type_in_the_callers_language(AssistantTestProvider provider)
    {
        var projects = new SearchableProjects();
        var subjectId = "PRJ 7/2026";
        projects.Add(subjectId, "Project seven");
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ChangeStatusFunction, new { input = new { projectId = Guid.NewGuid(), status = "Active" } }, "call-status")
            .RespondWithText("I proposed to activate the project.");
        await using var host = await CreateHostAsync(provider, projects, model);
        await StartAndRunAsync(host, new NhAiSubjectRef("project", subjectId));

        var dutch = await InCultureAsync("nl-NL", () => GetWorkAsync(host, subjectId, Approver));
        Assert.Equal("Opdracht", dutch.SubjectTypeName);
        // The id is URL-encoded into the link template.
        Assert.Equal("/management/projects/PRJ%207%2F2026", dutch.SubjectLink);
        var agent = Assert.Single(dutch.Agents);
        Assert.Equal("Opdracht", agent.Thread!.SubjectTypeName);
        Assert.Equal(dutch.SubjectLink, agent.Thread.SubjectLink);
        var proposal = Assert.Single(dutch.OpenProposals).Proposal;
        Assert.Equal("Opdracht", proposal.SubjectTypeName);
        Assert.Equal(dutch.SubjectLink, proposal.SubjectLink);

        // Another culture of a known language, then the fallback language.
        Assert.Equal("Project", (await InCultureAsync("en-GB", () => GetWorkAsync(host, subjectId, Approver))).SubjectTypeName);
        Assert.Equal("Project", (await InCultureAsync("de-DE", () => GetWorkAsync(host, subjectId, Approver))).SubjectTypeName);

        var catalog = await InCultureAsync("nl-NL", () =>
        {
            using var scope = host.Services.CreateScope();
            return Task.FromResult(scope.ServiceProvider.GetRequiredService<INhAssistantAgentConfiguration>().GetCatalog());
        });
        Assert.Equal(["project", "ticket"], catalog.SubjectTypes.Select(type => type.Id));
        Assert.Equal(
            new NhAssistantSubjectTypeDto(
                "project",
                "Opdracht",
                "Opdrachten",
                nameof(TestProjectEntity),
                typeof(TestProjectEntity).FullName,
                "/management/projects/{id}",
                Searchable: true,
                IsSystem: false),
            catalog.SubjectTypes[0]);
        // Without a configuration people see the id, and the type is chosen by id.
        Assert.Equal(new NhAssistantSubjectTypeDto("ticket", "ticket", null, null, null, null, false, false), catalog.SubjectTypes[1]);
    }

    [Fact]
    public void A_link_template_has_one_id_in_the_application_or_in_the_path_of_an_https_address()
    {
        Assert.True(NhAssistantSubjectLinks.IsValidTemplate("/projects/{id}"));
        Assert.True(NhAssistantSubjectLinks.IsValidTemplate("/management/intake-reviews?project={id}"));
        Assert.True(NhAssistantSubjectLinks.IsValidTemplate("https://portal.example.com/projects/{id}/overview"));
        Assert.False(NhAssistantSubjectLinks.IsValidTemplate(null));
        Assert.False(NhAssistantSubjectLinks.IsValidTemplate("/projects"));
        Assert.False(NhAssistantSubjectLinks.IsValidTemplate("/projects/{id}/{id}"));
        Assert.False(NhAssistantSubjectLinks.IsValidTemplate("/projects/{id}/{name}"));
        Assert.False(NhAssistantSubjectLinks.IsValidTemplate("projects/{id}"));
        Assert.False(NhAssistantSubjectLinks.IsValidTemplate("//evil.example.com/{id}"));
        Assert.False(NhAssistantSubjectLinks.IsValidTemplate("/\\evil.example.com/{id}"));
        Assert.False(NhAssistantSubjectLinks.IsValidTemplate("/projects/ {id}"));
        Assert.False(NhAssistantSubjectLinks.IsValidTemplate("http://portal.example.com/projects/{id}"));
        Assert.False(NhAssistantSubjectLinks.IsValidTemplate("javascript:alert({id})"));
        Assert.False(NhAssistantSubjectLinks.IsValidTemplate("https://{id}.example.com/projects"));
        Assert.False(NhAssistantSubjectLinks.IsValidTemplate("https://user@portal.example.com/projects/{id}"));

        Assert.Equal("/p/a%20b%2Fc%3Fd%26e", NhAssistantSubjectLinks.Build("/p/{id}", "a b/c?d&e"));
    }

    [Fact]
    public void Names_need_a_text_in_the_fallback_language_of_at_most_100_characters_per_culture()
    {
        var builder = new NhAssistantSubjectTypeBuilder(new NhAssistantSubjectTypeOptions(typeof(SearchableProjectResolver)));

        Assert.Throws<ArgumentException>(() => builder.Named(new() { ["nl"] = "Offerte" }, fallbackLanguage: "en"));
        Assert.Throws<ArgumentException>(() => builder.Named(new() { ["en"] = new string('x', 101) }, fallbackLanguage: "en"));
        Assert.Throws<ArgumentException>(() => builder.Named(new() { ["en"] = " " }, fallbackLanguage: "en"));
        Assert.Throws<ArgumentException>(() => builder.Named(new() { ["en_US"] = "Quote", ["en"] = "Quote" }, fallbackLanguage: "en"));
        Assert.Throws<ArgumentException>(() => builder.Named(new() { ["en"] = "Quote" }, fallbackLanguage: "e n"));
        Assert.Throws<ArgumentException>(() => builder.Named(new Dictionary<string, string>(), fallbackLanguage: "en"));
        Assert.Throws<ArgumentException>(() => builder.OpenAt("/quotes"));

        var options = new NhAssistantSubjectTypeOptions(typeof(SearchableProjectResolver));
        new NhAssistantSubjectTypeBuilder(options)
            .Named(new() { ["en"] = "Quote", ["nl-NL"] = "Offerte" }, fallbackLanguage: "en")
            .NamedPlural(new() { ["en"] = "Quotes", ["nl-NL"] = "Offertes" })
            .ForEntity<TestProjectEntity>()
            .OpenAt("/quotes/{id}");
        options.Validate();
        var type = new NhAssistantSubjectType("quote", typeof(SearchableProjectResolver), options, searchable: true);
        Assert.Equal("Offerte", type.NameIn(CultureInfo.GetCultureInfo("nl-NL")));
        // A request in "nl" finds the text of "nl-NL".
        Assert.Equal("Offertes", type.PluralNameIn(CultureInfo.GetCultureInfo("nl")));
        Assert.Equal("Quote", type.NameIn(CultureInfo.GetCultureInfo("fr-FR")));
        Assert.Equal("/quotes/Q-1", type.LinkFor("Q-1"));

        // A plural needs the fallback language as well.
        var withoutFallbackPlural = new NhAssistantSubjectTypeOptions(typeof(SearchableProjectResolver));
        new NhAssistantSubjectTypeBuilder(withoutFallbackPlural)
            .Named(new() { ["en"] = "Quote" }, fallbackLanguage: "en")
            .NamedPlural(new() { ["nl"] = "Offertes" });
        Assert.Throws<InvalidOperationException>(withoutFallbackPlural.Validate);
    }

    [Fact]
    public async Task The_search_endpoint_answers_with_the_visible_subjects_and_explains_why_it_cannot_search()
    {
        var projects = new SearchableProjects();
        projects.Add("alpha-1", "Alpha one");
        projects.Add("alpha-2", "Alpha two", hiddenFrom: "user-1");
        await using var app = await AssistantWebApplication.StartAsync(
            database,
            new NhAiScriptedChatClient(),
            configure: services =>
            {
                services.AddSingleton(projects);
                services.AddSingleton(new TestSubjects());
            },
            assistantBuilder: assistant => assistant
                .AddSubjectType<SearchableProjectResolver>(type => type
                    .Named(new() { ["en"] = "Project", ["nl"] = "Opdracht" }, fallbackLanguage: "en")
                    .OpenAt("/management/projects/{id}"))
                .AddSubjectType<TicketResolver>());
        using var client = app.CreateClient("user-1");

        var response = await client.GetAsync("/api/assistant/subjects/project/search?query=alpha&limit=10");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<NhAssistantSubjectSearchDto>(new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
        Assert.Equal("project", body.SubjectType);
        var item = Assert.Single(body.Items);
        Assert.Equal(
            new NhAssistantSubjectSearchResultDto("project", "alpha-1", "Alpha one", "global", null) { Link = "/management/projects/alpha-1" },
            item);

        var invalid = await client.GetAsync("/api/assistant/subjects/project/search?query=a");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using (var error = JsonDocument.Parse(await invalid.Content.ReadAsStringAsync()))
        {
            Assert.Equal(NhAssistantAdminErrorCodes.ValidationFailed, error.RootElement.GetProperty("code").GetString());
            Assert.Equal("invalid", error.RootElement.GetProperty("errors").GetProperty("query")[0].GetString());
        }

        await AssertErrorAsync(client, "/api/assistant/subjects/ticket/search?query=alpha", HttpStatusCode.NotFound, NhAssistantErrorCodes.SubjectSearchUnavailable);
        await AssertErrorAsync(client, "/api/assistant/subjects/order/search?query=alpha", HttpStatusCode.NotFound, NhAssistantErrorCodes.SubjectTypeUnknown);
        using var outsider = app.CreateClient("user-2", access: false);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync("/api/assistant/subjects/project/search?query=alpha")).StatusCode);
    }

    private static async Task AssertErrorAsync(HttpClient client, string path, HttpStatusCode status, string code)
    {
        var response = await client.GetAsync(path);
        Assert.Equal(status, response.StatusCode);
        using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, error.RootElement.GetProperty("code").GetString());
    }

    private async Task<AssistantTestHost> CreateHostAsync(
        AssistantTestProvider provider,
        SearchableProjects projects,
        NhAiScriptedChatClient model)
    {
        return await AssistantTestHost.CreateAsync(
            database,
            provider,
            model,
            configure: services =>
            {
                services.AddSingleton(projects);
                services.AddSingleton(new TestSubjects());
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, TestPrincipalFactory>());
            },
            assistantBuilder: assistant => assistant
                .AddSubjectType<SearchableProjectResolver>(type => type
                    .Named(new() { ["en"] = "Project", ["nl"] = "Opdracht" }, fallbackLanguage: "en")
                    .NamedPlural(new() { ["en"] = "Projects", ["nl"] = "Opdrachten" })
                    .ForEntity<TestProjectEntity>()
                    .OpenAt("/management/projects/{id}"))
                .AddSubjectType<TicketResolver>()
                .AddApprovalPolicy("project-review", policy => policy
                    .Lifetime(TimeSpan.FromDays(3))
                    .Approvers(NhAiApprovers.SubjectRole(NhAiSubjectRole.Approver))
                    .Mode(NhAiApprovalMode.Deferred)
                    .BindTo(NhAiApprovalBinding.ToolContract))
                .ConfigureAgent(AgentId, agent => agent
                    .Grant("projects.*")
                    .UseApprovalPolicy("project-review")));
    }

    private static async Task<Common.Models.TaskResult<IReadOnlyList<NhAiSubjectInfo>>> SearchAsync(
        AssistantTestHost host,
        string type,
        string query,
        int? limit,
        string actorId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NhAssistantSubjectSearchService>()
            .SearchAsync(type, query, limit, actorId, tenantId: null, CancellationToken.None);
    }

    private static async Task<NhAssistantSubjectWorkDto> GetWorkAsync(AssistantTestHost host, string subjectId, string actorId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actorId)], "test"));
        var work = await scope.ServiceProvider.GetRequiredService<INhAssistantSubjectWork>()
            .GetAsync(new NhAiSubjectRef("project", subjectId), actorId, principal);
        Assert.True(work.Success);
        return work.Data!;
    }

    private static async Task<T> InCultureAsync<T>(string culture, Func<Task<T>> action)
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            return await action();
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    private static async Task StartAndRunAsync(AssistantTestHost host, NhAiSubjectRef subject)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var info = (await services.GetRequiredService<NhAssistantSubjectAccessService>().DescribeAsync(subject, CancellationToken.None))!;
        var agent = (await services.GetRequiredService<NhAssistantAgentCatalog>().FindAsync(AgentId, false, CancellationToken.None))!;
        var thread = (await services.GetRequiredService<NhAssistantThreadService>().GetOrCreateAsync(agent, info, CancellationToken.None)).Data!;
        var run = new AssistantRun
        {
            Id = Guid.NewGuid(),
            ConversationId = thread.ConversationId,
            TriggerId = "project-created",
            AccountableOwnerId = thread.AccountableOwnerId,
            Status = NhAssistantRunStatuses.Queued,
            AgentVersion = agent.Definition.Version,
            CreatedAt = DateTimeOffset.UtcNow,
            ConcurrencyStamp = Guid.NewGuid()
        };
        await services.GetRequiredService<NhAssistantRunStore>().AddRunAsync(run, CancellationToken.None);
        var options = services.GetRequiredService<NhAssistantRegistrationState>().AgentRuns[AgentId];
        await services.GetRequiredService<INhAssistantTurnRunner>().RunAgentTurnAsync(
            new NhAssistantAgentTurnRequest(
                new NhAssistantAgentTurnContext(thread, info, options, run.Id, run.TriggerId, null, thread.AccountableOwnerId, IsRequestTurn: false),
                [new NhAssistantAgentTurnInput(Guid.NewGuid(), null, run.TriggerId, "Project PRJ-7 was created.")]),
            CancellationToken.None);
    }

    private static string? Code(Common.Models.TaskResult result)
    {
        Assert.False(result.Success);
        return result.GetResultItems().Select(item => item.Name).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
    }
}

/// <summary>
/// The entity the searchable test projects stand for.
/// </summary>
public sealed class TestProjectEntity;

/// <summary>
/// Projects the test search finds by name. Everyone in <see cref="Roles"/> has that role on every
/// project unless the project is hidden from them.
/// </summary>
public sealed class SearchableProjects
{
    private readonly List<(NhAiSubjectInfo Info, string? HiddenFrom)> _items = [];

    public ConcurrentDictionary<string, NhAiSubjectRole> Roles { get; } = new(new Dictionary<string, NhAiSubjectRole>
    {
        ["user-1"] = NhAiSubjectRole.Reader,
        ["member-1"] = NhAiSubjectRole.Participant,
        ["approver-1"] = NhAiSubjectRole.Approver
    });

    public NhAiSubjectSearchRequest? LastRequest { get; set; }

    /// <summary>How many results the platform read from the last search.</summary>
    public int Pulled { get; set; }

    public void Add(string id, string name, string? hiddenFrom = null, string? tenantId = null)
    {
        var subject = new NhAiSubjectRef("project", id);
        var scopeKey = NhAiSubjectInfo.CreateScopeKey(tenantId, null);
        _items.Add((new NhAiSubjectInfo(subject, scopeKey) { DisplayName = name, TenantId = tenantId, Version = "1" }, hiddenFrom));
    }

    public void Repeat(string id)
    {
        _items.Add(_items.First(item => item.Info.Subject.Id == id));
    }

    public NhAiSubjectInfo? Find(NhAiSubjectRef subject)
    {
        return _items.FirstOrDefault(item => item.Info.Subject == subject).Info;
    }

    public bool IsHidden(NhAiSubjectRef subject, string actorId)
    {
        return _items.Any(item => item.Info.Subject == subject && item.HiddenFrom == actorId);
    }

    public IEnumerable<NhAiSubjectInfo> Matching(string query)
    {
        return _items
            .Where(item => item.Info.DisplayName!.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Info);
    }
}

public sealed class SearchableProjectResolver(SearchableProjects projects) : INhAiSubjectAccessResolver, INhAiSubjectSearch
{
    public string SubjectType => "project";

    public ValueTask<NhAiSubjectInfo?> DescribeAsync(NhAiSubjectRef subject, CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(projects.Find(subject));
    }

    public ValueTask<NhAiSubjectAccess> ResolveAsync(NhAiSubjectRef subject, string actorId, CancellationToken cancellationToken = default)
    {
        if (projects.Find(subject) is null || projects.IsHidden(subject, actorId) || !projects.Roles.TryGetValue(actorId, out var role))
        {
            return ValueTask.FromResult(NhAiSubjectAccess.None);
        }

        return ValueTask.FromResult(new NhAiSubjectAccess(role));
    }

    public async IAsyncEnumerable<NhAiSubjectMember> ListMembersAsync(
        NhAiSubjectRef subject,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var (actorId, role) in projects.Roles)
        {
            await Task.Yield();
            yield return new NhAiSubjectMember(actorId, role) { DisplayName = actorId };
        }
    }

    public async IAsyncEnumerable<NhAiSubjectInfo> SearchAsync(
        NhAiSubjectSearchRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        projects.LastRequest = request;
        projects.Pulled = 0;
        foreach (var info in projects.Matching(request.Query))
        {
            await Task.Yield();
            projects.Pulled++;
            yield return info;
        }
    }
}

/// <summary>
/// A subject type without search: people give a ticket's id.
/// </summary>
public sealed class TicketResolver : INhAiSubjectAccessResolver
{
    public string SubjectType => "ticket";

    public ValueTask<NhAiSubjectInfo?> DescribeAsync(NhAiSubjectRef subject, CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult<NhAiSubjectInfo?>(new NhAiSubjectInfo(subject, "global") { DisplayName = "Ticket " + subject.Id });
    }

    public ValueTask<NhAiSubjectAccess> ResolveAsync(NhAiSubjectRef subject, string actorId, CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(new NhAiSubjectAccess(NhAiSubjectRole.Reader));
    }

    public async IAsyncEnumerable<NhAiSubjectMember> ListMembersAsync(
        NhAiSubjectRef subject,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield break;
    }
}
