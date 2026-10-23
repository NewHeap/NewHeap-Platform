using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// The administration's endpoints of trigger versions, trigger candidates, agents that start in shadow
/// and consultations that start off (S8 of the agent runs adoption plan) over HTTP: their contracts,
/// and that only administrators read or change them, whatever tenant an administrator is bound to, as
/// for the administration's other endpoints.
/// </summary>
public sealed class AssistantTriggerVersionEndpointTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string CodeAgent = "project-assistant";
    private const string Follower = "project-follower";
    private const string TriggerId = "follower-changed";
    private const string Admin = "admin-1";
    private const string TenantOfAdministrators = "tenant-1";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Administrators_keep_versions_of_a_trigger_and_evaluate_a_candidate_over_http(AssistantTestProvider provider)
    {
        await using var app = await StartAsync(provider);
        using var admin = app.CreateClient(Admin, admin: true);

        // An agent created to start in shadow, with run settings and a trigger.
        var created = await ReadJsonAsync(await admin.PostAsJsonAsync("/api/assistant/admin/agents", NewAgent(startsInShadow: true)), HttpStatusCode.Created);
        Assert.Equal(Follower, created.GetProperty("id").GetString());
        await ReadJsonAsync(await admin.PutAsJsonAsync($"/api/assistant/admin/agents/{Follower}/run-settings", new { accountability = "agent" }));
        var shadow = (await ReadJsonAsync(await admin.GetAsync("/api/assistant/admin/agents"))).EnumerateArray()
            .Single(agent => agent.GetProperty("id").GetString() == Follower)
            .GetProperty("shadow");
        Assert.Equal((true, true), (shadow.GetProperty("shadow").GetBoolean(), shadow.GetProperty("override").GetBoolean()));

        var first = await ReadJsonAsync(await admin.PutAsJsonAsync(TriggerPath(), new { source = "event", @event = "project.changed" }));
        Assert.Equal(1, first.GetProperty("version").GetInt32());
        await AssertFieldErrorAsync(await admin.PutAsJsonAsync(TriggerPath(), new { source = "event", @event = "project.changed", expectedVersion = 1, apply = "later" }), "apply");

        // The change becomes the candidate next to the live version.
        var candidate = await ReadJsonAsync(await admin.PutAsJsonAsync(
            TriggerPath(),
            new { source = "event", @event = "project.changed", debounceSeconds = 30, expectedVersion = 1, apply = "shadow", reason = "Shorter window" }));
        Assert.Equal((2, true, 30), (candidate.GetProperty("version").GetInt32(), candidate.GetProperty("shadow").GetBoolean(), candidate.GetProperty("debounceSeconds").GetInt32()));
        var configuration = await ReadJsonAsync(await admin.GetAsync($"/api/assistant/admin/agents/{Follower}/configuration"));
        var trigger = Assert.Single(configuration.GetProperty("dataTriggers").EnumerateArray());
        Assert.Equal((1, 2), (trigger.GetProperty("version").GetInt32(), trigger.GetProperty("candidate").GetProperty("version").GetInt32()));

        var versions = await ReadJsonAsync(await admin.GetAsync($"{TriggerPath()}/versions"));
        Assert.Equal(
            [(2, "candidate", 30, "Shorter window"), (1, "live", 120, (string?)null)],
            versions.EnumerateArray()
                .Select(item => (item.GetProperty("version").GetInt32(), item.GetProperty("status").GetString(), item.GetProperty("debounceSeconds").GetInt32(), item.GetProperty("reason").GetString()))
                .ToArray());
        var comparison = await ReadJsonAsync(await admin.GetAsync($"{TriggerPath()}/candidate/comparison"));
        Assert.Equal((1, 2, 0), (
            comparison.GetProperty("live").GetProperty("version").GetInt32(),
            comparison.GetProperty("candidate").GetProperty("version").GetInt32(),
            comparison.GetProperty("pairedEvents").GetInt32()));

        // Promotion names the candidate's version.
        await AssertFieldErrorAsync(await admin.PostAsJsonAsync($"{TriggerPath()}/candidate/promote", new { reason = "Ready" }), "version");
        await AssertErrorAsync(await admin.PostAsJsonAsync($"{TriggerPath()}/candidate/promote", new { version = 1 }), HttpStatusCode.Conflict, NhAssistantAdminErrorCodes.VersionConflict);
        var promoted = await ReadJsonAsync(await admin.PostAsJsonAsync($"{TriggerPath()}/candidate/promote", new { version = 2, reason = "Ready" }));
        Assert.Equal((2, 30), (promoted.GetProperty("version").GetInt32(), promoted.GetProperty("debounceSeconds").GetInt32()));
        await AssertErrorAsync(await admin.PostAsJsonAsync($"{TriggerPath()}/candidate/discard", new { version = 2 }), HttpStatusCode.NotFound, NhAssistantErrorCodes.TriggerCandidateNotFound);
        await AssertErrorAsync(await admin.GetAsync($"{TriggerPath()}/candidate/comparison"), HttpStatusCode.NotFound, NhAssistantErrorCodes.TriggerCandidateNotFound);

        // A consultation's state is on or off; the host defines none as data.
        await AssertFieldErrorAsync(await admin.PostAsJsonAsync($"/api/assistant/admin/agents/{Follower}/consultations/project/state", new { state = "paused" }), "state");
        await AssertErrorAsync(
            await admin.PostAsJsonAsync($"/api/assistant/admin/agents/{Follower}/consultations/project/state", new { state = "on" }),
            HttpStatusCode.NotFound,
            NhAssistantErrorCodes.ConsultationNotFound);
    }

    [Fact]
    public async Task People_who_do_not_administer_neither_read_nor_change_trigger_versions_or_consultation_states()
    {
        await using var app = await StartAsync(AssistantTestProvider.PostgreSql);
        using var reader = app.CreateClient("reader-1");

        foreach (var response in await RequestsAsync(reader))
        {
            await AssertErrorAsync(response, HttpStatusCode.Forbidden, NhAssistantAdminErrorCodes.Forbidden);
        }

        using var anonymous = app.CreateClient(user: null, access: false);
        foreach (var response in await RequestsAsync(anonymous))
        {
            using (response)
            {
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            }
        }

        // A person may not let a new agent start in shadow either.
        await AssertErrorAsync(
            await reader.PostAsJsonAsync("/api/assistant/admin/agents", NewAgent(startsInShadow: true)),
            HttpStatusCode.Forbidden,
            NhAssistantAdminErrorCodes.Forbidden);
    }

    [Fact]
    public async Task An_administrator_bound_to_a_tenant_administers_them_as_the_administrations_other_endpoints()
    {
        // The administration keeps no tenant rule (ST14): an administrator whom the AI ASP.NET
        // integration projects into a tenant reads and changes the run configuration as one without.
        await using var app = await StartAsync(AssistantTestProvider.PostgreSql, tenantBound: true);
        using var admin = app.CreateClient(Admin, admin: true);
        using var reader = app.CreateClient("reader-1");

        Assert.Empty((await ReadJsonAsync(await admin.GetAsync($"/api/assistant/admin/agents/{CodeAgent}/triggers/{TriggerId}/versions"))).EnumerateArray());
        await AssertErrorAsync(
            await admin.GetAsync($"/api/assistant/admin/agents/{CodeAgent}/triggers/{TriggerId}/candidate/comparison"),
            HttpStatusCode.NotFound,
            NhAssistantErrorCodes.TriggerCandidateNotFound);
        var saved = await ReadJsonAsync(await admin.PutAsJsonAsync(
            $"/api/assistant/admin/agents/{CodeAgent}/triggers/{TriggerId}",
            new { source = "event", @event = "project.changed" }));
        Assert.Equal(1, saved.GetProperty("version").GetInt32());
        await AssertErrorAsync(
            await admin.PostAsJsonAsync($"/api/assistant/admin/agents/{CodeAgent}/consultations/project/state", new { state = "on" }),
            HttpStatusCode.NotFound,
            NhAssistantErrorCodes.ConsultationNotFound);

        // A person of the same tenant without the administration's permission is still refused.
        await AssertErrorAsync(
            await reader.GetAsync($"/api/assistant/admin/agents/{CodeAgent}/triggers/{TriggerId}/versions"),
            HttpStatusCode.Forbidden,
            NhAssistantAdminErrorCodes.Forbidden);
    }

    private static async Task<IReadOnlyList<HttpResponseMessage>> RequestsAsync(HttpClient client)
    {
        var path = $"/api/assistant/admin/agents/{CodeAgent}/triggers/{TriggerId}";
        return
        [
            await client.GetAsync($"{path}/versions"),
            await client.GetAsync($"{path}/candidate/comparison"),
            await client.PostAsJsonAsync($"{path}/candidate/promote", new { version = 2 }),
            await client.PostAsJsonAsync($"{path}/candidate/discard", new { version = 2 }),
            await client.PutAsJsonAsync(path, new { source = "event", @event = "project.changed", expectedVersion = 1, apply = "shadow" }),
            await client.PostAsJsonAsync($"/api/assistant/admin/agents/{CodeAgent}/consultations/project/state", new { state = "on" })
        ];
    }

    private static string TriggerPath()
    {
        return $"/api/assistant/admin/agents/{Follower}/triggers/{TriggerId}";
    }

    private Task<AssistantWebApplication> StartAsync(AssistantTestProvider provider, bool tenantBound = false)
    {
        return AssistantWebApplication.StartAsync(
            database,
            new NhAiScriptedChatClient(),
            configure: services =>
            {
                if (tenantBound)
                {
                    // Everybody signs in at one issuer and belongs to its tenant, administrators too.
                    services.AddAuthentication(SingleTenantAuthentication.SchemeName)
                        .AddScheme<AuthenticationSchemeOptions, SingleTenantAuthentication>(SingleTenantAuthentication.SchemeName, _ => { });
                    services.AddNewHeapPlatformAIAspNet(ai => ai.UseAuthenticatedClaimsForSingleTenant(AssistantSingleTenantTests.Issuer, TenantOfAdministrators));
                }

                // The agents are configured without background operations, which runs need.
                services.Remove(services.Single(descriptor => descriptor.ImplementationType == typeof(NhAssistantStartupValidator)));
            },
            assistantBuilder: assistant => assistant
                .AddTriggerEvent<ProjectChanged>("project.changed", evt => evt
                    .Description("A project changed.")
                    .Subject(change => new NhAiSubjectRef("project", change.ProjectId.ToString("N")))
                    .Message(change => change.Change)
                    .Dedupe(change => change.EventId.ToString("N")))
                .ConfigureAgent(CodeAgent, run => run.Grant("projects.*")),
            provider: provider);
    }

    private static object NewAgent(bool startsInShadow)
    {
        return new
        {
            id = Follower,
            displayName = "Project follower",
            description = "Follows up changed projects.",
            instructions = "Follow up the project.",
            toolSelectors = new[] { "projects.*" },
            mcpServerIds = Array.Empty<string>(),
            autonomy = "propose",
            startsInShadow
        };
    }

    private static async Task AssertFieldErrorAsync(HttpResponseMessage response, string field)
    {
        using (response)
        {
            var body = await ReadJsonAsync(response, HttpStatusCode.BadRequest);
            Assert.Equal(NhAssistantAdminErrorCodes.ValidationFailed, body.GetProperty("code").GetString());
            Assert.True(body.GetProperty("errors").TryGetProperty(field, out _), $"No field error {field}: {body}");
        }
    }

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        using (response)
        {
            var body = await ReadJsonAsync(response, status);
            Assert.Equal(code, body.GetProperty("code").GetString());
        }
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{response.RequestMessage?.RequestUri} answered {response.StatusCode}: {body}");
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }
}
