using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// The agent package endpoints (PK12): behind the administration policy, a JSON download for the
/// export, the preview and the import with the error contract of the administration, and the unlink.
/// </summary>
public sealed class AssistantAgentPackageEndpointTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string Base = "/api/assistant/";

    [Fact]
    public async Task The_package_endpoints_require_the_administration_policy_and_follow_the_contract()
    {
        await using var app = await AssistantWebApplication.StartAsync(
            database,
            new NhAiScriptedChatClient(),
            configure: services =>
            {
                services.AddSingleton(new TestSubjects());
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, PackagePrincipalFactory>());
                // The endpoints store definitions only; this host runs no background operations.
                services.Remove(services.Single(descriptor => descriptor.ImplementationType == typeof(NhAssistantStartupValidator)));
            },
            assistantBuilder: AssistantAgentPackageTests.Configure);
        using var admin = app.CreateClient(admin: true);
        using var user = app.CreateClient();
        var package = JsonDocument.Parse(NhAssistantAgentPackageFormat.Write(AssistantAgentPackageTests.Desks(locked: true))).RootElement;

        // Without the administration policy every endpoint answers 403 with the contract body.
        foreach (var route in new[] { NhAssistantAgentPackageRoutes.Export, NhAssistantAgentPackageRoutes.Preview, NhAssistantAgentPackageRoutes.Import, "admin/agents/" + AssistantAgentPackageTests.Kickoff + "/package/unlink" })
        {
            using var forbidden = await user.PostAsync(Base + route, Json(new { agentIds = new[] { AssistantAgentPackageTests.Kickoff }, package }));
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
            Assert.Equal("assistant-forbidden", (await ReadJsonAsync(forbidden)).GetProperty("code").GetString());
        }

        // An account that cannot sign in refuses the package; nothing is imported.
        using var refused = await admin.PostAsync(Base + NhAssistantAgentPackageRoutes.Import, Json(new
        {
            package,
            options = new { serviceAccounts = new Dictionary<string, string> { [AssistantAgentPackageTests.Kickoff] = PackagePrincipalFactory.CannotSignIn } }
        }));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var refusal = await ReadJsonAsync(refused);
        Assert.Equal(NhAssistantErrorCodes.PackageRefused, refusal.GetProperty("code").GetString());
        Assert.Equal(
            NhAssistantErrorCodes.ServiceAccountNotFound,
            refusal.GetProperty("errors").GetProperty(AssistantAgentPackageTests.Kickoff + "/runSettings")[0].GetString());

        // A property the format does not define is refused before anything is checked.
        using var unknown = await admin.PostAsync(Base + NhAssistantAgentPackageRoutes.Preview, Json(new
        {
            package = new { format = NhAssistantAgentPackageFormat.Name, formatVersion = 1, package = new { id = "p", version = 1 }, agents = Array.Empty<object>(), signature = "x" }
        }));
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal(NhAssistantErrorCodes.PackageFormatUnsupported, (await ReadJsonAsync(unknown)).GetProperty("code").GetString());

        var options = new { serviceAccounts = new Dictionary<string, string> { [AssistantAgentPackageTests.Kickoff] = PackagePrincipalFactory.Account } };
        using var preview = await admin.PostAsync(Base + NhAssistantAgentPackageRoutes.Preview, Json(new { package, options }));
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var previewed = await ReadJsonAsync(preview);
        Assert.True(previewed.GetProperty("importable").GetBoolean());
        Assert.Equal(
            ["agentId", "part", "outcome", "changedLocally", "code", "errors", "lockedFields", "candidateVersion"],
            previewed.GetProperty("parts")[0].EnumerateObject().Select(property => property.Name).ToArray());
        using var imported = await admin.PostAsync(Base + NhAssistantAgentPackageRoutes.Import, Json(new { package, options }));
        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        Assert.Equal(AssistantAgentPackageTests.PackageId, (await ReadJsonAsync(imported)).GetProperty("packageId").GetString());

        // The administration shows the package and its locks; a save of a locked field is refused.
        var agents = await ReadJsonAsync(await admin.GetAsync(Base + "admin/agents"));
        var kickoff = agents.EnumerateArray().Single(agent => agent.GetProperty("id").GetString() == AssistantAgentPackageTests.Kickoff);
        var link = kickoff.GetProperty("package");
        Assert.Equal(AssistantAgentPackageTests.PackageId, link.GetProperty("packageId").GetString());
        Assert.Contains(link.GetProperty("parts").EnumerateArray(), part => part.GetProperty("part").GetString() == "skill:kickoff"
                                                                            && part.GetProperty("lockedFields")[0].GetString() == "instructions");
        Assert.False(agents.EnumerateArray().Single(agent => agent.GetProperty("id").GetString() == "project-assistant").TryGetProperty("package", out _));
        using var locked = await admin.PutAsync(Base + "admin/agents/" + AssistantAgentPackageTests.Kickoff, Json(new
        {
            displayName = "Kickoff desk",
            description = "Starts projects.",
            instructions = "Start every project at once.",
            toolSelectors = new[] { "projects.*" },
            autonomy = "execute",
            isEnabled = true,
            expectedVersion = 1
        }));
        Assert.Equal(HttpStatusCode.BadRequest, locked.StatusCode);
        Assert.Equal(NhAssistantFieldErrors.Locked, (await ReadJsonAsync(locked)).GetProperty("errors").GetProperty("instructions")[0].GetString());

        // The export downloads the package; references outside the selection come in a header.
        using var export = await admin.PostAsync(Base + NhAssistantAgentPackageRoutes.Export, Json(new { agentIds = new[] { AssistantAgentPackageTests.Kickoff } }));
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal("application/json", export.Content.Headers.ContentType!.MediaType);
        Assert.Equal("agent-package-" + AssistantAgentPackageTests.PackageId + ".json", export.Content.Headers.ContentDisposition!.FileNameStar ?? export.Content.Headers.ContentDisposition.FileName);
        var warnings = JsonDocument.Parse(export.Headers.GetValues(NhAssistantAgentPackageRoutes.WarningsHeader).Single()).RootElement;
        Assert.Equal(AssistantAgentPackageTests.Intake, warnings[0].GetProperty("referencedAgentId").GetString());
        var downloaded = NhAssistantAgentPackageFormat.Read(await export.Content.ReadAsStringAsync());
        Assert.True(downloaded.Success);
        Assert.Equal((AssistantAgentPackageTests.PackageId, 2), (downloaded.Data!.Package.Id, downloaded.Data.Package.Version));

        // Unlinked, the part changes freely.
        using var unlinked = await admin.PostAsync(Base + "admin/agents/" + AssistantAgentPackageTests.Kickoff + "/package/unlink", Json(new { part = "agent", reason = "Our own wording" }));
        Assert.Equal(HttpStatusCode.OK, unlinked.StatusCode);
        Assert.Equal("agent", (await ReadJsonAsync(unlinked)).GetProperty("parts")[0].GetString());
        using var notLinked = await admin.PostAsync(Base + "admin/agents/" + AssistantAgentPackageTests.Kickoff + "/package/unlink", Json(new { part = "agent" }));
        Assert.Equal(HttpStatusCode.NotFound, notLinked.StatusCode);
        Assert.Equal(NhAssistantErrorCodes.PackageNotLinked, (await ReadJsonAsync(notLinked)).GetProperty("code").GetString());
    }

    private static StringContent Json(object value)
    {
        return new StringContent(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json");
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }
}
