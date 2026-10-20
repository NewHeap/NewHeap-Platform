using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NSubstitute;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// Results and history (W6 of the plan for agents working on application objects, K13 and K14): a run
/// keeps its whole answer, bounded, next to the summary for memory; people see the runs of the objects
/// they read and the runs for the whole application whose view policy they pass; administrators see
/// every run of an agent; histories page and filter in the database, on both relational providers.
/// </summary>
public sealed class AssistantRunResultTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string ProjectAgent = "project-assistant";
    private const string ManagerAgent = "project-manager";
    private const string Statistics = "weekly-statistics";
    private const string ViewPolicy = "app.results.view";
    private const string Admin = "admin-1";
    private const string Reader = "reader-1";
    private const string Member = "member-1";
    private const string Viewer = "viewer-1";
    private const string Manager = "manager-1";
    private const string Stranger = "stranger-1";

    [Fact]
    public void A_result_appends_the_answers_of_a_runs_turns_within_its_bound()
    {
        Assert.Equal(("First.", false), NhAssistantRunResults.Append(null, false, "  First.  "));
        Assert.Equal(("First." + NhAssistantRunResults.TurnSeparator + "Second.", false), NhAssistantRunResults.Append("First.", false, "Second."));
        Assert.Equal(("First.", false), NhAssistantRunResults.Append("First.", false, "   "));

        // A longer answer keeps its beginning, never half a character, and nothing follows a cut.
        var almost = new string('a', NhAssistantLimits.MaxRunResultCharacters - 1);
        var (cut, truncated) = NhAssistantRunResults.Append(null, false, almost + "\U0001F600tail");
        Assert.True(truncated);
        Assert.Equal(almost, cut);
        Assert.Equal((cut, true), NhAssistantRunResults.Append(cut, true, "More."));

        // The preview of a listing is the beginning of the stored answer; without one, the summary.
        var run = new AssistantRun { Summary = "2026-10-20 07:00 UTC, trigger manual, completed.", Result = new string('r', 400) };
        Assert.Equal((new string('r', NhAssistantRunResults.MaxPreviewCharacters), true), NhAssistantRunResults.PreviewOf(run, English));
        run.Result = null;
        Assert.Equal((run.Summary, false), NhAssistantRunResults.PreviewOf(run, English));
    }

    [Fact]
    public void A_result_the_rules_wrote_per_culture_reads_in_the_readers_language()
    {
        // A model's answer stays in one language: nothing per culture.
        Assert.Null(NhAssistantRunResults.AppendLocalized(null, null, false, "A model's answer.", null));

        // A rule turn answers per culture; the run's own culture (Dutch here) is one of them.
        var localized = NhAssistantRunResults.AppendLocalized(
            null,
            null,
            false,
            "Er zijn **5** projecten open.",
            new Dictionary<string, string> { ["nl-NL"] = "Er zijn **5** projecten open.", ["en-US"] = "**5** projects are open." });
        var run = new AssistantRun { Summary = "Ended completed.", Result = "Er zijn **5** projecten open.", ResultsJson = localized };

        // The exact culture, a parent or another culture of the same language, else the run's own culture.
        Assert.Equal(("**5** projects are open.", false), NhAssistantRunResults.ResultOf(run, English));
        Assert.Equal(("**5** projects are open.", false), NhAssistantRunResults.ResultOf(run, CultureInfo.GetCultureInfo("en")));
        Assert.Equal(("**5** projects are open.", false), NhAssistantRunResults.ResultOf(run, CultureInfo.GetCultureInfo("en-GB")));
        Assert.Equal(("Er zijn **5** projecten open.", false), NhAssistantRunResults.ResultOf(run, Dutch));
        Assert.Equal(("Er zijn **5** projecten open.", false), NhAssistantRunResults.ResultOf(run, CultureInfo.GetCultureInfo("de-DE")));
        Assert.Equal(("**5** projects are open.", false), NhAssistantRunResults.PreviewOf(run, English));
        // A result without results per culture, or with unreadable ones, reads as stored.
        Assert.Equal(("Er zijn **5** projecten open.", false), NhAssistantRunResults.ResultOf(new AssistantRun { Result = run.Result, ResultsJson = "not json" }, English));

        // A later model turn adds the same answer to every culture; a culture the rules left out of a
        // turn starts from the result so far and takes the run's own answer.
        var withModel = NhAssistantRunResults.AppendLocalized(localized, run.Result, false, "Ik heb ook naar PRJ-1 gekeken.", null);
        run.ResultsJson = withModel;
        Assert.Equal("**5** projects are open." + NhAssistantRunResults.TurnSeparator + "Ik heb ook naar PRJ-1 gekeken.", NhAssistantRunResults.ResultOf(run, English).Text);
        var withGerman = NhAssistantRunResults.AppendLocalized(
            localized,
            run.Result,
            false,
            "Nog een regel.",
            new Dictionary<string, string> { ["de-DE"] = "Noch eine Regel.", ["en-US"] = "One more rule." });
        run.ResultsJson = withGerman;
        Assert.Equal("**5** projects are open." + NhAssistantRunResults.TurnSeparator + "One more rule.", NhAssistantRunResults.ResultOf(run, English).Text);
        Assert.Equal("Er zijn **5** projecten open." + NhAssistantRunResults.TurnSeparator + "Nog een regel.", NhAssistantRunResults.ResultOf(run, Dutch).Text);
        Assert.Equal("Er zijn **5** projecten open." + NhAssistantRunResults.TurnSeparator + "Noch eine Regel.", NhAssistantRunResults.ResultOf(run, CultureInfo.GetCultureInfo("de-DE")).Text);

        // Every culture is bounded like the result, and a culture that was cut takes nothing more.
        var cut = NhAssistantRunResults.AppendLocalized(
            null,
            null,
            false,
            "Kort.",
            new Dictionary<string, string> { ["nl-NL"] = "Kort.", ["en-US"] = new string('e', NhAssistantLimits.MaxRunResultCharacters + 10) });
        run.ResultsJson = NhAssistantRunResults.AppendLocalized(cut, "Kort.", false, "Meer.", null);
        Assert.Equal((new string('e', NhAssistantLimits.MaxRunResultCharacters), true), NhAssistantRunResults.ResultOf(run, English));
        Assert.Equal(("Kort." + NhAssistantRunResults.TurnSeparator + "Meer.", false), NhAssistantRunResults.ResultOf(run, Dutch));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_rule_runs_result_reads_in_each_readers_language_and_a_models_answer_as_written(AssistantTestProvider provider)
    {
        // The application works in Dutch and English; runs work in Dutch, its default culture.
        await using var app = await StartAsync(provider, new NhAiScriptedChatClient().RespondWithText("Een antwoord van het model."), twoLanguages: true);
        app.Services.GetRequiredService<TestRuleScript>().Run = (_, _) => Task.FromResult(NhAssistantRuleResult.Done(culture =>
            culture.TwoLetterISOLanguageName == "nl" ? "Er zijn **5** projecten open." : "**5** projects are open."));

        var receipt = await StartNowAsync(app, Statistics, NhAiSystemSubjects.Global, Admin, "Count now.");
        await ExecuteRunAsync(app, receipt.RunId!.Value);

        // The run keeps its result in its own culture, which its thread and memory use, and per culture.
        var run = (await WithRunsAsync(app, runs => runs.FindRunAsync(receipt.RunId!.Value, CancellationToken.None)))!;
        Assert.Equal((NhAssistantRunStatuses.Completed, NhAssistantRunKinds.Rule), (run.Status, run.Kind));
        Assert.Equal("Er zijn **5** projecten open.", run.Result);
        Assert.Contains("Answer: Er zijn **5** projecten open.", run.Summary, StringComparison.Ordinal);
        Assert.Equal(("**5** projects are open.", false), NhAssistantRunResults.ResultOf(run, English));

        // Every screen that shows the result reads it in the reader's language: the run's result, the
        // run history, the administration's runs view (and the flow view), and the agents of the object.
        using var admin = app.CreateClient(Admin, admin: true);
        foreach (var (language, expected) in new[]
        {
            ("en-US", "**5** projects are open."),
            ("en", "**5** projects are open."),
            ("nl-NL", "Er zijn **5** projecten open.")
        })
        {
            admin.DefaultRequestHeaders.AcceptLanguage.Clear();
            admin.DefaultRequestHeaders.AcceptLanguage.ParseAdd(language);
            var result = await ReadJsonAsync(await admin.GetAsync($"/api/assistant/runs/{run.Id}"));
            Assert.Equal((language, expected), (language, result.GetProperty("result").GetString()));
            Assert.Equal((language, expected), (language, result.GetProperty("run").GetProperty("result").GetString()));
            var history = (await ReadJsonAsync(await admin.GetAsync($"/api/assistant/runs?agentId={Statistics}"))).GetProperty("items")[0];
            Assert.Equal((language, expected), (language, history.GetProperty("result").GetString()));
            var runsView = (await ReadJsonAsync(await admin.GetAsync($"/api/assistant/admin/agents/{Statistics}/runs"))).GetProperty("items")[0];
            Assert.Equal((language, expected), (language, runsView.GetProperty("result").GetString()));
            var agents = await ReadJsonAsync(await admin.GetAsync("/api/assistant/subjects/nh-system/global/agents"));
            var latest = agents.GetProperty("agents").EnumerateArray().Single(agent => agent.GetProperty("agentId").GetString() == Statistics).GetProperty("latestRun");
            Assert.Equal((language, expected), (language, latest.GetProperty("result").GetString()));
        }

        // A model's answer stays as the model wrote it, for every reader.
        var answered = await StartNowAsync(app, ProjectAgent, NhAiSystemSubjects.Global, Admin, "Wat is er open?");
        await ExecuteRunAsync(app, answered.RunId!.Value);
        admin.DefaultRequestHeaders.AcceptLanguage.Clear();
        admin.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US");
        var modelResult = await ReadJsonAsync(await admin.GetAsync($"/api/assistant/runs/{answered.RunId}"));
        Assert.Equal("Een antwoord van het model.", modelResult.GetProperty("result").GetString());
        Assert.Null((await WithRunsAsync(app, runs => runs.FindRunAsync(answered.RunId!.Value, CancellationToken.None)))!.ResultsJson);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_run_keeps_its_whole_answer_as_its_result_next_to_the_summary_for_memory(AssistantTestProvider provider)
    {
        // The model answers with Markdown longer than a result keeps.
        var answer = "# Weekly review\n\n" + string.Concat(Enumerable.Repeat("- Project PRJ-1 is on track.\n", 1_000));
        var model = new NhAiScriptedChatClient()
            .RespondWithText(answer)
            .RespondWithText("A first answer.")
            .RespondWithText("A second answer.");
        await using var app = await StartAsync(provider, model);
        var project = AddProject(app, (Member, NhAiSubjectRole.Participant), (Reader, NhAiSubjectRole.Reader));

        // A run by hand, executed by its background operation.
        var receipt = await StartNowAsync(app, ProjectAgent, Project(project), Member, "Review the project.");
        await ExecuteRunAsync(app, receipt.RunId!.Value);
        var run = (await WithRunsAsync(app, runs => runs.FindRunAsync(receipt.RunId!.Value, CancellationToken.None)))!;
        Assert.Equal(NhAssistantRunStatuses.Completed, run.Status);
        Assert.True(run.ResultTruncated);
        Assert.Equal(NhAssistantLimits.MaxRunResultCharacters, run.Result!.Length);
        Assert.StartsWith("# Weekly review\n\n- Project PRJ-1 is on track.", run.Result, StringComparison.Ordinal);
        // The summary for memory stays small, with the beginning of the answer on one line.
        Assert.Contains("Answer: # Weekly review", run.Summary, StringComparison.Ordinal);
        Assert.True(run.Summary!.Length < 1_000);

        // People read the whole result; a listing shows its beginning.
        using var reader = app.CreateClient(Reader);
        var result = await ReadJsonAsync(await reader.GetAsync($"/api/assistant/runs/{run.Id}"));
        Assert.Equal(run.Result, result.GetProperty("result").GetString());
        Assert.True(result.GetProperty("resultTruncated").GetBoolean());
        Assert.Equal(run.Summary, result.GetProperty("summary").GetString());
        Assert.Equal(run.Result[..NhAssistantRunResults.MaxPreviewCharacters], result.GetProperty("run").GetProperty("result").GetString());
        Assert.True(result.GetProperty("canOpenConversation").GetBoolean());
        var agents = await ReadJsonAsync(await reader.GetAsync($"/api/assistant/subjects/project/{project}/agents"));
        var latest = agents.GetProperty("agents").EnumerateArray().Single(agent => agent.GetProperty("agentId").GetString() == ProjectAgent).GetProperty("latestRun");
        Assert.Equal(run.Result[..NhAssistantRunResults.MaxPreviewCharacters], latest.GetProperty("result").GetString());
        Assert.True(latest.GetProperty("resultTruncated").GetBoolean());

        // A run of two turns keeps both answers, in order.
        var twoTurns = await StartRunAsync(app, ProjectAgent, Project(project));
        var first = await RunTurnAsync(app, twoTurns, "Have a look.");
        var second = await RunTurnAsync(app, twoTurns, "And once more.");
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var writer = scope.ServiceProvider.GetRequiredService<NhAssistantRunResultWriter>();
            await writer.AppendTurnAsync(twoTurns.Run.Id, twoTurns.Thread.ConversationId, first.TurnId!.Value, null, CancellationToken.None);
            await writer.AppendTurnAsync(twoTurns.Run.Id, twoTurns.Thread.ConversationId, second.TurnId!.Value, null, CancellationToken.None);
        }
        var kept = (await WithRunsAsync(app, runs => runs.FindRunAsync(twoTurns.Run.Id, CancellationToken.None)))!;
        Assert.Equal(("A first answer." + NhAssistantRunResults.TurnSeparator + "A second answer.", false), (kept.Result, kept.ResultTruncated));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_rule_runs_result_is_the_summary_its_rules_wrote(AssistantTestProvider provider)
    {
        await using var app = await StartAsync(provider, new NhAiScriptedChatClient());
        app.Services.GetRequiredService<TestRuleScript>().Run = (_, _) =>
            Task.FromResult(NhAssistantRuleResult.Done("**5** projects are open; proposed completing PRJ-1 and PRJ-2."));

        var receipt = await StartNowAsync(app, Statistics, NhAiSystemSubjects.Global, Admin, "Count now.");
        await ExecuteRunAsync(app, receipt.RunId!.Value);

        var run = (await WithRunsAsync(app, runs => runs.FindRunAsync(receipt.RunId!.Value, CancellationToken.None)))!;
        Assert.Equal((NhAssistantRunStatuses.Completed, NhAssistantRunKinds.Rule), (run.Status, run.Kind));
        Assert.Equal(("**5** projects are open; proposed completing PRJ-1 and PRJ-2.", false), (run.Result, run.ResultTruncated));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task People_see_the_runs_of_objects_they_read_and_of_the_whole_application_by_the_view_policy(AssistantTestProvider provider)
    {
        await using var app = await StartAsync(provider, new NhAiScriptedChatClient());
        var readable = AddProject(app, (Reader, NhAiSubjectRole.Reader), (Member, NhAiSubjectRole.Participant), (Admin, NhAiSubjectRole.Reader));
        var other = AddProject(app, (Member, NhAiSubjectRole.Participant));
        var now = DateTimeOffset.UtcNow;
        var onReadable = await SeedRunAsync(app, ProjectAgent, Project(readable), now.AddMinutes(-50), "Readable project answer.");
        var onOther = await SeedRunAsync(app, ProjectAgent, Project(other), now.AddMinutes(-40), "Other project answer.");
        var shadow = await SeedRunAsync(app, ProjectAgent, Project(readable), now.AddMinutes(-30), "Shadow answer.", shadow: true);
        var viewed = await SeedRunAsync(app, ProjectAgent, NhAiSystemSubjects.Global, now.AddMinutes(-20), "**3** projects are open.");
        var open = await SeedRunAsync(app, Statistics, NhAiSystemSubjects.Global, now.AddMinutes(-10), "Statistics for everyone.");
        var managed = await SeedRunAsync(app, ManagerAgent, NhAiSystemSubjects.Global, now.AddMinutes(-5), "For project managers.");

        // An object's readers see its runs; the whole application's runs follow the agent's view policy,
        // by default its required policy, and an agent that names neither shows them to everyone.
        Assert.Equal([open, onReadable], await ListIdsAsync(app.CreateClient(Reader)));
        Assert.Equal([open, onOther, onReadable], await ListIdsAsync(app.CreateClient(Member)));
        Assert.Equal([open, viewed], await ListIdsAsync(app.CreateClient(Viewer)));
        Assert.Equal([managed, open], await ListIdsAsync(app.CreateClient(Manager)));
        // Someone without an account that may sign in sees nothing of the whole application.
        Assert.Empty(await ListIdsAsync(app.CreateClient(Stranger)));
        // Administrators see shadow work in people's history as well, of the objects they read; the
        // other rules stay.
        Assert.Equal([open], await ListIdsAsync(app.CreateClient(Admin, admin: true), "?subjectType=nh-system"));
        Assert.Equal([shadow], await ListIdsAsync(app.CreateClient(Admin, admin: true), $"?subjectType=project&subjectId={readable}&agentId={ProjectAgent}", asAdminReader: true));

        // One run with its result follows the same rule; a run someone may not see is not found.
        using var viewer = app.CreateClient(Viewer);
        viewer.DefaultRequestHeaders.AcceptLanguage.ParseAdd("nl-NL");
        var result = await ReadJsonAsync(await viewer.GetAsync($"/api/assistant/runs/{viewed}"));
        Assert.Equal("**3** projects are open.", result.GetProperty("result").GetString());
        Assert.Equal((ProjectAgent, NhAiSubjectRef.SystemType, NhAiSystemSubjects.GlobalId), (
            result.GetProperty("run").GetProperty("agentId").GetString(),
            result.GetProperty("run").GetProperty("subjectType").GetString(),
            result.GetProperty("run").GetProperty("subjectId").GetString()));
        Assert.Equal("De hele applicatie", result.GetProperty("run").GetProperty("subjectDisplayName").GetString());
        using var reader = app.CreateClient(Reader);
        await AssertNotFoundAsync(reader, $"/api/assistant/runs/{viewed}");
        await AssertNotFoundAsync(reader, $"/api/assistant/runs/{onOther}");
        await AssertNotFoundAsync(reader, $"/api/assistant/runs/{shadow}");
        await AssertNotFoundAsync(reader, $"/api/assistant/runs/{Guid.NewGuid()}");
        Assert.Equal("Readable project answer.", (await ReadJsonAsync(await reader.GetAsync($"/api/assistant/runs/{onReadable}"))).GetProperty("result").GetString());

        // Administrators see every run of an agent in its runs view, shadow work marked, and open any run.
        using var admin = app.CreateClient(Admin, admin: true);
        var view = await ReadJsonAsync(await admin.GetAsync($"/api/assistant/admin/agents/{ProjectAgent}/runs"));
        Assert.Equal(4, view.GetProperty("total").GetInt32());
        Assert.False(view.GetProperty("limited").GetBoolean());
        var items = view.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal([viewed, shadow, onOther, onReadable], items.Select(item => item.GetProperty("id").GetGuid()).ToArray());
        Assert.Equal([false, true, false, false], items.Select(item => item.GetProperty("shadow").GetBoolean()).ToArray());
        var first = items[0];
        Assert.Equal(("manual", NhAssistantRunStatuses.Completed, 2, 150L), (
            first.GetProperty("triggerId").GetString(),
            first.GetProperty("status").GetString(),
            first.GetProperty("toolCalls").GetInt32(),
            first.GetProperty("inputTokens").GetInt64() + first.GetProperty("outputTokens").GetInt64()));
        Assert.Equal("Project", items[2].GetProperty("subjectTypeName").GetString());
        Assert.Equal("Shadow answer.", (await ReadJsonAsync(await admin.GetAsync($"/api/assistant/runs/{shadow}"))).GetProperty("result").GetString());
        Assert.Equal("For project managers.", (await ReadJsonAsync(await admin.GetAsync($"/api/assistant/runs/{managed}"))).GetProperty("result").GetString());

        // The runs view is for administrators only.
        using (var refused = await reader.GetAsync($"/api/assistant/admin/agents/{ProjectAgent}/runs"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }
        await AssertNotFoundAsync(admin, "/api/assistant/admin/agents/unknown-agent/runs");
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Run_histories_page_and_filter_by_object_status_and_period(AssistantTestProvider provider)
    {
        await using var app = await StartAsync(provider, new NhAiScriptedChatClient());
        var first = AddProject(app, (Member, NhAiSubjectRole.Participant));
        var second = AddProject(app, (Member, NhAiSubjectRole.Participant));
        var now = DateTimeOffset.UtcNow;
        var oldest = await SeedRunAsync(app, ProjectAgent, Project(first), now.AddDays(-3), "Three days ago.");
        var failed = await SeedRunAsync(app, ProjectAgent, Project(first), now.AddDays(-2), null, NhAssistantRunStatuses.Failed);
        var onSecond = await SeedRunAsync(app, ProjectAgent, Project(second), now.AddDays(-1), "Yesterday.");
        var application = await SeedRunAsync(app, Statistics, NhAiSystemSubjects.Global, now.AddHours(-1), "This morning.");
        using var member = app.CreateClient(Member);

        Assert.Equal([application, onSecond, failed, oldest], await ListIdsAsync(member));
        Assert.Equal([onSecond, failed, oldest], await ListIdsAsync(member, $"?agentId={ProjectAgent}"));
        Assert.Equal([application], await ListIdsAsync(member, "?subjectType=nh-system"));
        Assert.Equal([failed, oldest], await ListIdsAsync(member, $"?subjectType=project&subjectId={first}"));
        Assert.Equal([failed], await ListIdsAsync(member, "?status=failed"));
        var from = Uri.EscapeDataString(now.AddDays(-2).AddHours(-1).ToString("O"));
        var to = Uri.EscapeDataString(now.AddHours(-2).ToString("O"));
        Assert.Equal([onSecond, failed], await ListIdsAsync(member, $"?from={from}&to={to}"));

        // Pages of two, with the total of what the person may see.
        var page = await ReadJsonAsync(await member.GetAsync("/api/assistant/runs?page=2&itemsPerPage=2"));
        Assert.Equal(4, page.GetProperty("total").GetInt32());
        Assert.Equal([failed, oldest], page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ToArray());

        // A failed run without an answer lists the summary it ended with.
        var failedItem = (await ReadJsonAsync(await member.GetAsync("/api/assistant/runs?status=failed"))).GetProperty("items")[0];
        Assert.Equal("Ended failed.", failedItem.GetProperty("result").GetString());

        // The administration's runs view pages and filters the same way.
        using var admin = app.CreateClient(Admin, admin: true);
        var adminPage = await ReadJsonAsync(await admin.GetAsync($"/api/assistant/admin/agents/{ProjectAgent}/runs?subjectType=project&subjectId={first}&itemsPerPage=1"));
        Assert.Equal(2, adminPage.GetProperty("total").GetInt32());
        Assert.Equal(failed, Assert.Single(adminPage.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());

        // Invalid filters are refused with one code and the parameter that is wrong.
        foreach (var (query, field, error) in new[]
        {
            ("?status=finished", "status", NhAssistantFieldErrors.Invalid),
            ($"?subjectId={first}", "subjectType", NhAssistantFieldErrors.Required),
            ("?subjectType=Not_A_Type", "subjectType", NhAssistantFieldErrors.Invalid),
            ($"?from={to}&to={from}", "to", NhAssistantFieldErrors.Invalid),
            ("?page=0", "page", NhAssistantFieldErrors.Invalid),
            ("?itemsPerPage=51", "itemsPerPage", NhAssistantFieldErrors.Invalid)
        })
        {
            using var refused = await member.GetAsync("/api/assistant/runs" + query);
            var body = await ReadJsonAsync(refused, HttpStatusCode.BadRequest);
            Assert.Equal(NhAssistantErrorCodes.RunFilterInvalid, body.GetProperty("code").GetString());
            Assert.Equal(error, Assert.Single(body.GetProperty("errors").GetProperty(field).EnumerateArray()).GetString());
            // The administration checks the filter before the agent, so an unknown agent reads 400 too.
            using var adminRefused = await admin.GetAsync("/api/assistant/admin/agents/unknown-agent/runs" + query);
            Assert.Equal(field, Assert.Single((await ReadJsonAsync(adminRefused, HttpStatusCode.BadRequest)).GetProperty("errors").EnumerateObject()).Name);
        }
        using (var unknownAgent = await admin.GetAsync("/api/assistant/admin/agents/unknown-agent/runs"))
        {
            Assert.Equal(NhAssistantErrorCodes.AgentNotFound, (await ReadJsonAsync(unknownAgent, HttpStatusCode.NotFound)).GetProperty("code").GetString());
        }

        // An unknown run and an unknown file of a known run answer their own codes.
        using (var unknownRun = await member.GetAsync($"/api/assistant/runs/{Guid.NewGuid()}"))
        {
            Assert.Equal(NhAssistantErrorCodes.RunNotFound, (await ReadJsonAsync(unknownRun, HttpStatusCode.NotFound)).GetProperty("code").GetString());
        }
        using (var unknownFile = await member.GetAsync($"/api/assistant/runs/{oldest}/artifacts/{Guid.NewGuid()}/content"))
        {
            Assert.Equal(NhAssistantErrorCodes.ArtifactNotFound, (await ReadJsonAsync(unknownFile, HttpStatusCode.NotFound)).GetProperty("code").GetString());
        }
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_runs_result_lists_its_proposals_those_placed_elsewhere_by_access_and_its_files(AssistantTestProvider provider)
    {
        await using var app = await StartAsync(provider, new NhAiScriptedChatClient());
        var readable = AddProject(app, (Reader, NhAiSubjectRole.Reader));
        var other = AddProject(app, (Member, NhAiSubjectRole.Participant));
        var runId = await SeedRunAsync(app, Statistics, NhAiSystemSubjects.Global, DateTimeOffset.UtcNow.AddMinutes(-5), "Proposed to complete two projects.");
        var run = (await WithRunsAsync(app, runs => runs.FindRunWithThreadAsync(runId, CancellationToken.None)))!;
        var readableThread = await ThreadAtAsync(app, Statistics, Project(readable));
        var otherThread = await ThreadAtAsync(app, Statistics, Project(other));
        var own = await AddProposalAsync(app, run.Thread.ConversationId, runId, null, DateTimeOffset.UtcNow.AddMinutes(-4));
        var placedReadable = await AddProposalAsync(app, readableThread.ConversationId, runId, run.Thread.ConversationId, DateTimeOffset.UtcNow.AddMinutes(-3));
        var placedOther = await AddProposalAsync(app, otherThread.ConversationId, runId, run.Thread.ConversationId, DateTimeOffset.UtcNow.AddMinutes(-2));
        var file = await AddArtifactAsync(app, run.Thread.ConversationId, runId, "weekly-report", "Five projects are open.");
        var otherRunsFile = await AddArtifactAsync(app, run.Thread.ConversationId, null, "older-report", "Older.");

        // A reader of one of the projects sees the run's own proposal and the one placed at that
        // project; the other placed one is counted, not shown.
        using var reader = app.CreateClient(Reader);
        var result = await ReadJsonAsync(await reader.GetAsync($"/api/assistant/runs/{runId}"));
        var proposals = result.GetProperty("proposals").EnumerateArray().ToArray();
        Assert.Equal([own, placedReadable], proposals.Select(proposal => proposal.GetProperty("approvalId").GetGuid()).ToArray());
        Assert.Equal(readable.ToString(), proposals[1].GetProperty("subjectId").GetString());
        Assert.Equal(run.Thread.ConversationId, proposals[1].GetProperty("placedBy").GetProperty("conversationId").GetGuid());
        Assert.Equal(1, result.GetProperty("hiddenProposals").GetInt32());
        // The run's files only, downloadable by whoever sees the run.
        var artifact = Assert.Single(result.GetProperty("artifacts").EnumerateArray());
        Assert.Equal((file, "weekly-report", false), (artifact.GetProperty("id").GetGuid(), artifact.GetProperty("name").GetString(), artifact.GetProperty("restricted").GetBoolean()));
        using (var download = await reader.GetAsync($"/api/assistant/runs/{runId}/artifacts/{file}/content"))
        {
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            Assert.Equal("Five projects are open.", await download.Content.ReadAsStringAsync());
        }
        await AssertNotFoundAsync(reader, $"/api/assistant/runs/{runId}/artifacts/{otherRunsFile}/content");
        // A person who may not see the run gets none of its files.
        await AssertNotFoundAsync(app.CreateClient(Stranger), $"/api/assistant/runs/{runId}/artifacts/{file}/content");

        // Administrators see every proposal of the run.
        using var admin = app.CreateClient(Admin, admin: true);
        var asAdmin = await ReadJsonAsync(await admin.GetAsync($"/api/assistant/runs/{runId}"));
        Assert.Equal([own, placedReadable, placedOther], asAdmin.GetProperty("proposals").EnumerateArray().Select(proposal => proposal.GetProperty("approvalId").GetGuid()).ToArray());
        Assert.Equal(0, asAdmin.GetProperty("hiddenProposals").GetInt32());
    }

    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private static readonly CultureInfo Dutch = CultureInfo.GetCultureInfo("nl-NL");

    private Task<AssistantWebApplication> StartAsync(AssistantTestProvider provider, NhAiScriptedChatClient model, bool twoLanguages = false)
    {
        return AssistantWebApplication.StartAsync(
            database,
            model,
            configure: services =>
            {
                if (twoLanguages)
                {
                    // Runs work in the default culture, Dutch; people read in either language.
                    services.Configure<RequestLocalizationOptions>(options =>
                    {
                        options.DefaultRequestCulture = new RequestCulture(Dutch);
                        options.SupportedCultures = [Dutch, English];
                        options.SupportedUICultures = [Dutch, English];
                    });
                }
                services.AddSingleton<ResultProjects>();
                services.AddSingleton<TestRuleScript>();
                services.AddSingleton<RecordingOperations>();
                services.AddSingleton(provider => provider.GetRequiredService<RecordingOperations>().Service);
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, ResultPrincipals>());
                services.AddAuthorization(options => options.AddPolicy(ViewPolicy, policy => policy.RequireClaim("permission", ViewPolicy)));
                // Runs are executed here, not by background operations.
                services.Remove(services.Single(descriptor => descriptor.ImplementationType == typeof(NhAssistantStartupValidator)));
            },
            assistantBuilder: assistant => assistant
                .AddSubjectType<ResultProjectResolver>(type => type.Named(new() { ["en"] = "Project" }, fallbackLanguage: "en"))
                .AddAgent(AssistantTestData.Agent(Statistics))
                .ConfigureAgent(ProjectAgent, run => run.WorksOn("project").WorksOnApplication().ViewPolicy(ViewPolicy))
                .ConfigureAgent(ManagerAgent, run => run.WorksOnApplication())
                .ConfigureAgent(Statistics, run => run.WorksOn("project").WorksOnApplication().UseRuleHandler<TestRules>()),
            provider: provider);
    }

    private static Guid AddProject(AssistantWebApplication app, params (string ActorId, NhAiSubjectRole Role)[] roles)
    {
        var id = Guid.NewGuid();
        app.Services.GetRequiredService<ResultProjects>().Roles[id.ToString()] = roles.ToDictionary(role => role.ActorId, role => role.Role, StringComparer.Ordinal);
        return id;
    }

    private static NhAiSubjectRef Project(Guid id)
    {
        return new NhAiSubjectRef("project", id.ToString());
    }

    private static async Task<NhAssistantTriggerReceipt> StartNowAsync(
        AssistantWebApplication app,
        string agentId,
        NhAiSubjectRef subject,
        string actorId,
        string message)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var receipt = await scope.ServiceProvider.GetRequiredService<NhAssistantAgentTriggers>()
            .StartNowAsync(agentId, subject, actorId, message, CancellationToken.None);
        Assert.Equal(NhAssistantTriggerOutcomes.Started, receipt.Outcome);
        return receipt;
    }

    /// <summary>
    /// Executes the run's background operation once, as the background-operation worker does.
    /// </summary>
    private static async Task ExecuteRunAsync(AssistantWebApplication app, Guid runId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>().HttpContext = null;
        var operation = ActivatorUtilities.CreateInstance<NhAssistantAgentRunOperation>(scope.ServiceProvider);
        var result = await operation.ExecuteAsync(
            new NhAssistantAgentRunRequest(runId),
            Substitute.For<INhBackgroundOperationContext>(),
            CancellationToken.None);
        Assert.True(result.Success);
    }

    private sealed record StartedRun(AssistantThread Thread, AssistantRun Run, NhAssistantAgentTurnContext AgentTurn);

    private static async Task<StartedRun> StartRunAsync(AssistantWebApplication app, string agentId, NhAiSubjectRef subject)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var thread = await ThreadAtAsync(app, agentId, subject);
        var info = (await services.GetRequiredService<NhAssistantSubjectAccessService>().DescribeAsync(subject, CancellationToken.None))!;
        var run = new AssistantRun
        {
            Id = Guid.NewGuid(),
            ConversationId = thread.ConversationId,
            TriggerId = NhAssistantTriggerIds.Manual,
            AccountableOwnerId = thread.AccountableOwnerId,
            Status = NhAssistantRunStatuses.Running,
            AgentVersion = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            ConcurrencyStamp = Guid.NewGuid()
        };
        await services.GetRequiredService<NhAssistantRunStore>().AddRunAsync(run, CancellationToken.None);
        var options = services.GetRequiredService<NhAssistantRegistrationState>().AgentRuns[agentId];
        var agentTurn = new NhAssistantAgentTurnContext(thread, info, options, run.Id, run.TriggerId, null, thread.AccountableOwnerId, IsRequestTurn: false);
        return new StartedRun(thread, run, agentTurn);
    }

    private static async Task<NhAssistantAgentTurnResult> RunTurnAsync(AssistantWebApplication app, StartedRun run, string message)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>().HttpContext = null;
        var result = await scope.ServiceProvider.GetRequiredService<INhAssistantTurnRunner>().RunAgentTurnAsync(
            new NhAssistantAgentTurnRequest(run.AgentTurn, [new NhAssistantAgentTurnInput(Guid.NewGuid(), null, run.Run.TriggerId, message)]),
            CancellationToken.None);
        Assert.Equal(NhAssistantTurnStatuses.Completed, result.Outcome);
        return result;
    }

    private static async Task<AssistantThread> ThreadAtAsync(AssistantWebApplication app, string agentId, NhAiSubjectRef subject, bool shadow = false)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var info = (await services.GetRequiredService<NhAssistantSubjectAccessService>().DescribeAsync(subject, CancellationToken.None))!;
        var agent = (await services.GetRequiredService<NhAssistantAgentCatalog>().FindAsync(agentId, includeDisabled: false, CancellationToken.None))!;
        return (await services.GetRequiredService<NhAssistantThreadService>().GetOrCreateAsync(agent, info, CancellationToken.None, shadow)).Data!;
    }

    /// <summary>
    /// Stores an ended run of the agent on the subject, as a run that answered <paramref name="result"/>.
    /// </summary>
    private static async Task<Guid> SeedRunAsync(
        AssistantWebApplication app,
        string agentId,
        NhAiSubjectRef subject,
        DateTimeOffset createdAt,
        string? result,
        string status = NhAssistantRunStatuses.Completed,
        bool shadow = false)
    {
        var thread = await ThreadAtAsync(app, agentId, subject, shadow);
        var run = new AssistantRun
        {
            Id = Guid.NewGuid(),
            ConversationId = thread.ConversationId,
            TriggerId = NhAssistantTriggerIds.Manual,
            AccountableOwnerId = thread.AccountableOwnerId,
            Status = status,
            AgentVersion = 1,
            ToolCalls = 2,
            InputTokens = 100,
            OutputTokens = 50,
            Summary = "Ended " + status + ".",
            Result = result,
            Shadow = shadow,
            CreatedAt = createdAt,
            StartedAt = createdAt,
            EndedAt = createdAt.AddSeconds(30),
            ConcurrencyStamp = Guid.NewGuid()
        };
        await WithRunsAsync(app, async runs =>
        {
            await runs.AddRunAsync(run, CancellationToken.None);
            return true;
        });
        return run.Id;
    }

    private static async Task<Guid> AddProposalAsync(
        AssistantWebApplication app,
        Guid conversationId,
        Guid runId,
        Guid? placedFrom,
        DateTimeOffset createdAt)
    {
        await using var scope = app.Services.CreateAsyncScope();
        await using var context = scope.ServiceProvider.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        var approval = new AssistantApproval
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            TurnId = Guid.NewGuid(),
            ToolInvocationId = Guid.NewGuid(),
            ProposalId = Guid.NewGuid(),
            ProposalHash = "sha256:" + Guid.NewGuid().ToString("N"),
            ProposalJson = "{}",
            ToolId = "projects.complete",
            Summary = "Complete the project.",
            ArgumentsPreview = "{}",
            Status = NhAssistantApprovalStatuses.Pending,
            CreatedAt = createdAt,
            ExpiresAt = createdAt.AddDays(1),
            RunId = runId,
            PlacedFromConversationId = placedFrom,
            Mode = NhAssistantApprovalModes.Deferred,
            ConcurrencyStamp = Guid.NewGuid()
        };
        context.Approvals.Add(approval);
        await context.SaveChangesAsync();
        return approval.Id;
    }

    private static async Task<Guid> AddArtifactAsync(AssistantWebApplication app, Guid conversationId, Guid? runId, string name, string content)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var bytes = Encoding.UTF8.GetBytes(content);
        var artifact = await scope.ServiceProvider.GetRequiredService<NhAssistantArtifactStore>().AddVersionAsync(
            new AssistantArtifact
            {
                Id = Guid.NewGuid(),
                ConversationId = conversationId,
                RunId = runId,
                Name = name,
                ContentType = "text/plain",
                SizeBytes = bytes.Length,
                Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)),
                Content = bytes,
                CreatedAt = DateTimeOffset.UtcNow
            },
            CancellationToken.None);
        return artifact.Id;
    }

    /// <summary>
    /// The ids of the runs <c>GET runs</c> lists for the client, newest first. With
    /// <paramref name="asAdminReader"/> only the shadow runs of that listing.
    /// </summary>
    private static async Task<Guid[]> ListIdsAsync(HttpClient client, string query = "", bool asAdminReader = false)
    {
        var list = await ReadJsonAsync(await client.GetAsync("/api/assistant/runs" + query));
        return list.GetProperty("items").EnumerateArray()
            .Where(item => !asAdminReader || item.GetProperty("shadow").GetBoolean())
            .Select(item => item.GetProperty("id").GetGuid())
            .ToArray();
    }

    private static async Task AssertNotFoundAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task<T> WithRunsAsync<T>(AssistantWebApplication app, Func<NhAssistantRunStore, Task<T>> action)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<NhAssistantRunStore>());
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        Assert.Equal(expected, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    /// <summary>
    /// The people's roles per project of the test.
    /// </summary>
    private sealed class ResultProjects
    {
        public ConcurrentDictionary<string, Dictionary<string, NhAiSubjectRole>> Roles { get; } = new(StringComparer.Ordinal);
    }

    private sealed class ResultProjectResolver(ResultProjects projects) : INhAiSubjectAccessResolver
    {
        public string SubjectType => "project";

        public ValueTask<NhAiSubjectInfo?> DescribeAsync(NhAiSubjectRef subject, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<NhAiSubjectInfo?>(projects.Roles.ContainsKey(subject.Id)
                ? new NhAiSubjectInfo(subject, NhAiSubjectInfo.CreateScopeKey(null, null)) { DisplayName = "Project " + subject.Id[..8] }
                : null);
        }

        public ValueTask<NhAiSubjectAccess> ResolveAsync(NhAiSubjectRef subject, string actorId, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(projects.Roles.TryGetValue(subject.Id, out var roles) && roles.TryGetValue(actorId, out var role)
                ? new NhAiSubjectAccess(role)
                : NhAiSubjectAccess.None);
        }

        public async IAsyncEnumerable<NhAiSubjectMember> ListMembersAsync(
            NhAiSubjectRef subject,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (projects.Roles.TryGetValue(subject.Id, out var roles))
            {
                foreach (var (actorId, role) in roles)
                {
                    yield return new NhAiSubjectMember(actorId, role) { DisplayName = actorId };
                }
            }
        }
    }

    /// <summary>
    /// The people as headless runs and the whole application see them: the administrator administers,
    /// the viewer passes the results' view policy, the manager the project manager agent's required
    /// policy, the others only use the assistant; the stranger's account may no longer sign in.
    /// </summary>
    private sealed class ResultPrincipals : INhAiPrincipalFactory
    {
        public ValueTask<ClaimsPrincipal?> CreateAsync(string actorId, NhAiSubjectInfo subject, CancellationToken cancellationToken = default)
        {
            var permissions = actorId switch
            {
                Admin => new[] { "assistant", "assistant-admin" },
                Viewer => new[] { "assistant", ViewPolicy },
                Manager => new[] { "assistant", "manager" },
                Stranger => null,
                _ => new[] { "assistant" }
            };
            return ValueTask.FromResult(permissions is null
                ? null
                : new ClaimsPrincipal(new ClaimsIdentity(
                    permissions.Select(value => new Claim("permission", value)).Append(new Claim(ClaimTypes.NameIdentifier, actorId)),
                    "test")));
        }
    }
}
