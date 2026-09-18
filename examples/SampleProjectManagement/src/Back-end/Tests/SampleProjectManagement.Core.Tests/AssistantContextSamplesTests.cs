using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Test;
using SampleProjectManagement.Api.Composition;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// Executable evidence for SPM-254: every assistant turn carries the situation (date, time and
/// the sample user's name and roles) and the page the user has open, as data after the
/// instructions and outside the prompt hash.
/// </summary>
public sealed partial class AssistantSamplesTests
{
    [Fact]
    public async Task SPM_254_turns_carry_situational_and_page_context_as_data()
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("projects_search_v1", new { input = new { query = "roadmap", limit = 5 } })
            .RespondWithText("You are looking at the roadmap project.")
            .RespondWithFunctionCall("projects_search_v1", new { input = new { query = "roadmap", limit = 5 } })
            .RespondWithText("Nothing is open.");
        Host.Model.Use(model);
        using var client = Host.CreateClient("spm-254-user");
        client.DefaultRequestHeaders.Add("X-Sample-Name", "Sam Planner");
        client.DefaultRequestHeaders.Add("X-Sample-Roles", "ProjectManager,Viewer");
        var conversationId = await Host.CreateConversationAsync(client);

        var withPage = await Host.SendAsync(client, conversationId, "What am I looking at?", new
        {
            route = "/projects/PRJ-1",
            title = "Roadmap",
            entities = new[] { new { type = "project", id = "PRJ-1", label = "Roadmap" } }
        });
        var withoutPage = await Host.SendAsync(client, conversationId, "And now?", new { route = 12 });

        Assert.Equal("completed", withPage[^1].Data.GetProperty("status").GetString());
        Assert.Equal("completed", withoutPage[^1].Data.GetProperty("status").GetString());
        var first = model.Requests[0].Options!.Instructions!;
        var amsterdam = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam"));
        Assert.Contains("# Situation", first);
        Assert.Contains("- Date: " + amsterdam.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), first);
        Assert.Contains("(Europe/Amsterdam)", first);
        Assert.Contains("- User: Sam Planner", first);
        Assert.Contains("- Roles: ProjectManager, Viewer", first);
        Assert.Contains("<page-data>\n- Route: /projects/PRJ-1\n- Title: Roadmap\n- Entity: project PRJ-1 (Roadmap)\n</page-data>", first);
        Assert.True(first.IndexOf("# User preferences", StringComparison.Ordinal) < first.IndexOf("# Situation", StringComparison.Ordinal));
        // An invalid page context is ignored, not rejected.
        Assert.DoesNotContain("<page-data>", model.Requests[2].Options!.Instructions);

        var audit = Host.Services.GetRequiredService<SampleAssistantAuditLog>().Events
            .Where(evt => evt.ConversationId == conversationId)
            .ToArray();
        Assert.Contains(audit, evt => evt.HadPageContext == true && evt.PageEntityCount == 1 && evt.ContextFactCount == 2);
        Assert.Contains(audit, evt => evt.HadPageContext == false);
        var serialized = JsonSerializer.Serialize(audit);
        Assert.DoesNotContain("Sam Planner", serialized);
        Assert.DoesNotContain("PRJ-1", serialized);
    }
}
