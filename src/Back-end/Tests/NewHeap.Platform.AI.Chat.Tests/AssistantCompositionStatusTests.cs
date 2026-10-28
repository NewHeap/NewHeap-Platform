using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// TC8's effective state: <c>GET admin/agents/{id}/compositions</c> says per feature and channel what
/// the agent is offered and why (on, not-installed, agent-not-allowed, off-by-application), asks the
/// application's switch for the caller, and only answers administrators.
/// </summary>
public sealed class AssistantCompositionStatusTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string AllowingAgent = "project-assistant";
    private const string PlainAgent = "project-manager";
    private const string Admin = "admin-1";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task The_state_names_why_each_feature_is_on_or_off_per_channel(AssistantTestProvider provider)
    {
        var switchState = new TestCompositionSwitchState
        {
            Answer = context => context.Channel == NhAssistantCompositionChannel.Chat
        };
        await using var app = await StartAsync(switchState, provider);
        using var admin = app.CreateClient(Admin, admin: true);

        using var allowing = await admin.GetAsync($"/api/assistant/admin/agents/{AllowingAgent}/compositions");
        Assert.Equal(HttpStatusCode.OK, allowing.StatusCode);
        var state = await ReadJsonAsync(allowing);
        AssertNames(state, "agentId", "features");
        Assert.Equal(AllowingAgent, state.GetProperty("agentId").GetString());
        var compositions = Feature(state, "compositions");
        AssertNames(compositions, "channels", "feature", "on", "reason");
        Assert.True(compositions.GetProperty("on").GetBoolean());
        Assert.Equal(NhAssistantCompositionReasons.On, compositions.GetProperty("reason").GetString());
        Assert.Equal(
            ["chat:on", "run:off-by-application", "consultation:off-by-application", "hand-off:off-by-application"],
            Channels(compositions));
        AssertNames(compositions.GetProperty("channels")[0], "channel", "on", "reason");
        // Only a composition is registered: the scripting package is not installed.
        var scripts = Feature(state, "scripts");
        Assert.False(scripts.GetProperty("on").GetBoolean());
        Assert.Equal(NhAssistantCompositionReasons.NotInstalled, scripts.GetProperty("reason").GetString());
        Assert.All(Channels(scripts), channel => Assert.EndsWith(":not-installed", channel, StringComparison.Ordinal));

        // The switch was asked for the reading administrator, once per channel, about compositions only.
        Assert.Equal(4, switchState.Asked.Count);
        Assert.All(switchState.Asked, context =>
        {
            Assert.Equal(NhAiCompositionFeature.Compositions, context.Feature);
            Assert.Equal(AllowingAgent, context.AgentId);
            Assert.Equal(Admin, context.PersonActorId);
        });

        // An agent that code does not allow the feature says so, and the switch is not asked.
        var plain = await ReadJsonAsync(await admin.GetAsync($"/api/assistant/admin/agents/{PlainAgent}/compositions"));
        Assert.Equal(NhAssistantCompositionReasons.AgentNotAllowed, Feature(plain, "compositions").GetProperty("reason").GetString());
        Assert.Equal(NhAssistantCompositionReasons.NotInstalled, Feature(plain, "scripts").GetProperty("reason").GetString());
        Assert.Equal(4, switchState.Asked.Count);

        // The application switches compositions off everywhere.
        switchState.Answer = _ => false;
        var off = await ReadJsonAsync(await admin.GetAsync($"/api/assistant/admin/agents/{AllowingAgent}/compositions"));
        Assert.False(Feature(off, "compositions").GetProperty("on").GetBoolean());
        Assert.Equal(NhAssistantCompositionReasons.OffByApplication, Feature(off, "compositions").GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Only_administrators_read_the_state_and_an_unknown_agent_is_not_found()
    {
        var switchState = new TestCompositionSwitchState();
        await using var app = await StartAsync(switchState, AssistantTestProvider.PostgreSql);
        using var anonymous = app.CreateClient(user: null, access: false);
        using var member = app.CreateClient("member-1");
        using var admin = app.CreateClient(Admin, admin: true);
        var path = $"/api/assistant/admin/agents/{AllowingAgent}/compositions";

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
        using var forbidden = await member.GetAsync(path);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal("assistant-forbidden", (await ReadJsonAsync(forbidden)).GetProperty("code").GetString());
        using var unknown = await admin.GetAsync("/api/assistant/admin/agents/unknown-agent/compositions");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(NhAssistantErrorCodes.AgentNotFound, (await ReadJsonAsync(unknown)).GetProperty("code").GetString());
        // Neither refusal asked the application's switch.
        Assert.Empty(switchState.Asked);
    }

    private async Task<AssistantWebApplication> StartAsync(TestCompositionSwitchState switchState, AssistantTestProvider provider)
    {
        return await AssistantWebApplication.StartAsync(
            database,
            new NhAiScriptedChatClient(),
            provider: provider,
            configure: services =>
            {
                services.AddSingleton<TestCompositionToolRecorder>();
                services.AddSingleton(switchState);
                services.AddNewHeapPlatformAI(ai => ai.AddGeneratedToolCatalog<TestCompositionMutationCatalog>());
            },
            assistantBuilder: assistant => assistant
                .UseCompositionSwitch<TestCompositionSwitch>()
                .ConfigureAgentContext(AllowingAgent, context => context.AllowCompositions().AllowScripts()));
    }

    private static JsonElement Feature(JsonElement state, string feature)
    {
        return state.GetProperty("features").EnumerateArray().Single(item => item.GetProperty("feature").GetString() == feature);
    }

    private static string[] Channels(JsonElement feature)
    {
        return feature.GetProperty("channels").EnumerateArray()
            .Select(channel => channel.GetProperty("channel").GetString() + ":" + channel.GetProperty("reason").GetString())
            .ToArray();
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static void AssertNames(JsonElement element, params string[] expected)
    {
        Assert.Equal(
            expected.Order(StringComparer.Ordinal),
            element.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
    }
}
