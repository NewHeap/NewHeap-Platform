using System.Net;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Test;
using SampleProjectManagement.Api.Composition;
using SampleProjectManagement.Core.Services;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// Executable evidence for data classification in the administration (SPM-307): an administrator
/// creates an agent with its model profile and data classification, people's plain chats with it carry
/// that classification and move to the profile's fallback that permits it, and a classification that
/// no profile permits is saved with a warning and refused at the model call.
/// </summary>
public sealed partial class AssistantSamplesTests
{
    [Fact]
    public async Task SPM_307_an_administrator_creates_an_agent_with_its_profile_and_classification_and_its_chats_move_to_the_fallback()
    {
        Host.Model.Use(new NhAiScriptedChatClient().RespondWithText("The budget figures of the portfolio are on track."));
        using var admin = Host.CreateClient("spm-307-admin", admin: true);
        using var user = Host.CreateClient("spm-307-user");
        var usage = Host.Services.GetRequiredService<ProjectAiBudgetUsage>();

        // The agent works with the chat profile, which permits internal data only, and classifies its
        // model calls as confidential: they move to the fallback that permits it.
        using var created = await admin.PostAsync(
            "/api/assistant/admin/agents",
            ClassifiedAgent("budget-figures", NhAssistantDataClassificationNames.Confidential),
            TestContext.Current.CancellationToken);
        // No profile of the chain permits restricted data: the agent is saved, with a warning.
        using var restricted = await admin.PostAsync(
            "/api/assistant/admin/agents",
            ClassifiedAgent("salary-figures", NhAssistantDataClassificationNames.Restricted),
            TestContext.Current.CancellationToken);
        var answered = await Host.SendAsync(user, await CreateConversationAsync(user, "budget-figures"), "How are the budget figures?");
        var refused = await Host.SendAsync(user, await CreateConversationAsync(user, "salary-figures"), "How are the salary figures?");

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var agent = await AssistantSampleHost.ReadJsonAsync(created);
        Assert.Equal(
            (SampleAssistantComposition.ProfileName, NhAssistantDataClassificationNames.Confidential, 0),
            (agent.GetProperty("profile").GetString(), agent.GetProperty("dataClassification").GetString(), agent.GetProperty("warnings").GetArrayLength()));
        Assert.Equal("completed", answered[^1].Data.GetProperty("status").GetString());
        Assert.Equal([SampleAssistantComposition.ConfidentialProfileName], usage.ProfilesOf("budget-figures"));

        Assert.Equal(HttpStatusCode.Created, restricted.StatusCode);
        Assert.Equal(
            [NhAssistantConfigurationWarnings.ClassificationNotPermitted],
            (await AssistantSampleHost.ReadJsonAsync(restricted)).GetProperty("warnings").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(
            ("failed", NhAssistantErrorCodes.ClassificationNotPermitted),
            (refused[^1].Data.GetProperty("status").GetString(), refused[^1].Data.GetProperty("errorCode").GetString()));
        Assert.Empty(usage.ProfilesOf("salary-figures"));
    }

    private static HttpContent ClassifiedAgent(string id, string dataClassification)
    {
        return AssistantSampleHost.Json(new
        {
            id,
            displayName = "Budget figures",
            description = "Answers questions about the budget figures of the portfolio.",
            instructions = "Answer questions about the budget figures of the portfolio in one sentence.",
            toolSelectors = new[] { "projects.search" },
            mcpServerIds = Array.Empty<string>(),
            requiredPolicy = (string?)null,
            autonomy = "observe",
            profile = SampleAssistantComposition.ProfileName,
            dataClassification
        });
    }
}
