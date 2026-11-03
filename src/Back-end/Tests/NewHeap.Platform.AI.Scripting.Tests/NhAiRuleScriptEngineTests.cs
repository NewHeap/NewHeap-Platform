using System.Text.Json;
using NewHeap.Platform.AI.Chat;
using Xunit;

namespace NewHeap.Platform.AI.Scripting.Tests;

/// <summary>
/// Rule scripts on the script engine without a database (RB-1, RB-4): a script reads its run's subject,
/// inputs and clock, frozen; raises and resolves alerts of the kinds its agent lists, proposes and ends
/// the run once; calls every read of its run and only the writes its version opted in to, at most the
/// ceiling's writes per run, a destructive one only when the ceiling lets it run; and never reaches a tool
/// its run does not offer.
/// </summary>
public sealed class NhAiRuleScriptEngineTests
{
    private const string Kind = "project-needs-document";
    private static readonly Guid Apollo = Guid.Parse("4a7c9a2e-0000-4000-8000-000000000001");

    [Fact]
    public async Task A_script_reads_its_run_raises_and_resolves_alerts_and_ends_with_a_summary()
    {
        var host = Host();
        var proposals = new List<(string Tool, string Input, string? Rationale)>();

        var outcome = await RunAsync(
            $$"""
            const found = await tools.projects_search_v1({ query: subject.id });
            if (subject.type !== "project" || inputs[0].payload.days !== 3 || run.version !== 4 || run.mode !== "live") {
              throw new Error("unexpected run");
            }
            alerts.raise({ kind: "{{Kind}}", key: "start:2026-11-06", urgency: "warning", parameters: { project: found.data[0].key, days: 3 } });
            alerts.resolve({ kind: "{{Kind}}", key: null, subject: { type: "project", id: "other" } });
            const proposed = await propose({ tool: "projects_request_review_v1", input: { projectId: "{{Apollo}}" }, rationale: "No plan yet." });
            done("Warned about " + found.data[0].key + " (" + proposed.outcome + ").");
            """,
            host,
            run => run,
            (tool, input, rationale) =>
            {
                proposals.Add((tool, input.GetRawText(), rationale));
                return new NhAssistantRuleProposal(NhAssistantProposeOutcome.Created, Guid.NewGuid(), [], null, null);
            });

        Assert.True(outcome.Completed, outcome.Message);
        Assert.Equal((NhAssistantRuleScriptEndKind.Done, "Warned about APO (created)."), (outcome.End!.Kind, outcome.End.Text));
        var alert = Assert.Single(outcome.Alerts);
        Assert.Equal((Kind, "project", Apollo.ToString("D"), "start:2026-11-06", NhAssistantAlertUrgency.Warning), (alert.KindId, alert.Subject.Type, alert.Subject.Id, alert.Key, alert.Urgency));
        Assert.Equal("APO", alert.Parameters["project"]);
        Assert.Equal("3", alert.Parameters["days"]);
        var resolved = Assert.Single(outcome.Resolves);
        Assert.Equal((Kind, "other", null), (resolved.KindId, resolved.Subject.Id, resolved.Key));
        var proposal = Assert.Single(proposals);
        Assert.Equal(("projects_request_review_v1", "No plan yet."), (proposal.Tool, proposal.Rationale));
        Assert.Contains(Apollo.ToString("D"), proposal.Input, StringComparison.Ordinal);
        Assert.Equal(["projects_search_v1"], outcome.Calls.Select(call => call.Tool).ToArray());
    }

    [Fact]
    public async Task The_run_s_values_are_frozen_and_text_the_script_returns_is_its_summary()
    {
        var frozen = await RunAsync("""subject.id = "other"; return "changed";""", Host());
        var returned = await RunAsync("""return "Nothing to do.";""", Host());
        var silent = await RunAsync("return 42;", Host());

        Assert.Equal(NhAiScriptCodes.RuntimeError, frozen.Code);
        Assert.Equal("Nothing to do.", returned.End!.Text);
        Assert.Equal(NhAiRuleScriptEngine.DefaultSummary, silent.End!.Text);
    }

    [Fact]
    public async Task A_script_cannot_call_a_tool_its_run_does_not_offer()
    {
        var host = Host();

        var outcome = await RunAsync("""return await tools.users_delete_v1({ id: "u-1" });""", host);

        Assert.False(outcome.Completed);
        Assert.Equal(NhAiScriptCodes.UnknownTool, outcome.Code);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public async Task A_write_the_version_did_not_opt_in_to_is_refused_and_never_reaches_its_tool()
    {
        var host = Host();

        var outcome = await RunAsync(
            $$"""return await tools.projects_change_status_v1({ projectId: "{{Apollo}}", status: "OnHold" });""",
            host,
            run => run.With(writes: ["projects.request-review"], maxWrites: 1));

        Assert.Equal(NhAiScriptCodes.ToolRefused, outcome.Code);
        Assert.Contains("rule script version may not call it", outcome.Message, StringComparison.Ordinal);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public async Task More_writes_than_the_ceiling_allows_per_run_are_refused()
    {
        var host = Host();

        var outcome = await RunAsync(
            $$"""
            await tools.projects_change_status_v1({ projectId: "{{Apollo}}", status: "OnHold" });
            await tools.projects_change_status_v1({ projectId: "{{Apollo}}", status: "Active" });
            done("Twice.");
            """,
            host,
            run => run.With(writes: ["projects.change-status"], maxWrites: 1));

        Assert.Equal(NhAiScriptCodes.MutationLimit, outcome.Code);
        Assert.Single(host.Calls);
        Assert.Null(outcome.End);
    }

    [Fact]
    public async Task A_destructive_write_runs_only_when_the_ceiling_lets_it_run_in_a_script()
    {
        var script = $$"""await tools.projects_delete_v1({ projectId: "{{Apollo}}" }); done("Deleted.");""";
        var refusedHost = Host();
        var allowedHost = Host();

        var refused = await RunAsync(script, refusedHost, run => run.With(writes: ["projects.delete"], maxWrites: 1));
        var allowed = await RunAsync(script, allowedHost, run => run.With(writes: ["projects.delete"], maxWrites: 1, destructive: ["projects.delete"]));

        Assert.Equal(NhAiScriptCodes.ToolRefused, refused.Code);
        Assert.Contains("destructive", refused.Message, StringComparison.Ordinal);
        Assert.Empty(refusedHost.Calls);
        Assert.True(allowed.Completed, allowed.Message);
        Assert.Single(allowedHost.Calls);
    }

    [Fact]
    public async Task An_alert_of_a_kind_the_agent_does_not_list_is_a_type_error_and_the_run_ends_once()
    {
        var caught = await RunAsync(
            """
            try { alerts.raise({ kind: "not-listed" }); } catch (error) { console.log(error.name); }
            handOver("A person should look.");
            try { done("Again."); } catch (error) { console.log(error.message); }
            """,
            Host());
        var uncaught = await RunAsync("""alerts.raise({ kind: "not-listed" }); done("Never.");""", Host());

        Assert.True(caught.Completed, caught.Message);
        Assert.Equal((NhAssistantRuleScriptEndKind.HandOver, "A person should look."), (caught.End!.Kind, caught.End.Text));
        Assert.Equal("TypeError", caught.Logs[0]);
        Assert.Contains("already ended", caught.Logs[1], StringComparison.Ordinal);
        Assert.Empty(caught.Alerts);
        Assert.Equal(NhAiScriptCodes.RuntimeError, uncaught.Code);
        Assert.Contains("not listed", uncaught.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Propose_in_a_dry_run_records_and_tells_the_script_nothing_waits()
    {
        var recorded = 0;

        var outcome = await RunAsync(
            $$"""
            const proposed = await propose({ tool: "projects_request_review_v1", input: { projectId: "{{Apollo}}" } });
            done(proposed.outcome);
            """,
            Host(),
            run => run.With(mode: NhAssistantRuleScriptMode.DryRun),
            (_, _, _) =>
            {
                recorded++;
                return Refused(NhAssistantRuleScriptCodes.DryRunWrite);
            });

        Assert.Equal(1, recorded);
        Assert.Equal("dry-run", outcome.End!.Text);
    }

    [Fact]
    public void A_script_that_does_not_parse_names_its_line_and_the_declarations_name_what_it_may_call()
    {
        var engine = Engine();

        var problem = engine.Check("agent", "const a = 1;\nconst = ;");
        var declarations = engine.Describe(
            "agent",
            Host().Tools.Select(tool => tool.Descriptor).ToArray(),
            new HashSet<string>(["projects.request-review"], StringComparer.Ordinal));

        Assert.Equal(NhAiScriptCodes.SyntaxError, problem!.Code);
        Assert.Equal(2, problem.Line);
        Assert.Null(engine.Check("agent", "done('ok');"));
        Assert.Contains("alerts", declarations.PlatformFunctions, StringComparison.Ordinal);
        Assert.Contains("propose", declarations.PlatformFunctions, StringComparison.Ordinal);
        var callable = declarations.Tools.Where(tool => tool.Callable).Select(tool => tool.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["projects_request_review_v1", "projects_search_v1"], callable);
        Assert.NotNull(declarations.Tools.Single(tool => tool.Name == "projects_change_status_v1").Refusal);
    }

    private static NhAiRuleScriptEngine Engine()
    {
        var options = new NhAiScriptingOptions();
        return new NhAiRuleScriptEngine(options, new NhAiScriptRunner(options));
    }

    private static async Task<NhAssistantRuleScriptOutcome> RunAsync(
        string script,
        FakeScriptHost host,
        Func<RunShape, RunShape>? shape = null,
        Func<string, JsonElement, string?, NhAssistantRuleProposal>? propose = null)
    {
        var run = (shape ?? (value => value))(new RunShape(NhAssistantRuleScriptMode.Live, [], 0, []));
        var ruleRun = new NhAssistantRuleScriptRun((tool, input, rationale, _) => Task.FromResult(
            (propose ?? ((_, _, _) => Refused("assistant-proposal-refused")))(tool, input, rationale)))
        {
            AgentId = "sample-document-check",
            Version = 4,
            Script = script,
            Mode = run.Mode,
            Subject = new NhAiSubjectInfo(new NhAiSubjectRef("project", Apollo.ToString("D")), "division:1") { DisplayName = "Apollo" },
            TriggerId = "project-kickoff-check",
            RunId = Guid.NewGuid(),
            Now = new DateTimeOffset(2026, 11, 3, 7, 0, 0, TimeSpan.Zero),
            Inputs = [new NhAssistantRuleInput("project-kickoff-check", "Morning round.", """{"days":3}""", DateTimeOffset.UnixEpoch)],
            AlertKinds = [Kind],
            Tools = host.Tools.Select(tool => tool.Descriptor).ToArray(),
            Writes = run.Writes.ToHashSet(StringComparer.Ordinal),
            Destructive = run.Destructive.ToHashSet(StringComparer.Ordinal),
            MaxWrites = run.MaxWrites
        };

        return await Engine().RunAsync(ruleRun, host, CancellationToken.None);
    }

    private static NhAssistantRuleProposal Refused(string code)
    {
        return new NhAssistantRuleProposal(NhAssistantProposeOutcome.Refused, null, [], null, code);
    }

    private static FakeScriptHost Host()
    {
        return new FakeScriptHost()
            .Tool("projects_search_v1", _ => FakeScriptHost.Ok(new[] { new { id = Apollo, key = "APO", name = "Apollo" } }))
            .Tool("projects_change_status_v1", _ => FakeScriptHost.Ok(new { changed = true }), NhAiToolEffect.IdempotentMutation, FakeScriptHost.StatusSchema, id: "projects.change-status")
            .Tool("projects_request_review_v1", _ => FakeScriptHost.Ok(new { requested = true }), NhAiToolEffect.Mutation, FakeScriptHost.RiskSchema, id: "projects.request-review")
            .Tool("projects_delete_v1", _ => FakeScriptHost.Ok(new { deleted = true }), NhAiToolEffect.Destructive, FakeScriptHost.RiskSchema, id: "projects.delete");
    }

    private sealed record RunShape(NhAssistantRuleScriptMode Mode, IReadOnlyList<string> Writes, int MaxWrites, IReadOnlyList<string> Destructive)
    {
        public RunShape With(
            NhAssistantRuleScriptMode? mode = null,
            IReadOnlyList<string>? writes = null,
            int? maxWrites = null,
            IReadOnlyList<string>? destructive = null)
        {
            return new RunShape(mode ?? Mode, writes ?? Writes, maxWrites ?? MaxWrites, destructive ?? Destructive);
        }
    }
}
