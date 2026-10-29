using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Scripting;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using SampleProjectManagement.Api.Composition;
using SampleProjectManagement.Core.Services;
using SampleProjectManagement.DAL;
using SampleProjectManagement.DAL.Entities;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// Executable evidence for SPM-373 and SPM-374: the project assistant answers a chained question with
/// one script that runs the turn's tools on the server and returns only what the answer needs, and a
/// status change inside a script gets the sample's approval exactly as the same call on its own does.
/// The portal's own setting of the environment and agent switches scripts on (SPM-371). SPM-375: a
/// script passes a due date and a time of day to <c>projects.due-by</c> as strings, and the tool reads
/// them on PostgreSQL as the date and time the script meant.
/// </summary>
public sealed class AssistantScriptSamplesTests(AssistantScriptSampleHost host) : IClassFixture<AssistantScriptSampleHost>
{
    [Fact]
    public async Task SPM_373_the_assistant_answers_a_chained_question_with_one_script()
    {
        const string script = """
            const roadmap = await tools.projects_search_v1({ query: "roadmap", limit: 5 });
            const launch = await tools.projects_search_v1({ query: "launch", limit: 5 });
            if (!roadmap.success || !launch.success) { return { failed: true }; }
            const keys = [...new Set([...roadmap.data, ...launch.data].map(project => project.key))];
            return { keys, searched: 2 };
            """;
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(NhAiScriptTool.FunctionName, new { script })
            .RespondWithText("Your roadmap and launch work is in PRJ-1.");
        await SwitchScriptsOnAsync();
        host.Model.Use(model);
        var searchesBefore = host.Projects.SearchedDivisions.Count;
        using var client = host.CreateClient("spm-373-user");
        var conversationId = await host.CreateConversationAsync(client);

        var events = await host.SendAsync(client, conversationId, "Which projects cover the roadmap and the launch?");

        Assert.Equal("completed", events[^1].Data.GetProperty("status").GetString());
        // The script tool is offered next to the individual tools.
        var offered = model.Requests[0].Options!.Tools!.Select(tool => tool.Name).ToArray();
        Assert.Contains(NhAiScriptTool.FunctionName, offered);
        Assert.Contains("projects_search_v1", offered);
        // Both searches ran in the person's division, as their own calls would.
        Assert.Equal(2, host.Projects.SearchedDivisions.Count - searchesBefore);
        Assert.All(host.Projects.SearchedDivisions.Skip(searchesBefore), division => Assert.Equal(host.DivisionId, division));
        // Only the program's value reached the model, with the calls it ran in order.
        var result = JsonSerializer.SerializeToElement(Assert.Single(model.FunctionResults).Result);
        var data = result.GetProperty("data");
        Assert.True(result.GetProperty("success").GetBoolean(), result.GetRawText());
        Assert.Equal("""{"keys":["PRJ-1"],"searched":2}""", data.GetProperty("value").GetRawText());
        Assert.Equal(["done", "done"], data.GetProperty("calls").EnumerateArray().Select(call => call.GetProperty("outcome").GetString()!).ToArray());
        Assert.Equal(1, data.GetProperty("roundsSaved").GetInt32());
        // One model round for the chain: the script call, then the answer.
        Assert.Equal(2, model.Requests.Count);
    }

    [Fact]
    public async Task SPM_373_what_the_searches_allocate_does_not_count_as_the_scripts_memory()
    {
        // Each search allocates twice the script's memory budget (16 MiB by default) while it runs; the
        // program keeps only two keys, so it completes.
        const string script = """
            const roadmap = await tools.projects_search_v1({ query: "roadmap", limit: 5 });
            const launch = await tools.projects_search_v1({ query: "launch", limit: 5 });
            return [roadmap.data[0].key, launch.data[0].key];
            """;
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(NhAiScriptTool.FunctionName, new { script })
            .RespondWithText("Both are in PRJ-1.");
        await SwitchScriptsOnAsync();
        host.Model.Use(model);
        host.Projects.SearchWorkBytes = 32 * 1024 * 1024;
        try
        {
            using var client = host.CreateClient("spm-373-memory-user");
            var conversationId = await host.CreateConversationAsync(client);

            var events = await host.SendAsync(client, conversationId, "Which projects cover the roadmap and the launch?");

            Assert.Equal("completed", events[^1].Data.GetProperty("status").GetString());
            var result = JsonSerializer.SerializeToElement(Assert.Single(model.FunctionResults).Result);
            Assert.True(result.GetProperty("success").GetBoolean(), result.GetRawText());
            Assert.Equal("""["PRJ-1","PRJ-1"]""", result.GetProperty("data").GetProperty("value").GetRawText());
        }
        finally
        {
            host.Projects.SearchWorkBytes = 0;
        }
    }

    [Fact]
    public async Task SPM_374_a_status_change_in_a_script_waits_for_the_persons_approval_as_it_does_on_its_own()
    {
        var projectId = Guid.NewGuid();
        var script = $$"""
            const found = await tools.projects_search_v1({ query: "roadmap", limit: 1 });
            const changed = await tools.projects_change_status_v1({ projectId: "{{projectId}}", status: "Active" });
            return { key: found.data[0].key, changed: changed.success };
            """;
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(NhAiScriptTool.FunctionName, new { script })
            .RespondWithText("The roadmap project is active now.");
        await SwitchScriptsOnAsync();
        host.Model.Use(model);
        using var client = host.CreateClient("spm-374-user");
        var conversationId = await host.CreateConversationAsync(client);

        var paused = await host.SendAsync(client, conversationId, "Activate the roadmap project.");

        // The change inside the script is the same proposal the call gets on its own; the script stopped
        // there and the turn waits for the person.
        var approval = paused.Single(evt => evt.Name == "approval.required").Data;
        Assert.Equal("projects.change-status", approval.GetProperty("toolId").GetString());
        Assert.Equal("waiting-for-approval", paused[^1].Data.GetProperty("status").GetString());
        Assert.DoesNotContain(host.Projects.StatusChanges, change => change.ProjectId == projectId);

        var resumed = await host.DecideAsync(client, conversationId, approval, "approve");

        Assert.Equal("completed", resumed[^1].Data.GetProperty("status").GetString());
        Assert.Equal(ProjectStatus.Active, host.Projects.StatusChanges.Single(change => change.ProjectId == projectId).Status);
        Assert.Contains(projectId, host.Projects.VerifiedProjects);
        var stopped = model.Requests[^1].Messages
            .SelectMany(message => message.Contents.OfType<FunctionResultContent>())
            .Select(content => JsonSerializer.SerializeToElement(content.Result))
            .First(content => content.TryGetProperty("code", out var code) && code.GetString() == NhAiScriptCodes.WaitingForApproval);
        Assert.Equal(
            ["done", "waiting-for-approval"],
            stopped.GetProperty("data").GetProperty("calls").EnumerateArray().Select(call => call.GetProperty("outcome").GetString()!).ToArray());
    }

    [Fact]
    public async Task SPM_375_a_script_passes_a_due_date_and_a_time_of_day_that_the_tool_reads()
    {
        const string script = """
            const due = await tools.projects_due_by_v1({ dueBy: "2026-11-01", before: "12:00", limit: 10 });
            if (!due.success) { return due.code; }
            return due.data.map(project => project.key + " " + project.dueDate + " " + project.dueTime);
            """;
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(NhAiScriptTool.FunctionName, new { script })
            .RespondWithText("DUE-1 and DUE-2 are due by 1 November at noon.");
        await SwitchScriptsOnAsync();
        host.Model.Use(model);
        using var client = host.CreateClient("spm-375-user");
        var conversationId = await host.CreateConversationAsync(client);

        var events = await host.SendAsync(client, conversationId, "Which projects are due by 1 November at noon UTC?");

        Assert.Equal("completed", events[^1].Data.GetProperty("status").GetString());
        Assert.Contains(ProjectDeadlineAiTools.DueByToolName, model.Requests[0].Options!.Tools!.Select(tool => tool.Name));
        var result = JsonSerializer.SerializeToElement(Assert.Single(model.FunctionResults).Result);
        Assert.True(result.GetProperty("success").GetBoolean(), result.GetRawText());
        // The tool read "2026-11-01" and "12:00" as a DateOnly and a TimeOnly and queried PostgreSQL with
        // them: the deadline at noon and the completed and other division's projects stay out, and the
        // deadlines come back as the strings a script reads.
        Assert.Equal(
            """["DUE-1 2026-10-30 15:00:00","DUE-2 2026-11-01 09:30:00"]""",
            result.GetProperty("data").GetProperty("value").GetRawText());
    }

    [Fact]
    public async Task SPM_375_a_date_in_another_form_stops_the_script_at_the_field_before_the_tool_runs()
    {
        const string script = """
            return await tools.projects_due_by_v1({ dueBy: "1 November 2026", limit: 10 });
            """;
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(NhAiScriptTool.FunctionName, new { script })
            .RespondWithText("I need the date as 2026-11-01.");
        await SwitchScriptsOnAsync();
        host.Model.Use(model);
        using var client = host.CreateClient("spm-375-invalid-user");
        var conversationId = await host.CreateConversationAsync(client);

        var events = await host.SendAsync(client, conversationId, "Which projects are due by 1 November?");

        Assert.Equal("completed", events[^1].Data.GetProperty("status").GetString());
        var result = JsonSerializer.SerializeToElement(Assert.Single(model.FunctionResults).Result);
        Assert.Equal(NhAiScriptCodes.CallInvalid, result.GetProperty("code").GetString());
        var stoppedAt = result.GetProperty("data").GetProperty("stoppedAt");
        Assert.Equal("dueBy", stoppedAt.GetProperty("field").GetString());
        Assert.Equal("expected a date string yyyy-MM-dd", stoppedAt.GetProperty("error").GetString());
        // The model sees the date and the time as strings with their format, never as objects.
        Assert.Equal(
            "projects_due_by_v1(input: { before?: string /* time */ | null; dueBy: string /* date */; limit: number }): "
            + "Promise<Result<Array<{ dueDate: string /* date */; dueTime: string /* time */; id: string /* uuid */; key: string; name: string }>>>;",
            stoppedAt.GetProperty("signature").GetString());
        // The tool never ran.
        Assert.Empty(result.GetProperty("data").GetProperty("calls").EnumerateArray());
    }

    /// <summary>
    /// The portal's own setting switches scripts on for the project assistant in this environment
    /// (SPM-371); compositions stay off.
    /// </summary>
    private async Task SwitchScriptsOnAsync()
    {
        await using var scope = host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<SampleAssistantCompositionSettings>()
            .SaveAsync(string.Empty, SampleAssistantComposition.AgentId, compositions: false, scripts: true, Guid.NewGuid(), TestContext.Current.CancellationToken);
    }
}

/// <summary>
/// The assistant sample with the portal's composition switch, as Program.cs composes it after
/// <c>AddSampleAssistant</c>, which registers the script tool, and the deadline tool of SPM-375 with
/// projects of the host's division in its database.
/// </summary>
public sealed class AssistantScriptSampleHost(SamplePostgreSqlServer postgreSql) : AssistantSampleHost(postgreSql)
{
    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        await using var scope = Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SampleProjectManagementDbContext>();
        var otherDivisionId = Guid.NewGuid();
        dbContext.Set<NhDivision>().AddRange(
            new NhDivision { Id = DivisionId, Name = "Script sample division", TimeZoneId = "Europe/Amsterdam" },
            new NhDivision { Id = otherDivisionId, Name = "Other division", TimeZoneId = "Europe/Amsterdam" });
        dbContext.Projects.AddRange(
            Due(DivisionId, "DUE-1", ProjectStatus.Active, new DateTimeOffset(2026, 10, 30, 15, 0, 0, TimeSpan.Zero)),
            Due(DivisionId, "DUE-2", ProjectStatus.Draft, new DateTimeOffset(2026, 11, 1, 9, 30, 0, TimeSpan.Zero)),
            Due(DivisionId, "DUE-3", ProjectStatus.Active, new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero)),
            Due(DivisionId, "DUE-4", ProjectStatus.Completed, new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero)),
            Due(otherDivisionId, "OTHER-1", ProjectStatus.Active, new DateTimeOffset(2026, 10, 15, 8, 0, 0, TimeSpan.Zero)));
        await dbContext.SaveChangesAsync();
    }

    protected override void ConfigureAssistant(IServiceCollection services)
    {
        services.AddSampleAssistantCompositions();
        services.AddSampleProjectDeadlines();
    }

    private static Project Due(Guid divisionId, string key, ProjectStatus status, DateTimeOffset deadline)
    {
        return new Project
        {
            Id = Guid.NewGuid(),
            DivisionId = divisionId,
            Key = key,
            Name = "Project " + key,
            Status = status,
            Deadline = deadline
        };
    }
}
