using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.Chat.AspNet.Mcp;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Actors;
using Xunit;
using static NewHeap.Platform.AI.Chat.Tests.Infrastructure.ServerSentEventReader;

namespace NewHeap.Platform.AI.Chat.Tests;

public sealed class AssistantAdminEndpointTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string McpUrl = "http://localhost/planning/mcp";

    [Fact]
    public async Task Preferences_are_personal_validated_and_follow_the_contract()
    {
        await using var app = await StartAsync();
        using var user = app.CreateClient("user-1");
        using var other = app.CreateClient("user-2");

        var defaults = await ReadJsonAsync(await user.GetAsync("/api/assistant/preferences"));
        using var saved = await user.PutAsync("/api/assistant/preferences", Json(new
        {
            style = "direct",
            addressForm = "formal",
            responseLength = "short",
            customInstructions = "Use bullet points."
        }));
        using var invalid = await user.PutAsync("/api/assistant/preferences", Json(new
        {
            style = "loud",
            addressForm = "formal",
            responseLength = "short",
            customInstructions = (string?)null
        }));
        var otherDefaults = await ReadJsonAsync(await other.GetAsync("/api/assistant/preferences"));

        AssertNames(defaults, "style", "addressForm", "responseLength", "customInstructions");
        Assert.Equal("default", defaults.GetProperty("style").GetString());
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal("Use bullet points.", (await ReadJsonAsync(saved)).GetProperty("customInstructions").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var invalidBody = await ReadJsonAsync(invalid);
        Assert.Equal("assistant-validation", invalidBody.GetProperty("code").GetString());
        Assert.Equal("nh-assistant.errors.assistant-validation", invalidBody.GetProperty("messageKey").GetString());
        AssertNames(invalidBody.GetProperty("errors"), "style");
        Assert.Equal("invalid", invalidBody.GetProperty("errors").GetProperty("style")[0].GetString());
        Assert.Equal("default", otherDefaults.GetProperty("style").GetString());
    }

    [Fact]
    public async Task Administration_requires_the_admin_policy_and_status_reports_it()
    {
        await using var app = await StartAsync();
        using var anonymous = app.CreateClient(user: null, access: false);
        using var user = app.CreateClient();
        using var admin = app.CreateClient(admin: true);

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/assistant/admin/agents")).StatusCode);
        using var forbidden = await user.GetAsync("/api/assistant/admin/agents");
        using var forbiddenWrite = await user.PutAsync("/api/assistant/admin/context", Json(new { text = "x", expectedVersion = 0 }));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        var forbiddenBody = await ReadJsonAsync(forbidden);
        AssertNames(forbiddenBody, "code", "messageKey");
        Assert.Equal("assistant-forbidden", forbiddenBody.GetProperty("code").GetString());
        Assert.Equal("nh-assistant.errors.assistant-forbidden", forbiddenBody.GetProperty("messageKey").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenWrite.StatusCode);
        Assert.Equal("assistant-forbidden", (await ReadJsonAsync(forbiddenWrite)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/assistant/admin/agents")).StatusCode);
        Assert.False((await ReadJsonAsync(await user.GetAsync("/api/assistant/status"))).GetProperty("canAdminister").GetBoolean());
        Assert.True((await ReadJsonAsync(await admin.GetAsync("/api/assistant/status"))).GetProperty("canAdminister").GetBoolean());
    }

    [Fact]
    public async Task A_disabled_assistant_hides_preferences_and_administration()
    {
        await using var app = await StartAsync(enabled: false);
        using var admin = app.CreateClient(admin: true);

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/assistant/preferences")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/assistant/admin/context")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/assistant/admin/mcp-servers")).StatusCode);
        Assert.False((await ReadJsonAsync(await admin.GetAsync("/api/assistant/status"))).GetProperty("canAdminister").GetBoolean());
    }

    [Fact]
    public async Task The_application_context_is_versioned_over_http()
    {
        await using var app = await StartAsync();
        using var admin = app.CreateClient(admin: true);

        using var missing = await admin.GetAsync("/api/assistant/admin/context");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("assistant-context-not-found", (await ReadJsonAsync(missing)).GetProperty("code").GetString());
        using var tooLong = await admin.PutAsync("/api/assistant/admin/context", Json(new { text = new string('x', 20_001), expectedVersion = 0 }));
        using var withoutVersion = await admin.PutAsync("/api/assistant/admin/context", Json(new { text = "Projects." }));
        using var first = await admin.PutAsync("/api/assistant/admin/context", Json(new { text = "Projects belong to divisions.", expectedVersion = 0 }));
        using var stale = await admin.PutAsync("/api/assistant/admin/context", Json(new { text = "Stale.", expectedVersion = 0 }));
        using var second = await admin.PutAsync("/api/assistant/admin/context", Json(new { text = "Projects and tasks belong to divisions.", expectedVersion = 1 }));
        var current = await ReadJsonAsync(await admin.GetAsync("/api/assistant/admin/context"));
        var versions = await ReadJsonAsync(await admin.GetAsync("/api/assistant/admin/context/versions"));

        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal("too-long", (await ReadJsonAsync(tooLong)).GetProperty("errors").GetProperty("text")[0].GetString());
        Assert.Equal("required", (await ReadJsonAsync(withoutVersion)).GetProperty("errors").GetProperty("expectedVersion")[0].GetString());
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("assistant-version-conflict", (await ReadJsonAsync(stale)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        AssertNames(current, "text", "version", "hash", "updatedAt", "updatedBy");
        Assert.Equal(2, current.GetProperty("version").GetInt32());
        Assert.Equal("user-1", current.GetProperty("updatedBy").GetString());
        Assert.Equal(2, versions.GetArrayLength());
        AssertNames(versions[0], "version", "hash", "updatedAt", "updatedBy");
    }

    [Fact]
    public async Task Agents_are_administered_with_versions_sources_and_validation()
    {
        await using var app = await StartAsync();
        using var admin = app.CreateClient(admin: true);
        var agentInput = new
        {
            id = "budget-helper",
            displayName = "Budget helper",
            description = "Answers budget questions.",
            instructions = "Explain budgets briefly.",
            toolSelectors = new[] { "projects.search" },
            mcpServerIds = Array.Empty<string>(),
            requiredPolicy = (string?)null,
            autonomy = "observe",
            isEnabled = true
        };

        var list = await ReadJsonAsync(await admin.GetAsync("/api/assistant/admin/agents"));
        using var created = await admin.PostAsync("/api/assistant/admin/agents", Json(agentInput));
        using var duplicate = await admin.PostAsync("/api/assistant/admin/agents", Json(agentInput));
        using var badSelector = await admin.PostAsync("/api/assistant/admin/agents", Json(agentInput with { id = "x-agent", toolSelectors = new[] { "*" } }));
        using var unknownPolicy = await admin.PostAsync("/api/assistant/admin/agents", Json(agentInput with { id = "y-agent", requiredPolicy = "app.unknown" }));
        using var unknownProfile = await admin.PostAsync("/api/assistant/admin/agents", Json(new
        {
            id = "z-agent", agentInput.displayName, agentInput.description, agentInput.instructions,
            agentInput.toolSelectors, agentInput.mcpServerIds, agentInput.requiredPolicy, agentInput.autonomy,
            profile = "missing-chat"
        }));
        using var classified = await admin.PostAsync("/api/assistant/admin/agents", Json(new
        {
            id = "figures-helper", agentInput.displayName, agentInput.description, agentInput.instructions,
            agentInput.toolSelectors, agentInput.mcpServerIds, agentInput.requiredPolicy, agentInput.autonomy,
            dataClassification = "confidential"
        }));
        using var unknownClassification = await admin.PostAsync("/api/assistant/admin/agents", Json(new
        {
            id = "secret-helper", agentInput.displayName, agentInput.description, agentInput.instructions,
            agentInput.toolSelectors, agentInput.mcpServerIds, agentInput.requiredPolicy, agentInput.autonomy,
            dataClassification = "secret"
        }));
        using var noVersion = await admin.PutAsync("/api/assistant/admin/agents/budget-helper", Json(agentInput));
        using var updated = await admin.PutAsync("/api/assistant/admin/agents/budget-helper", Json(new
        {
            agentInput.id, agentInput.displayName, agentInput.description, instructions = "Explain budgets in one sentence.",
            agentInput.toolSelectors, agentInput.mcpServerIds, agentInput.requiredPolicy, agentInput.autonomy, agentInput.isEnabled,
            expectedVersion = 1
        }));
        using var stale = await admin.PutAsync("/api/assistant/admin/agents/budget-helper", Json(new
        {
            agentInput.id, agentInput.displayName, agentInput.description, agentInput.instructions,
            agentInput.toolSelectors, agentInput.mcpServerIds, agentInput.requiredPolicy, agentInput.autonomy, agentInput.isEnabled,
            expectedVersion = 1
        }));
        using var resetAdmin = await admin.PostAsync("/api/assistant/admin/agents/budget-helper/reset", null);
        using var resetCode = await admin.PostAsync("/api/assistant/admin/agents/project-assistant/reset", null);
        using var deleteCode = await admin.DeleteAsync("/api/assistant/admin/agents/project-assistant");
        using var unknown = await admin.PutAsync("/api/assistant/admin/agents/no-such-agent", Json(new
        {
            agentInput.id, agentInput.displayName, agentInput.description, agentInput.instructions,
            agentInput.toolSelectors, agentInput.mcpServerIds, agentInput.requiredPolicy, agentInput.autonomy, agentInput.isEnabled,
            expectedVersion = 1
        }));
        var visible = await ReadJsonAsync(await admin.GetAsync("/api/assistant/agents"));
        using var deleteAdmin = await admin.DeleteAsync("/api/assistant/admin/agents/budget-helper");

        var code = list.EnumerateArray().Single(agent => agent.GetProperty("id").GetString() == "project-assistant");
        AssertNames(code, "id", "displayName", "description", "instructions", "toolSelectors", "mcpServerIds", "requiredPolicy",
            "autonomy", "isEnabled", "version", "source", "isOverridden", "instructionsHash", "updatedAt",
            "runState", "stateChangedAt", "stateChangedBy", "stateReason", "waitingInputs", "skills", "shadowOf", "shadow", "serviceAccount",
            "offeredInChat", "profile", "dataClassification", "warnings", "worksOnSubjectTypes", "worksOnAllSubjects", "worksOnApplication",
            "perDivision", "runPolicy", "viewPolicy", "worksOnDeclared", "knownSubjectTypes", "groups", "codeGroups");
        Assert.Equal("active", code.GetProperty("runState").GetString());
        Assert.Equal("project-chat", code.GetProperty("profile").GetString());
        Assert.Equal(JsonValueKind.Null, code.GetProperty("dataClassification").ValueKind);
        Assert.Equal(0, code.GetProperty("warnings").GetArrayLength());
        // An agent created here chooses the classification of its model calls. The chat profile permits
        // only Internal and has no fallback: the agent is saved with a warning.
        Assert.Equal(HttpStatusCode.Created, classified.StatusCode);
        var classifiedBody = await ReadJsonAsync(classified);
        Assert.Equal("confidential", classifiedBody.GetProperty("dataClassification").GetString());
        Assert.Equal(["classification-not-permitted"], classifiedBody.GetProperty("warnings").EnumerateArray().Select(item => item.GetString()).ToArray());
        Assert.Equal(HttpStatusCode.BadRequest, unknownClassification.StatusCode);
        Assert.Equal("invalid", (await ReadJsonAsync(unknownClassification)).GetProperty("errors").GetProperty("dataClassification")[0].GetString());
        Assert.Equal("code", code.GetProperty("source").GetString());
        Assert.Equal("execute", code.GetProperty("autonomy").GetString());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("admin", (await ReadJsonAsync(created)).GetProperty("source").GetString());
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badSelector.StatusCode);
        var badSelectorBody = await ReadJsonAsync(badSelector);
        Assert.Equal("assistant-validation", badSelectorBody.GetProperty("code").GetString());
        AssertNames(badSelectorBody.GetProperty("errors"), "toolSelectors");
        Assert.Equal(HttpStatusCode.BadRequest, unknownPolicy.StatusCode);
        Assert.Equal("not-found", (await ReadJsonAsync(unknownPolicy)).GetProperty("errors").GetProperty("requiredPolicy")[0].GetString());
        // An agent created here uses one of the profiles the run catalog lists.
        Assert.Equal(HttpStatusCode.BadRequest, unknownProfile.StatusCode);
        Assert.Equal("not-found", (await ReadJsonAsync(unknownProfile)).GetProperty("errors").GetProperty("profile")[0].GetString());
        Assert.Equal(HttpStatusCode.BadRequest, noVersion.StatusCode);
        AssertNames((await ReadJsonAsync(noVersion)).GetProperty("errors"), "expectedVersion");
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(2, (await ReadJsonAsync(updated)).GetProperty("version").GetInt32());
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, resetAdmin.StatusCode);
        Assert.Equal(HttpStatusCode.OK, resetCode.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, deleteCode.StatusCode);
        Assert.Equal("assistant-code-agent-not-deletable", (await ReadJsonAsync(deleteCode)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        var unknownBody = await ReadJsonAsync(unknown);
        AssertNames(unknownBody, "code", "messageKey");
        Assert.EndsWith("-not-found", unknownBody.GetProperty("code").GetString());
        Assert.Contains(visible.EnumerateArray(), agent => agent.GetProperty("displayNameKey").GetString() == "Budget helper");
        Assert.Equal(HttpStatusCode.NoContent, deleteAdmin.StatusCode);
    }

    [Fact]
    public async Task The_state_endpoints_pause_stop_switch_off_and_resume_an_agent_without_touching_its_definition()
    {
        await using var app = await StartAsync();
        using var admin = app.CreateClient(admin: true);
        using var user = app.CreateClient();
        const string path = "/api/assistant/admin/agents/project-assistant";

        var initial = await ReadJsonAsync(await admin.GetAsync(path + "/state"));
        using var paused = await admin.PostAsync(path + "/state", Json(new { state = "paused", reason = "Nightly import" }));
        using var missing = await admin.PostAsync(path + "/state", Json(new { reason = "No state" }));
        using var invalid = await admin.PostAsync(path + "/state", Json(new { state = "sleeping" }));
        using var tooLong = await admin.PostAsync(path + "/state", Json(new { state = "paused", reason = new string('r', 501) }));
        using var forbidden = await user.PostAsync(path + "/state", Json(new { state = "stopped" }));
        using var unknown = await admin.GetAsync("/api/assistant/admin/agents/no-such-agent/state");
        var listed = (await ReadJsonAsync(await admin.GetAsync("/api/assistant/admin/agents"))).EnumerateArray()
            .Single(agent => agent.GetProperty("id").GetString() == "project-assistant");
        using var edited = await admin.PutAsync(path, Json(new
        {
            id = "project-assistant",
            displayName = listed.GetProperty("displayName").GetString(),
            description = listed.GetProperty("description").GetString(),
            instructions = "Help with projects briefly.",
            toolSelectors = listed.GetProperty("toolSelectors").EnumerateArray().Select(item => item.GetString()).ToArray(),
            mcpServerIds = Array.Empty<string>(),
            requiredPolicy = (string?)null,
            autonomy = listed.GetProperty("autonomy").GetString(),
            expectedVersion = listed.GetProperty("version").GetInt32()
        }));
        using var off = await admin.PostAsync(path + "/state", Json(new { state = "off", reason = "Not yet" }));
        using var engaged = await admin.PostAsync(path + "/kill-switch", Json(new { engaged = true }));
        var stopped = await ReadJsonAsync(await admin.GetAsync(path + "/state"));
        using var released = await admin.PostAsync(path + "/kill-switch", Json(new { engaged = false }));
        var offAgain = await ReadJsonAsync(await admin.GetAsync(path + "/state"));
        // A paused agent behaves as before: the kill switch stops it and releasing it resumes it.
        using var pausedAgain = await admin.PostAsync(path + "/state", Json(new { state = "paused" }));
        using var engagedAgain = await admin.PostAsync(path + "/kill-switch", Json(new { engaged = true }));
        using var releasedAgain = await admin.PostAsync(path + "/kill-switch", Json(new { engaged = false }));
        var active = await ReadJsonAsync(await admin.GetAsync(path + "/state"));

        AssertNames(initial, "agentId", "runState", "active", "changedAt", "changedBy", "reason", "waitingInputs", "cancelledRuns", "startedRuns", "skills", "shadowOf", "shadow", "serviceAccount");
        Assert.Equal("active", initial.GetProperty("runState").GetString());
        Assert.True(initial.GetProperty("active").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, paused.StatusCode);
        var pausedBody = await ReadJsonAsync(paused);
        Assert.Equal("paused", pausedBody.GetProperty("runState").GetString());
        Assert.Equal("Nightly import", pausedBody.GetProperty("reason").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        AssertNames((await ReadJsonAsync(missing)).GetProperty("errors"), "state");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("invalid", (await ReadJsonAsync(invalid)).GetProperty("errors").GetProperty("state")[0].GetString());
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal("too-long", (await ReadJsonAsync(tooLong)).GetProperty("errors").GetProperty("reason")[0].GetString());
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("paused", listed.GetProperty("runState").GetString());
        Assert.Equal("Nightly import", listed.GetProperty("stateReason").GetString());
        Assert.True(listed.GetProperty("isEnabled").GetBoolean());
        // An editor that read the agent before the pause saves without a conflict, and without
        // isEnabled; the state stays as it was.
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        Assert.Equal("paused", (await ReadJsonAsync(edited)).GetProperty("runState").GetString());
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        var offBody = await ReadJsonAsync(off);
        Assert.Equal(("off", false, "Not yet"), (offBody.GetProperty("runState").GetString(), offBody.GetProperty("active").GetBoolean(), offBody.GetProperty("reason").GetString()));
        Assert.Equal(HttpStatusCode.OK, engaged.StatusCode);
        var engagedBody = await ReadJsonAsync(engaged);
        AssertNames(engagedBody, "agentId", "enabled", "cancelledRuns", "runState");
        Assert.Equal((false, "stopped"), (engagedBody.GetProperty("enabled").GetBoolean(), engagedBody.GetProperty("runState").GetString()));
        Assert.Equal("stopped", stopped.GetProperty("runState").GetString());
        // Releasing the kill switch never switches on an agent that was off before it was engaged.
        var releasedBody = await ReadJsonAsync(released);
        Assert.Equal((true, "off"), (releasedBody.GetProperty("enabled").GetBoolean(), releasedBody.GetProperty("runState").GetString()));
        Assert.Equal("off", offAgain.GetProperty("runState").GetString());
        Assert.Equal(HttpStatusCode.OK, pausedAgain.StatusCode);
        Assert.Equal("stopped", (await ReadJsonAsync(engagedAgain)).GetProperty("runState").GetString());
        Assert.Equal("active", (await ReadJsonAsync(releasedAgain)).GetProperty("runState").GetString());
        Assert.Equal("active", active.GetProperty("runState").GetString());
    }

    [Fact]
    public async Task The_shadow_and_service_account_endpoints_override_the_code_at_runtime()
    {
        await using var app = await AssistantWebApplication.StartAsync(
            database,
            new NhAiScriptedChatClient(),
            configure: services =>
            {
                services.Replace(ServiceDescriptor.Singleton<NewHeap.Platform.AI.AspNet.INhAiPrincipalFactory, KnownAccountPrincipalFactory>());
                // The endpoints change stored overrides only; this host has no background operations
                // for agent runs, which the startup check would require.
                services.Remove(services.Single(descriptor => descriptor.ImplementationType == typeof(NewHeap.Platform.AI.Chat.AspNet.NhAssistantStartupValidator)));
            },
            assistantBuilder: assistant => assistant
                .AddAgent(AssistantTestData.Agent("project-assistant-next"))
                .ConfigureAgent("project-assistant", agent => agent
                    .ServiceAccount(KnownAccountPrincipalFactory.First)
                    .Skill("notes", skill => skill.Description("Writes notes.").Shadow()))
                .ConfigureAgent("project-assistant-next", agent => agent.ShadowOf("project-assistant"))
                .ConfigureAgent("project-manager", _ => { }));
        using var admin = app.CreateClient(admin: true);
        using var user = app.CreateClient();
        const string path = "/api/assistant/admin/agents/project-assistant";

        var initial = await ReadJsonAsync(await admin.GetAsync(path + "/state"));
        using var shadow = await admin.PostAsync(path + "/shadow", Json(new { mode = "shadow", reason = "Evaluate the new instructions" }));
        using var missingMode = await admin.PostAsync(path + "/shadow", Json(new { reason = "No mode" }));
        using var invalidMode = await admin.PostAsync(path + "/shadow", Json(new { mode = "sometimes" }));
        using var forbidden = await user.PostAsync(path + "/shadow", Json(new { mode = "live" }));
        using var variant = await admin.PostAsync("/api/assistant/admin/agents/project-assistant-next/shadow", Json(new { mode = "live" }));
        using var skillLive = await admin.PostAsync(path + "/skills/notes/shadow", Json(new { mode = "live" }));
        using var unknownSkill = await admin.PostAsync(path + "/skills/missing/shadow", Json(new { mode = "live" }));
        using var unknownAccount = await admin.PostAsync(path + "/service-account", Json(new { actorId = "nobody", reason = "Rotate" }));
        using var account = await admin.PostAsync(path + "/service-account", Json(new { actorId = KnownAccountPrincipalFactory.Second, reason = "Rotate" }));
        using var notUsed = await admin.PostAsync("/api/assistant/admin/agents/project-manager/service-account", Json(new { actorId = KnownAccountPrincipalFactory.Second }));
        var listed = (await ReadJsonAsync(await admin.GetAsync("/api/assistant/admin/agents"))).EnumerateArray()
            .Single(agent => agent.GetProperty("id").GetString() == "project-assistant");
        using var back = await admin.PostAsync(path + "/service-account", Json(new { actorId = (string?)null }));
        using var code = await admin.PostAsync(path + "/shadow", Json(new { mode = "code" }));

        AssertNames(initial.GetProperty("shadow"), "shadow", "configured", "override", "locked", "changedAt", "changedBy", "reason");
        AssertNames(initial.GetProperty("serviceAccount"), "actorId", "configuredActorId", "overridden", "changedAt", "changedBy", "reason");
        Assert.False(initial.GetProperty("shadow").GetProperty("shadow").GetBoolean());
        Assert.Equal(KnownAccountPrincipalFactory.First, initial.GetProperty("serviceAccount").GetProperty("actorId").GetString());
        var notes = initial.GetProperty("skills").EnumerateArray().Single();
        Assert.True(notes.GetProperty("shadow").GetProperty("shadow").GetBoolean());
        Assert.True(notes.GetProperty("shadow").GetProperty("configured").GetBoolean());

        Assert.Equal(HttpStatusCode.OK, shadow.StatusCode);
        var shadowed = (await ReadJsonAsync(shadow)).GetProperty("shadow");
        Assert.True(shadowed.GetProperty("shadow").GetBoolean());
        Assert.True(shadowed.GetProperty("override").GetBoolean());
        Assert.Equal("Evaluate the new instructions", shadowed.GetProperty("reason").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, missingMode.StatusCode);
        AssertNames((await ReadJsonAsync(missingMode)).GetProperty("errors"), "mode");
        Assert.Equal(HttpStatusCode.BadRequest, invalidMode.StatusCode);
        Assert.Equal("invalid", (await ReadJsonAsync(invalidMode)).GetProperty("errors").GetProperty("mode")[0].GetString());
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, variant.StatusCode);
        Assert.Equal("assistant-shadow-variant", (await ReadJsonAsync(variant)).GetProperty("code").GetString());
        // The skill is live by itself, but works in shadow while its agent does.
        Assert.Equal(HttpStatusCode.OK, skillLive.StatusCode);
        var skillShadow = (await ReadJsonAsync(skillLive)).GetProperty("shadow");
        Assert.False(skillShadow.GetProperty("override").GetBoolean());
        Assert.True(skillShadow.GetProperty("shadow").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, unknownSkill.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unknownAccount.StatusCode);
        Assert.Equal("assistant-service-account-not-found", (await ReadJsonAsync(unknownAccount)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.OK, account.StatusCode);
        var changed = (await ReadJsonAsync(account)).GetProperty("serviceAccount");
        Assert.Equal(KnownAccountPrincipalFactory.Second, changed.GetProperty("actorId").GetString());
        Assert.Equal(KnownAccountPrincipalFactory.First, changed.GetProperty("configuredActorId").GetString());
        Assert.True(changed.GetProperty("overridden").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, notUsed.StatusCode);
        Assert.Equal("assistant-service-account-not-used", (await ReadJsonAsync(notUsed)).GetProperty("code").GetString());
        Assert.True(listed.GetProperty("shadow").GetProperty("shadow").GetBoolean());
        Assert.Equal(KnownAccountPrincipalFactory.Second, listed.GetProperty("serviceAccount").GetProperty("actorId").GetString());
        Assert.Equal(HttpStatusCode.OK, back.StatusCode);
        Assert.False((await ReadJsonAsync(back)).GetProperty("serviceAccount").GetProperty("overridden").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, code.StatusCode);
        var followsCode = (await ReadJsonAsync(code)).GetProperty("shadow");
        Assert.False(followsCode.GetProperty("shadow").GetBoolean());
        Assert.Equal(JsonValueKind.Null, followsCode.GetProperty("override").ValueKind);
    }

    [Fact]
    public async Task Mcp_servers_and_tools_are_administered_without_exposing_secrets()
    {
        const string secret = "SECRET-HTTP-TOKEN-99";
        var servers = new InMemoryMcpServers();
        servers.Register(McpUrl, () => servers.PlanningTools());
        await using var app = await StartAsync(configure: services =>
            services.Replace(ServiceDescriptor.Singleton<INhAssistantMcpClientFactory>(servers)));
        using var admin = app.CreateClient(admin: true);
        var input = new
        {
            id = "planning",
            displayName = "Planning",
            url = McpUrl,
            authMode = "bearer",
            headerName = (string?)null,
            secret,
            requiredPolicy = (string?)null,
            isEnabled = true
        };

        using var created = await admin.PostAsync("/api/assistant/admin/mcp-servers", Json(input));
        using var blocked = await admin.PostAsync("/api/assistant/admin/mcp-servers", Json(input with { id = "metadata", url = "http://169.254.169.254/mcp" }));
        using var updated = await admin.PutAsync("/api/assistant/admin/mcp-servers/planning", Json(input with { displayName = "Planning 2", secret = (string?)null }));
        using var headless = await admin.PutAsync("/api/assistant/admin/mcp-servers/planning", Json(new { input.id, input.displayName, input.url, input.authMode, input.headerName, input.requiredPolicy, input.isEnabled, headlessUse = "organization" }));
        using var invalidHeadless = await admin.PutAsync("/api/assistant/admin/mcp-servers/planning", Json(new { input.id, input.displayName, input.url, input.authMode, input.headerName, input.requiredPolicy, input.isEnabled, headlessUse = "always" }));
        var test = await ReadJsonAsync(await admin.PostAsync("/api/assistant/admin/mcp-servers/planning/test", null));
        using var sync = await admin.PostAsync("/api/assistant/admin/mcp-servers/planning/sync", null);
        using var enable = await admin.PutAsync(
            "/api/assistant/admin/mcp-servers/planning/tools/Lookup_Order",
            Json(new { isEnabled = true, effect = "read-only", descriptionOverride = "Find the planning of one order." }));
        var tools = await ReadJsonAsync(await admin.GetAsync("/api/assistant/admin/mcp-servers/planning/tools"));
        var catalog = await ReadJsonAsync(await admin.GetAsync("/api/assistant/admin/tools"));
        var listText = await (await admin.GetAsync("/api/assistant/admin/mcp-servers")).Content.ReadAsStringAsync();
        using var deleted = await admin.DeleteAsync("/api/assistant/admin/mcp-servers/planning");
        using var missing = await admin.GetAsync("/api/assistant/admin/mcp-servers/planning/tools");

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var server = await ReadJsonAsync(created);
        AssertNames(server, "id", "displayName", "url", "authMode", "headerName", "hasSecret", "requiredPolicy", "isEnabled",
            "lastSyncAt", "lastSyncStatus", "assignedAgentIds", "headlessUse", "lastConnectAt", "lastConnectCode",
            "userName", "secretReference", "clientCertificateReference", "transport", "connectionScope", "oauth", "authorization",
            "organizationConnection", "reader");
        Assert.Equal(JsonValueKind.Null, server.GetProperty("reader").ValueKind);
        Assert.Equal("streamable-http", server.GetProperty("transport").GetString());
        Assert.True(server.GetProperty("hasSecret").GetBoolean());
        Assert.Equal("never", server.GetProperty("headlessUse").GetString());
        Assert.Equal("organization", (await ReadJsonAsync(headless)).GetProperty("headlessUse").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, invalidHeadless.StatusCode);
        Assert.Equal("invalid", (await ReadJsonAsync(invalidHeadless)).GetProperty("errors").GetProperty("headlessUse")[0].GetString());
        Assert.Equal(HttpStatusCode.BadRequest, blocked.StatusCode);
        Assert.Equal("assistant-mcp-host-blocked", (await ReadJsonAsync(blocked)).GetProperty("code").GetString());
        Assert.True((await ReadJsonAsync(updated)).GetProperty("hasSecret").GetBoolean());
        AssertNames(test, "ok", "code", "toolCount");
        Assert.True(test.GetProperty("ok").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, sync.StatusCode);
        Assert.Equal(HttpStatusCode.OK, enable.StatusCode);
        var lookup = tools.EnumerateArray().Single(tool => tool.GetProperty("remoteName").GetString() == "Lookup_Order");
        AssertNames(lookup, "remoteName", "localId", "description", "descriptionOverride", "isEnabled", "effect", "status", "readOnlyHint");
        Assert.True(lookup.GetProperty("isEnabled").GetBoolean());
        Assert.True(lookup.GetProperty("readOnlyHint").GetBoolean());
        Assert.Equal("mcp.planning.lookup-order", lookup.GetProperty("localId").GetString());
        Assert.False(tools.EnumerateArray().Single(tool => tool.GetProperty("remoteName").GetString() == "reschedule").GetProperty("isEnabled").GetBoolean());
        AssertNames(catalog[0], "id", "source", "effect", "description", "subjectType");
        Assert.Contains(catalog.EnumerateArray(), entry => entry.GetProperty("id").GetString() == "projects.search" && entry.GetProperty("source").GetString() == "local");
        // MCP tools never declare the object they concern.
        Assert.Contains(catalog.EnumerateArray(), entry => entry.GetProperty("id").GetString() == "mcp.planning.lookup-order"
                                                          && entry.GetProperty("source").GetString() == "mcp"
                                                          && entry.GetProperty("subjectType").ValueKind == JsonValueKind.Null);
        Assert.DoesNotContain(secret, listText);
        Assert.Contains("\"lastConnectAt\":\"", listText, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, await sync.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("Bearer " + secret, servers.Connections.Last().Headers["Authorization"]);
    }

    [Fact]
    public async Task Agents_can_be_kept_out_of_the_chat_while_they_keep_working()
    {
        await using var app = await AssistantWebApplication.StartAsync(
            database,
            new NhAiScriptedChatClient(),
            configure: services =>
                // This host has no background operations for agent runs, which the startup check requires.
                services.Remove(services.Single(descriptor => descriptor.ImplementationType == typeof(NewHeap.Platform.AI.Chat.AspNet.NhAssistantStartupValidator))),
            assistantBuilder: assistant => assistant
                .AddAgent(AssistantTestData.Agent("background-worker"))
                .ConfigureAgent("background-worker", agent => agent.HideFromChat()));
        using var admin = app.CreateClient(admin: true);
        using var user = app.CreateClient();
        var input = new
        {
            id = "intake-helper",
            displayName = "Intake helper",
            description = "Follows up new projects.",
            instructions = "Follow up.",
            toolSelectors = new[] { "projects.search" },
            mcpServerIds = Array.Empty<string>(),
            requiredPolicy = (string?)null,
            autonomy = "observe",
            offeredInChat = false
        };

        using var created = await admin.PostAsync("/api/assistant/admin/agents", Json(input));
        // Omitted, the choice stays as it is.
        using var kept = await admin.PutAsync("/api/assistant/admin/agents/intake-helper", Json(new
        {
            input.id, input.displayName, description = "Follows up new projects by rule.", input.instructions,
            input.toolSelectors, input.mcpServerIds, input.requiredPolicy, input.autonomy, expectedVersion = 1
        }));
        var picker = await ReadJsonAsync(await user.GetAsync("/api/assistant/status"));
        using var hiddenChat = await user.PostAsync("/api/assistant/conversations", Json(new { agentId = "intake-helper" }));
        using var codeHiddenChat = await user.PostAsync("/api/assistant/conversations", Json(new { agentId = "background-worker" }));
        var listed = (await ReadJsonAsync(await admin.GetAsync("/api/assistant/admin/agents"))).EnumerateArray()
            .ToDictionary(agent => agent.GetProperty("id").GetString()!, agent => agent.GetProperty("offeredInChat").GetBoolean());
        using var offered = await admin.PutAsync("/api/assistant/admin/agents/intake-helper", Json(new
        {
            input.id, input.displayName, input.description, input.instructions,
            input.toolSelectors, input.mcpServerIds, input.requiredPolicy, input.autonomy, offeredInChat = true, expectedVersion = 2
        }));
        var pickerAfter = await ReadJsonAsync(await user.GetAsync("/api/assistant/status"));
        using var chat = await user.PostAsync("/api/assistant/conversations", Json(new { agentId = "intake-helper" }));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.False((await ReadJsonAsync(created)).GetProperty("offeredInChat").GetBoolean());
        Assert.False((await ReadJsonAsync(kept)).GetProperty("offeredInChat").GetBoolean());
        var shown = picker.GetProperty("agents").EnumerateArray().Select(agent => agent.GetProperty("id").GetString()).ToArray();
        Assert.Contains("project-assistant", shown);
        Assert.DoesNotContain("intake-helper", shown);
        Assert.DoesNotContain("background-worker", shown);
        Assert.Equal(HttpStatusCode.NotFound, hiddenChat.StatusCode);
        Assert.Equal("assistant-agent-not-found", (await ReadJsonAsync(hiddenChat)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.NotFound, codeHiddenChat.StatusCode);
        Assert.False(listed["background-worker"]);
        Assert.False(listed["intake-helper"]);
        Assert.True(listed["project-assistant"]);
        Assert.True((await ReadJsonAsync(offered)).GetProperty("offeredInChat").GetBoolean());
        Assert.Contains(pickerAfter.GetProperty("agents").EnumerateArray(), agent => agent.GetProperty("id").GetString() == "intake-helper");
        Assert.Equal(HttpStatusCode.Created, chat.StatusCode);
    }

    [Fact]
    public async Task Accounts_are_found_by_name_and_policies_explain_themselves()
    {
        await using var app = await AssistantWebApplication.StartAsync(
            database,
            new NhAiScriptedChatClient(),
            configure: services => services
                .AddNewHeapActorSearch<TestAccounts>()
                .AddNewHeapActorDirectory<TestAccounts>(),
            assistantBuilder: assistant => assistant
                .AddApprovalPolicy("project-review", policy => policy
                    .Approvers(NhAiApprovers.SubjectRole(NhAiSubjectRole.Approver), NhAiApprovers.Policy("app.reviewers"))
                    .Lifetime(TimeSpan.FromDays(7))
                    .FourEyes()
                    .Mode(NhAiApprovalMode.Deferred)));
        using var admin = app.CreateClient(admin: true);
        using var user = app.CreateClient();

        var found = await ReadJsonAsync(await admin.GetAsync("/api/assistant/admin/accounts?query=OPER"));
        var named = await ReadJsonAsync(await admin.GetAsync($"/api/assistant/admin/accounts?ids={TestAccounts.Operations},unknown-account"));
        using var tooShort = await admin.GetAsync("/api/assistant/admin/accounts?query=o");
        using var forbidden = await user.GetAsync("/api/assistant/admin/accounts?query=oper");
        var catalog = await ReadJsonAsync(await admin.GetAsync("/api/assistant/admin/run-catalog"));

        var account = Assert.Single(found.EnumerateArray());
        AssertNames(account, "actorId", "displayName", "detail");
        Assert.Equal((TestAccounts.Operations, "Operations account", "operations@example.test"),
            (account.GetProperty("actorId").GetString(), account.GetProperty("displayName").GetString(), account.GetProperty("detail").GetString()));
        Assert.Equal([TestAccounts.Operations], named.EnumerateArray().Select(item => item.GetProperty("actorId").GetString()));
        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);
        AssertNames((await ReadJsonAsync(tooShort)).GetProperty("errors"), "query");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.True(catalog.GetProperty("accountSearch").GetBoolean());
        var review = catalog.GetProperty("approvalPolicySummaries").EnumerateArray()
            .Single(policy => policy.GetProperty("name").GetString() == "project-review");
        Assert.Equal(7 * 24 * 60, review.GetProperty("lifetimeMinutes").GetInt32());
        Assert.True(review.GetProperty("fourEyes").GetBoolean());
        Assert.Equal("deferred", review.GetProperty("mode").GetString());
        Assert.Equal(
            ["subject-role:approver:", "policy::app.reviewers"],
            review.GetProperty("approvers").EnumerateArray().Select(approver =>
                $"{approver.GetProperty("kind").GetString()}:{approver.GetProperty("role").GetString()}:{approver.GetProperty("policy").GetString()}"));

        // Without a search, the administration asks for the account's id.
        await using var plain = await StartAsync();
        using var plainAdmin = plain.CreateClient(admin: true);
        using var unavailable = await plainAdmin.GetAsync("/api/assistant/admin/accounts?query=oper");
        Assert.False((await ReadJsonAsync(await plainAdmin.GetAsync("/api/assistant/admin/run-catalog"))).GetProperty("accountSearch").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, unavailable.StatusCode);
        Assert.Equal("assistant-account-search-unavailable", (await ReadJsonAsync(unavailable)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task An_application_creates_an_agent_the_way_an_administrator_does()
    {
        await using var app = await StartAsync();
        await using var scope = app.Services.CreateAsyncScope();
        var configuration = scope.ServiceProvider.GetRequiredService<NewHeap.Platform.AI.Chat.AspNet.AgentRuns.INhAssistantAgentConfiguration>();
        var request = new NewHeap.Platform.AI.Chat.AspNet.AgentRuns.NhAssistantAgentRequest(
            "package-agent",
            "Package agent",
            "Imported with a package.",
            "Follow up imported work.",
            ["projects.search"],
            NhAiAutonomyLevel.Propose,
            OfferedInChat: false);

        var created = await configuration.CreateAgentAsync(request, "importer", CancellationToken.None);
        var again = await configuration.CreateAgentAsync(request, "importer", CancellationToken.None);
        var unknownPolicy = await configuration.CreateAgentAsync(request with { Id = "other-agent", RequiredPolicy = "app.unknown" }, "importer", CancellationToken.None);
        using var admin = app.CreateClient(admin: true);
        var listed = (await ReadJsonAsync(await admin.GetAsync("/api/assistant/admin/agents"))).EnumerateArray()
            .Single(agent => agent.GetProperty("id").GetString() == "package-agent");

        Assert.True(created.Success);
        Assert.Equal(("package-agent", "admin", null), (created.Data!.AgentId, created.Data.Source, created.Data.RunSettings));
        Assert.False(again.Success);
        Assert.Equal("assistant-agent-exists", again.GetResultItems().First().Name);
        Assert.False(unknownPolicy.Success);
        Assert.False(listed.GetProperty("offeredInChat").GetBoolean());
        Assert.Equal("propose", listed.GetProperty("autonomy").GetString());
    }

    [Fact]
    public async Task Mcp_server_credentials_and_transport_follow_the_contract()
    {
        await using var app = await StartAsync();
        using var admin = app.CreateClient(admin: true);
        var input = new
        {
            id = "planning",
            displayName = "Planning",
            url = McpUrl,
            authMode = "basic",
            headerName = (string?)null,
            secret = "BASIC-PASSWORD-77",
            requiredPolicy = (string?)null,
            isEnabled = true,
            userName = (string?)null,
            transport = (string?)null
        };

        using var withoutUser = await admin.PostAsync("/api/assistant/admin/mcp-servers", Json(input));
        using var created = await admin.PostAsync("/api/assistant/admin/mcp-servers", Json(input with { userName = "planner" }));
        using var auto = await admin.PutAsync("/api/assistant/admin/mcp-servers/planning", Json(input with { userName = "planner", secret = (string?)null, transport = "auto" }));
        using var unknownResolver = await admin.PutAsync("/api/assistant/admin/mcp-servers/planning", Json(new { input.id, input.displayName, input.url, authMode = "bearer", input.isEnabled, secretReference = "vault:planning" }));
        using var unknownCertificate = await admin.PutAsync("/api/assistant/admin/mcp-servers/planning", Json(new { input.id, input.displayName, input.url, input.authMode, input.isEnabled, userName = "planner", clientCertificateReference = "missing" }));
        var listText = await (await admin.GetAsync("/api/assistant/admin/mcp-servers")).Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, withoutUser.StatusCode);
        Assert.Equal("required", (await ReadJsonAsync(withoutUser)).GetProperty("errors").GetProperty("userName")[0].GetString());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var server = await ReadJsonAsync(created);
        Assert.Equal("planner", server.GetProperty("userName").GetString());
        Assert.True(server.GetProperty("hasSecret").GetBoolean());
        Assert.Equal(JsonValueKind.Null, server.GetProperty("secretReference").ValueKind);
        Assert.Equal(HttpStatusCode.OK, auto.StatusCode);
        Assert.Equal("auto", (await ReadJsonAsync(auto)).GetProperty("transport").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, unknownResolver.StatusCode);
        Assert.Equal("not-found", (await ReadJsonAsync(unknownResolver)).GetProperty("errors").GetProperty("secretReference")[0].GetString());
        Assert.Equal(HttpStatusCode.BadRequest, unknownCertificate.StatusCode);
        Assert.Equal("not-found", (await ReadJsonAsync(unknownCertificate)).GetProperty("errors").GetProperty("clientCertificateReference")[0].GetString());
        Assert.DoesNotContain("BASIC-PASSWORD-77", listText);
    }

    [Fact]
    public async Task Mcp_connections_list_personal_servers_and_record_each_callers_own_consent()
    {
        await using var app = await StartAsync(assistantBuilder: assistant => assistant.ConfigureMcp(mcp => mcp.ForwardUserTokenHosts = ["localhost"]));
        using var admin = app.CreateClient(admin: true);
        using var member = app.CreateClient("member-1");
        using var other = app.CreateClient("member-2");
        using var anonymous = app.CreateClient(user: null, access: false);
        var input = new
        {
            id = "planning",
            displayName = "Planning",
            url = McpUrl,
            authMode = "forward-user-token",
            isEnabled = true,
            headlessUse = "personal"
        };
        using var created = await admin.PostAsync("/api/assistant/admin/mcp-servers", Json(input));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        await AssignServerAsync(app, "project-assistant", "planning");

        var before = await ReadJsonAsync(await member.GetAsync("/api/assistant/mcp-connections"));
        using var missingField = await member.PutAsync("/api/assistant/mcp-connections/planning", Json(new { }));
        using var allowed = await member.PutAsync("/api/assistant/mcp-connections/planning", Json(new { allowBackgroundUse = true }));
        using var unknown = await member.PutAsync("/api/assistant/mcp-connections/elsewhere", Json(new { allowBackgroundUse = true }));
        var forOther = await ReadJsonAsync(await other.GetAsync("/api/assistant/mcp-connections"));
        using var moved = await admin.PutAsync("/api/assistant/admin/mcp-servers/planning", Json(input with { url = McpUrl + "/v2" }));
        var afterMove = await ReadJsonAsync(await member.GetAsync("/api/assistant/mcp-connections"));
        using var notSignedIn = await anonymous.GetAsync("/api/assistant/mcp-connections");

        var listed = Assert.Single(before.EnumerateArray());
        AssertNames(listed, "serverId", "displayName", "host", "allowBackgroundUse", "consentedAt", "agentIds",
            "connectionStatus", "grantedScopes", "requiredScopes", "connectedAt", "offersBackgroundUse");
        Assert.Equal("localhost", listed.GetProperty("host").GetString());
        Assert.False(listed.GetProperty("allowBackgroundUse").GetBoolean());
        Assert.Equal(JsonValueKind.Null, listed.GetProperty("consentedAt").ValueKind);
        Assert.Equal("project-assistant", Assert.Single(listed.GetProperty("agentIds").EnumerateArray()).GetString());
        Assert.Equal(HttpStatusCode.BadRequest, missingField.StatusCode);
        Assert.Equal("required", (await ReadJsonAsync(missingField)).GetProperty("errors").GetProperty("allowBackgroundUse")[0].GetString());
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        var consent = await ReadJsonAsync(allowed);
        Assert.True(consent.GetProperty("allowBackgroundUse").GetBoolean());
        Assert.Equal(JsonValueKind.String, consent.GetProperty("consentedAt").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("assistant-mcp-server-not-found", (await ReadJsonAsync(unknown)).GetProperty("code").GetString());
        Assert.False(Assert.Single(forOther.EnumerateArray()).GetProperty("allowBackgroundUse").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        Assert.False(Assert.Single(afterMove.EnumerateArray()).GetProperty("allowBackgroundUse").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, notSignedIn.StatusCode);
    }

    [Fact]
    public async Task An_mcp_server_is_set_up_as_reader_once_its_reading_tool_is_synced()
    {
        var servers = new InMemoryMcpServers();
        servers.Register(McpUrl, () => servers.PlanningTools());
        await using var app = await StartAsync(configure: services =>
            services.Replace(ServiceDescriptor.Singleton<INhAssistantMcpClientFactory>(servers)));
        using var admin = app.CreateClient(admin: true);
        var input = new
        {
            id = "planning",
            displayName = "Planning",
            url = McpUrl,
            authMode = "none",
            isEnabled = true,
            reader = new
            {
                mediaTypes = new[] { "application/vnd.openxmlformats-officedocument.wordprocessingml.document" },
                toolName = "Lookup_Order",
                maxClassification = "internal"
            }
        };

        using var beforeSync = await admin.PostAsync("/api/assistant/admin/mcp-servers", Json(input));
        using var created = await admin.PostAsync("/api/assistant/admin/mcp-servers", Json(new { input.id, input.displayName, input.url, input.authMode, input.isEnabled }));
        using var sync = await admin.PostAsync("/api/assistant/admin/mcp-servers/planning/sync", null);
        using var invalid = await admin.PutAsync("/api/assistant/admin/mcp-servers/planning", Json(input with
        {
            reader = new { mediaTypes = new[] { "word" }, toolName = "Lookup_Order", maxClassification = "secret" }
        }));
        using var reader = await admin.PutAsync("/api/assistant/admin/mcp-servers/planning", Json(input));
        var listed = Assert.Single((await ReadJsonAsync(await admin.GetAsync("/api/assistant/admin/mcp-servers"))).EnumerateArray());
        using var removed = await admin.PutAsync("/api/assistant/admin/mcp-servers/planning", Json(input with
        {
            reader = new { mediaTypes = Array.Empty<string>(), toolName = (string)null!, maxClassification = (string)null! }
        }));

        Assert.Equal(HttpStatusCode.BadRequest, beforeSync.StatusCode);
        Assert.Equal("not-found", (await ReadJsonAsync(beforeSync)).GetProperty("errors").GetProperty("reader.toolName")[0].GetString());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, sync.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var errors = (await ReadJsonAsync(invalid)).GetProperty("errors");
        Assert.Equal("invalid", errors.GetProperty("reader.mediaTypes")[0].GetString());
        Assert.Equal("invalid", errors.GetProperty("reader.maxClassification")[0].GetString());
        Assert.Equal(HttpStatusCode.OK, reader.StatusCode);
        var setting = (await ReadJsonAsync(reader)).GetProperty("reader");
        AssertNames(setting, "mediaTypes", "toolName", "maxClassification");
        Assert.Equal(
            ("application/vnd.openxmlformats-officedocument.wordprocessingml.document", "Lookup_Order", "internal"),
            (Assert.Single(setting.GetProperty("mediaTypes").EnumerateArray()).GetString(), setting.GetProperty("toolName").GetString(), setting.GetProperty("maxClassification").GetString()));
        Assert.Equal("Lookup_Order", listed.GetProperty("reader").GetProperty("toolName").GetString());
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await ReadJsonAsync(removed)).GetProperty("reader").ValueKind);
    }

    private static async Task AssignServerAsync(AssistantWebApplication app, string agentId, string serverId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var agents = scope.ServiceProvider.GetRequiredService<NhAssistantAgentAdministration>();
        var agent = (await scope.ServiceProvider.GetRequiredService<NhAssistantAgentCatalog>().FindAsync(agentId, includeDisabled: true, CancellationToken.None))!;
        var assigned = await agents.UpdateAsync(
            new NhAssistantAgentInput(
                agent.Id,
                agent.Definition.DisplayNameKey,
                agent.Definition.DescriptionKey,
                agent.Definition.Instructions.Content,
                agent.Definition.ToolSelectors,
                [serverId],
                agent.Definition.RequiredPolicy,
                agent.Definition.Autonomy),
            agent.Definition.Version,
            "admin-1",
            CancellationToken.None);
        Assert.True(assigned.Success);
    }

    private Task<AssistantWebApplication> StartAsync(
        bool enabled = true,
        Action<IServiceCollection>? configure = null,
        Action<NhAssistantBuilder>? assistantBuilder = null)
    {
        return AssistantWebApplication.StartAsync(database, new NhAiScriptedChatClient(), enabled, configure: configure, assistantBuilder: assistantBuilder);
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

    private sealed class TestAccounts : INhActorSearch, INhActorDirectory
    {
        public const string Operations = "6d1d5bd4-8a5c-4e70-9d43-0b3f1f43c1a2";

        private static readonly NhActorInfo OperationsAccount = new(new NhActorRef(NhActorKinds.User, Operations), "Operations account")
        {
            Email = "operations@example.test"
        };

        public Task<IReadOnlyList<NhActorInfo>> SearchAsync(NhActorSearchRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<NhActorInfo>>(
                OperationsAccount.DisplayName.Contains(request.Query, StringComparison.OrdinalIgnoreCase) ? [OperationsAccount] : []);
        }

        public Task<IReadOnlyDictionary<NhActorRef, NhActorInfo>> GetAsync(IReadOnlyCollection<NhActorRef> actors, CancellationToken cancellationToken = default)
        {
            IReadOnlyDictionary<NhActorRef, NhActorInfo> known = actors
                .Where(actor => actor == OperationsAccount.Actor)
                .ToDictionary(actor => actor, _ => OperationsAccount);
            return Task.FromResult(known);
        }
    }
}
