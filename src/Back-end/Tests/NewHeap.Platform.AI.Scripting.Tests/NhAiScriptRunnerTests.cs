using System.Text.Json;
using Xunit;

namespace NewHeap.Platform.AI.Scripting.Tests;

/// <summary>
/// The script engine without a database: a program chains the tools of its host and only its value
/// returns; every limit stops it with its code and the calls that ran; the engine reaches no .NET, no
/// modules and no tool its host does not offer.
/// </summary>
public sealed class NhAiScriptRunnerTests
{
    private static readonly Guid Apollo = Guid.Parse("4a7c9a2e-0000-4000-8000-000000000001");
    private static readonly Guid Hermes = Guid.Parse("4a7c9a2e-0000-4000-8000-000000000002");

    [Fact]
    public async Task A_chained_program_returns_only_its_value_and_lists_every_call_in_order()
    {
        var host = ProjectsWithRisks();

        var result = await RunAsync(
            """
            const found = await tools.projects_search_v1({ query: "a" });
            if (!found.success) { return found.code; }
            const high = [];
            for (const project of found.data) {
              const risks = await tools.risks_list_v1({ projectId: project.id, level: "High" });
              if (risks.success && risks.data.length > 0) { high.push({ key: project.key, owner: risks.data[0].owner }); }
            }
            return high;
            """,
            host);

        Assert.True(result.Completed, result.Describe());
        Assert.Equal("""[{"key":"APO","owner":"Ada"}]""", result.Value!.Value.GetRawText());
        Assert.Equal(
            ["projects_search_v1", "risks_list_v1", "risks_list_v1"],
            result.Calls.Select(call => call.Tool).ToArray());
        Assert.All(result.Calls, call => Assert.Equal(NhAiScriptCallOutcomes.Done, call.Outcome));
        Assert.Equal([1, 2, 3], result.Calls.Select(call => call.Sequence).ToArray());
        Assert.Equal(2, result.RoundsSaved);

        var shared = result.ToToolResult();
        Assert.True(shared.GetProperty("success").GetBoolean());
        Assert.Equal("completed", shared.GetProperty("data").GetProperty("status").GetString());
        // Only the value returns: the projects the tools listed do not.
        Assert.DoesNotContain("Hermes", shared.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Promise_all_runs_the_calls_one_at_a_time_in_the_order_the_program_made_them()
    {
        var host = ProjectsWithRisks();

        var result = await RunAsync(
            $$"""
            const [a, b] = await Promise.all([
              tools.risks_list_v1({ projectId: "{{Hermes}}" }),
              tools.risks_list_v1({ projectId: "{{Apollo}}" })
            ]);
            return [a.data.length, b.data.length];
            """,
            host);

        Assert.True(result.Completed, result.Describe());
        Assert.Equal("[0,1]", result.Value!.Value.GetRawText());
        Assert.Equal(
            [Hermes.ToString(), Apollo.ToString()],
            host.Calls.Select(call => call.Arguments.GetProperty("projectId").GetString()!).ToArray());
    }

    [Fact]
    public async Task Every_call_carries_the_hash_of_the_script_and_its_position()
    {
        var host = ProjectsWithRisks();
        const string script = """
            await tools.projects_search_v1({});
            await tools.projects_search_v1({ query: "b" });
            return 1;
            """;

        await RunAsync(script, host);

        Assert.All(host.Calls, call => Assert.Equal(NhAiScriptRunner.HashOf(script), call.ScriptHash));
        Assert.Equal([1, 2], host.Calls.Select(call => call.Sequence).ToArray());
        Assert.Equal(64, NhAiScriptRunner.HashOf(script).Length);
    }

    [Fact]
    public async Task An_infinite_loop_stops_at_the_statement_limit()
    {
        var result = await RunAsync("while (true) { }", ProjectsWithRisks(), limits => limits.MaxStatements = 10_000);

        Assert.Equal(NhAiScriptCodes.StatementLimit, result.Stop?.Code);
        Assert.False(result.ToToolResult().GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task An_infinite_loop_after_a_call_stops_at_the_time_limit_and_keeps_the_call()
    {
        var result = await RunAsync(
            "await tools.projects_search_v1({}); while (true) { }",
            ProjectsWithRisks(),
            limits =>
            {
                limits.MaxStatements = int.MaxValue;
                limits.Timeout = TimeSpan.FromSeconds(1);
            });

        Assert.Equal(NhAiScriptCodes.Timeout, result.Stop?.Code);
        Assert.Equal(NhAiScriptCallOutcomes.Done, Assert.Single(result.Calls).Outcome);
    }

    [Fact]
    public async Task Memory_is_counted_over_the_whole_script_also_after_a_tool_call()
    {
        var before = await RunAsync(
            "const a = []; for (;;) { a.push('x'.repeat(1000) + a.length); }",
            ProjectsWithRisks(),
            limits => limits.MaxMemoryBytes = 2 * 1024 * 1024);
        var after = await RunAsync(
            "let a = []; for (let k = 0; k < 40; k++) { await tools.projects_search_v1({}); for (let i = 0; i < 100; i++) { a.push('y'.repeat(500) + i); } } return a.length;",
            ProjectsWithRisks(),
            limits =>
            {
                limits.MaxMemoryBytes = 2 * 1024 * 1024;
                limits.MaxToolCalls = 64;
            });

        Assert.Equal(NhAiScriptCodes.MemoryLimit, before.Stop?.Code);
        Assert.Equal(NhAiScriptCodes.MemoryLimit, after.Stop?.Code);
        Assert.NotEmpty(after.Calls);
    }

    [Fact]
    public async Task Deep_recursion_stops_at_the_recursion_limit()
    {
        var result = await RunAsync(
            "function down(n) { return down(n + 1) + 1; } return down(0);",
            ProjectsWithRisks(),
            limits => limits.MaxRecursionDepth = 32);

        Assert.Equal(NhAiScriptCodes.RecursionLimit, result.Stop?.Code);
    }

    [Fact]
    public async Task The_call_after_the_tool_call_limit_does_not_run()
    {
        var host = ProjectsWithRisks();

        var result = await RunAsync(
            "for (let i = 0; i < 10; i++) { await tools.projects_search_v1({}); } return 'never';",
            host,
            limits => limits.MaxToolCalls = 3);

        Assert.Equal(NhAiScriptCodes.ToolCallLimit, result.Stop?.Code);
        Assert.Equal(4, result.Stop?.Sequence);
        Assert.Equal(3, host.Calls.Count);
        Assert.Equal(3, result.Calls.Count);
    }

    [Fact]
    public async Task The_mutation_after_the_mutation_limit_does_not_run_and_reads_go_on_counting_apart()
    {
        var host = ProjectsWithRisks()
            .Tool("projects_change_status_v1", _ => FakeScriptHost.Ok(new { changed = true }), NhAiToolEffect.IdempotentMutation, FakeScriptHost.StatusSchema);

        var result = await RunAsync(
            $$"""
            await tools.projects_search_v1({});
            await tools.projects_change_status_v1({ projectId: "{{Apollo}}", status: "OnHold" });
            await tools.projects_search_v1({});
            await tools.projects_change_status_v1({ projectId: "{{Hermes}}", status: "OnHold" });
            return 'never';
            """,
            host,
            limits => limits.MaxMutationsPerScript = 1);

        Assert.Equal(NhAiScriptCodes.MutationLimit, result.Stop?.Code);
        Assert.Equal(4, result.Stop?.Sequence);
        Assert.Equal(["projects_search_v1", "projects_change_status_v1", "projects_search_v1"], host.Calls.Select(call => call.Tool.Name).ToArray());
    }

    [Fact]
    public async Task A_read_only_script_is_not_offered_tools_that_change_data_and_a_call_of_one_is_refused()
    {
        var host = ProjectsWithRisks()
            .Tool("projects_change_status_v1", _ => FakeScriptHost.Ok(new { changed = true }), NhAiToolEffect.IdempotentMutation, FakeScriptHost.StatusSchema);
        var runner = new NhAiScriptRunner(new NhAiScriptingOptions());
        var limits = new NhAiScriptLimits();

        var description = NhAiScriptTool.Describe(runner.CallableTools(host, limits), limits);
        var result = await runner.RunAsync(
            $$"""return await tools.projects_change_status_v1({ projectId: "{{Apollo}}", status: "OnHold" });""",
            host,
            limits);

        Assert.DoesNotContain("projects_change_status_v1", description, StringComparison.Ordinal);
        Assert.Contains("read-only", description, StringComparison.Ordinal);
        Assert.Equal(NhAiScriptCodes.ToolRefused, result.Stop?.Code);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public async Task A_destructive_tool_runs_in_a_script_only_after_it_opted_in()
    {
        FakeScriptHost Host() => ProjectsWithRisks()
            .Tool("projects_delete_v1", _ => FakeScriptHost.Ok(new { deleted = true }), NhAiToolEffect.Destructive, FakeScriptHost.RiskSchema, id: "projects.delete");
        var script = $$"""return (await tools.projects_delete_v1({ projectId: "{{Apollo}}" })).success;""";
        var refusedHost = Host();
        var allowedHost = Host();
        var options = new NhAiScriptingOptions();
        var limits = new NhAiScriptLimits { MaxMutationsPerScript = 1 };

        var refused = await new NhAiScriptRunner(options).RunAsync(script, refusedHost, limits);
        new NhAiScriptingBuilderAccess(options).AllowDestructive("projects.delete");
        var allowed = await new NhAiScriptRunner(options).RunAsync(script, allowedHost, limits);

        Assert.Equal(NhAiScriptCodes.ToolRefused, refused.Stop?.Code);
        Assert.Contains("destructive", refused.Stop!.Message, StringComparison.Ordinal);
        Assert.Empty(refusedHost.Calls);
        Assert.True(allowed.Completed, allowed.Describe());
        Assert.Single(allowedHost.Calls);
    }

    [Fact]
    public async Task The_result_limit_stops_a_script_that_returns_too_much()
    {
        var result = await RunAsync("return 'x'.repeat(5000);", ProjectsWithRisks(), limits => limits.MaxResultBytes = 1024);

        Assert.Equal(NhAiScriptCodes.ResultTooLarge, result.Stop?.Code);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task A_script_reaches_no_dotnet_no_require_no_modules_and_no_string_compilation()
    {
        var host = ProjectsWithRisks();

        var globals = await RunAsync(
            "return [typeof System, typeof importNamespace, typeof require, typeof clr, typeof host, typeof setTimeout, typeof fetch, typeof process].join(',');",
            host);
        var required = await RunAsync("return require('fs');", host);
        var imported = await RunAsync("return await import('fs');", host);
        var staticImport = await RunAsync("import fs from 'fs';\nreturn 1;", host);
        var evaluated = await RunAsync("return eval('1 + 1');", host);
        var constructed = await RunAsync("return new Function('return 1')();", host);
        var reflected = await RunAsync("return tools.projects_search_v1.constructor('return this')();", host);

        Assert.Equal("undefined,undefined,undefined,undefined,undefined,undefined,undefined,undefined", globals.Value!.Value.GetString());
        Assert.Equal(NhAiScriptCodes.RuntimeError, required.Stop?.Code);
        Assert.Contains("require", required.Stop!.Message, StringComparison.Ordinal);
        Assert.Contains(imported.Stop?.Code, new[] { NhAiScriptCodes.NotSupported, NhAiScriptCodes.RuntimeError });
        Assert.Equal(NhAiScriptCodes.SyntaxError, staticImport.Stop?.Code);
        Assert.Equal(NhAiScriptCodes.RuntimeError, evaluated.Stop?.Code);
        Assert.Equal(NhAiScriptCodes.RuntimeError, constructed.Stop?.Code);
        Assert.Equal(NhAiScriptCodes.RuntimeError, reflected.Stop?.Code);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public async Task A_syntax_error_names_the_line_and_column_in_the_script()
    {
        var result = await RunAsync("const a = 1;\nconst b = ;\nreturn a;", ProjectsWithRisks());

        Assert.Equal(NhAiScriptCodes.SyntaxError, result.Stop?.Code);
        Assert.Equal(2, result.Stop?.Line);
        Assert.Equal(11, result.Stop?.Column);
        var stoppedAt = result.ToToolResult().GetProperty("data").GetProperty("stoppedAt");
        Assert.Equal(2, stoppedAt.GetProperty("line").GetInt32());
    }

    [Fact]
    public async Task A_program_that_closes_its_function_is_refused_before_it_runs()
    {
        var host = ProjectsWithRisks();

        var result = await RunAsync("}); tools.projects_search_v1({}); (async () => {", host);

        Assert.Equal(NhAiScriptCodes.SyntaxError, result.Stop?.Code);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public async Task A_runtime_error_names_the_error_and_its_line()
    {
        var result = await RunAsync("const x = null;\nreturn x.y;", ProjectsWithRisks());

        Assert.Equal(NhAiScriptCodes.RuntimeError, result.Stop?.Code);
        Assert.Contains("TypeError", result.Stop!.Message, StringComparison.Ordinal);
        Assert.Equal(2, result.Stop.Line);
    }

    [Fact]
    public async Task A_tool_the_turn_does_not_offer_is_never_reached_even_when_the_program_catches_the_stop()
    {
        var host = ProjectsWithRisks();

        var unknown = await RunAsync("return await tools.payroll_export_v1({});", host);
        var caught = await RunAsync("try { await tools.payroll_export_v1({}); } catch (e) { } return await tools.projects_search_v1({});", host);
        var bracket = await RunAsync("return await tools['payroll.export']({});", host);

        Assert.Equal(NhAiScriptCodes.UnknownTool, unknown.Stop?.Code);
        Assert.Contains("projects_search_v1", unknown.Stop!.Message, StringComparison.Ordinal);
        Assert.Equal(NhAiScriptCodes.UnknownTool, caught.Stop?.Code);
        Assert.Equal(NhAiScriptCodes.UnknownTool, bracket.Stop?.Code);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public async Task A_composition_or_the_script_tool_itself_is_never_a_tool_of_a_script()
    {
        var host = ProjectsWithRisks()
            .Tool("projects_overview_v1", _ => FakeScriptHost.Ok(1), feature: NhAiCompositionFeature.Compositions)
            .Tool(NhAiScriptTool.FunctionName, _ => FakeScriptHost.Ok(1), id: NhAiScriptTool.ToolId, feature: NhAiCompositionFeature.Scripts);

        var composition = await RunAsync("return await tools.projects_overview_v1({});", host);
        var nested = await RunAsync("return await tools.run_script({ script: 'return 1' });", host);

        Assert.Equal(NhAiScriptCodes.UnknownTool, composition.Stop?.Code);
        Assert.Equal(NhAiScriptCodes.UnknownTool, nested.Stop?.Code);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public async Task A_call_that_does_not_match_its_signature_stops_with_the_exact_signature_and_the_field_to_correct()
    {
        var host = ProjectsWithRisks();

        var misspelled = await RunAsync("return await tools.projects_search_v1({ qery: 'a' });", host);
        var mistyped = await RunAsync("return await tools.risks_list_v1({ projectId: 42 });", host);
        var missing = await RunAsync("return await tools.risks_list_v1({ level: 'High' });", host);
        var outOfEnum = await RunAsync($$"""return await tools.risks_list_v1({ projectId: "{{Apollo}}", level: "Severe" });""", host);

        Assert.Equal(NhAiScriptCodes.CallInvalid, misspelled.Stop?.Code);
        Assert.Equal("qery", misspelled.Stop!.FieldError!.Field);
        Assert.Contains("did you mean 'query'", misspelled.Stop.FieldError.Error, StringComparison.Ordinal);
        Assert.Equal(
            "projects_search_v1(input?: { query?: string | null; limit?: number }): Promise<Result<Array<{ id: string /* uuid */; key: string; name: string }>>>;",
            misspelled.Stop.Signature);
        Assert.Equal(("projectId", "expected string /* uuid */, got integer"), (mistyped.Stop!.FieldError!.Field, mistyped.Stop.FieldError.Error));
        Assert.Equal(("projectId", "required"), (missing.Stop!.FieldError!.Field, missing.Stop.FieldError.Error));
        Assert.Contains("\"High\"", outOfEnum.Stop!.FieldError!.Error, StringComparison.Ordinal);
        Assert.Empty(host.Calls);

        var stoppedAt = misspelled.ToToolResult().GetProperty("data").GetProperty("stoppedAt");
        Assert.Equal(misspelled.Stop.Signature, stoppedAt.GetProperty("signature").GetString());
        Assert.Equal("qery", stoppedAt.GetProperty("field").GetString());
    }

    [Fact]
    public async Task Input_the_tool_itself_rejects_stops_with_the_signature_as_well()
    {
        var host = new FakeScriptHost()
            .Tool("projects_search_v1", _ => FakeScriptHost.Fail(NhAiToolFailureCodes.InputInvalid, "The tool input could not be bound."), inputSchema: "{}");

        var result = await RunAsync("await tools.projects_search_v1({ any: 1 }); return 'never';", host);

        Assert.Equal(NhAiScriptCodes.CallInvalid, result.Stop?.Code);
        Assert.Equal(NhAiToolFailureCodes.InputInvalid, result.Stop?.CallCode);
        Assert.Equal(NhAiScriptCallOutcomes.Invalid, Assert.Single(result.Calls).Outcome);
        Assert.NotNull(result.Stop?.Signature);
    }

    [Fact]
    public async Task A_deferred_proposal_returns_the_proposal_result_and_the_script_goes_on()
    {
        var proposalId = Guid.NewGuid();
        var host = ProjectsWithRisks()
            .Tool(
                "projects_change_status_v1",
                _ => NhAiScriptCallOutcome.Proposed(FakeScriptHost.ProposalRecorded(proposalId), proposalId),
                NhAiToolEffect.IdempotentMutation,
                FakeScriptHost.StatusSchema);

        var result = await RunAsync(
            $$"""
            const changed = await tools.projects_change_status_v1({ projectId: "{{Apollo}}", status: "OnHold" });
            const after = await tools.projects_search_v1({});
            return { proposed: changed.code, after: after.data.length };
            """,
            host,
            limits => limits.MaxMutationsPerScript = 2);

        Assert.True(result.Completed, result.Describe());
        Assert.Equal("""{"proposed":"assistant-proposal-recorded","after":2}""", result.Value!.Value.GetRawText());
        Assert.Equal([NhAiScriptCallOutcomes.Proposed, NhAiScriptCallOutcomes.Done], result.Calls.Select(call => call.Outcome).ToArray());
        Assert.Equal(proposalId, result.Calls[0].ProposalId);
        Assert.Equal(1, result.Proposed);
        Assert.Equal(1, result.Done);
    }

    [Fact]
    public async Task An_inline_approval_stops_the_script_with_what_it_did_so_far_and_the_pending_approval()
    {
        var proposalId = Guid.NewGuid();
        var host = ProjectsWithRisks()
            .Tool(
                "projects_change_status_v1",
                _ => NhAiScriptCallOutcome.AwaitingApproval(FakeScriptHost.Fail(NhAiToolFailureCodes.ApprovalRequired, "AI tool approval is required.").Result, proposalId),
                NhAiToolEffect.IdempotentMutation,
                FakeScriptHost.StatusSchema);

        var result = await RunAsync(
            $$"""
            await tools.projects_search_v1({});
            await tools.projects_change_status_v1({ projectId: "{{Apollo}}", status: "OnHold" });
            await tools.projects_search_v1({ query: "after" });
            return 'never';
            """,
            host,
            limits => limits.MaxMutationsPerScript = 2);

        Assert.Equal(NhAiScriptCodes.WaitingForApproval, result.Stop?.Code);
        Assert.Equal(2, result.Stop?.Sequence);
        Assert.Equal(proposalId, result.Stop?.ProposalId);
        Assert.Equal([NhAiScriptCallOutcomes.Done, NhAiScriptCallOutcomes.WaitingForApproval], result.Calls.Select(call => call.Outcome).ToArray());
        Assert.Equal(2, host.Calls.Count);
        var shared = result.ToToolResult();
        Assert.Equal(NhAiScriptCodes.WaitingForApproval, shared.GetProperty("code").GetString());
        Assert.Contains("1 call(s) were done", shared.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(proposalId.ToString(), shared.GetProperty("data").GetProperty("stoppedAt").GetProperty("proposalId").GetString());
    }

    [Fact]
    public async Task A_call_the_turn_refuses_stops_the_script_with_the_turns_code()
    {
        var host = new FakeScriptHost()
            .Tool("projects_search_v1", _ => NhAiScriptCallOutcome.Refused(JsonSerializer.SerializeToElement(new { success = false, code = "assistant-tool-call-limit-reached" })));

        var result = await RunAsync("await tools.projects_search_v1({}); return 'never';", host);

        Assert.Equal(NhAiScriptCodes.CallRefused, result.Stop?.Code);
        Assert.Equal("assistant-tool-call-limit-reached", result.Stop?.CallCode);
        Assert.Equal(NhAiScriptCallOutcomes.Refused, Assert.Single(result.Calls).Outcome);
    }

    [Fact]
    public async Task A_failed_tool_returns_its_failure_to_the_program_which_may_go_on()
    {
        var host = new FakeScriptHost()
            .Tool("projects_search_v1", _ => FakeScriptHost.Fail("project-search-query-too-long", "A query has at most 200 characters."));

        var result = await RunAsync("const r = await tools.projects_search_v1({}); return r.success ? 'ok' : r.code;", host);

        Assert.True(result.Completed, result.Describe());
        Assert.Equal("\"project-search-query-too-long\"", result.Value!.Value.GetRawText());
        Assert.Equal(("failed", "project-search-query-too-long"), (result.Calls[0].Outcome, result.Calls[0].Code));
    }

    [Fact]
    public async Task A_program_that_waits_for_nothing_stops_as_stalled_and_console_output_returns()
    {
        var stalled = await RunAsync("await new Promise(() => { }); return 1;", ProjectsWithRisks());
        var logged = await RunAsync("console.log('found', 2, { a: 1 }); return 1;", ProjectsWithRisks());

        Assert.Equal(NhAiScriptCodes.Stalled, stalled.Stop?.Code);
        Assert.Equal(["found 2 {\"a\":1}"], logged.Logs);
    }

    [Fact]
    public async Task A_script_longer_than_its_limit_does_not_run()
    {
        var host = ProjectsWithRisks();

        var result = await RunAsync("return 1;" + new string(' ', 200), host, limits => limits.MaxScriptLength = 100);

        Assert.Equal(NhAiScriptCodes.TooLong, result.Stop?.Code);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void Limits_outside_their_range_are_refused()
    {
        Assert.Throws<InvalidOperationException>(() => new NhAiScriptLimits { MaxScriptLength = NhAiScriptLimits.HardMaxScriptLength + 1 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new NhAiScriptLimits { MaxToolCalls = NhAiScriptLimits.HardMaxToolCalls + 1 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new NhAiScriptLimits { MaxToolCalls = 2, MaxMutationsPerScript = 3 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new NhAiScriptLimits { Timeout = TimeSpan.Zero }.Validate());
        new NhAiScriptLimits().Validate();
        Assert.Equal(0, new NhAiScriptLimits().MaxMutationsPerScript);
    }

    private static FakeScriptHost ProjectsWithRisks()
    {
        return new FakeScriptHost()
            .Tool(
                "projects_search_v1",
                _ => FakeScriptHost.Ok(new[]
                {
                    new { id = Apollo, key = "APO", name = "Apollo" },
                    new { id = Hermes, key = "HER", name = "Hermes" }
                }),
                outputSchema: FakeScriptHost.ProjectListSchema)
            .Tool(
                "risks_list_v1",
                arguments => FakeScriptHost.Ok(arguments.GetProperty("projectId").GetString() == Apollo.ToString()
                    ? new[] { new { title = "Late supplier", owner = "Ada" } }
                    : []),
                inputSchema: FakeScriptHost.RiskSchema);
    }

    private static Task<NhAiScriptResult> RunAsync(string script, FakeScriptHost host, Action<NhAiScriptLimits>? configure = null)
    {
        var limits = new NhAiScriptLimits();
        configure?.Invoke(limits);
        return new NhAiScriptRunner(new NhAiScriptingOptions()).RunAsync(script, host, limits);
    }

    /// <summary>
    /// Opts a destructive tool in as an application's <c>AllowDestructive</c> does.
    /// </summary>
    private sealed class NhAiScriptingBuilderAccess(NhAiScriptingOptions options)
    {
        public void AllowDestructive(string toolId)
        {
            options.AllowDestructive(toolId);
        }
    }
}
