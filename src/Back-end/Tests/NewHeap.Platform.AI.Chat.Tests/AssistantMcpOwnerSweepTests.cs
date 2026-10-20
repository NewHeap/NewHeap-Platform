using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.Mcp;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// The owner sweep (I16): the MCP maintenance removes the personal connections of people the
/// platform no longer resolves, in batches and with audit, long before the retention period would.
/// </summary>
public sealed partial class AssistantMcpOAuthTests
{
    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Connections_of_people_the_platform_no_longer_resolves_are_removed_in_batches(AssistantTestProvider provider)
    {
        var people = new SweepPrincipals();
        await using var authorization = await NhAiTestAuthorizationServer.StartAsync();
        authorization.RegisterClient("assistant-web", WebClientSecret, RedirectUri);
        await using var mcp = await ProtectedMcpServer.StartAsync(authorization);
        await using var host = await CreateSweepHostAsync(provider, people);
        await AddPersonalServerAsync(host, mcp.Url, authorization);
        foreach (var person in new[] { AssistantTestHost.UserId, "member-2", "member-3" })
        {
            await McpOAuthTestFlows.ConnectPersonAsync(host, person, "planning");
        }
        await using (var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext())
        {
            // The second person consented to background use of the server.
            await context.McpConnections
                .Where(row => row.OwnerActorId == "member-2")
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.AllowBackgroundUse, true));
        }
        // Both may no longer sign in, or were removed: the platform's principal factory no longer
        // resolves them.
        people.Unresolved.Add("member-2");
        people.Unresolved.Add("member-3");
        var maintenance = new NhAssistantMcpMaintenance(
            host.Services.GetRequiredService<IServiceScopeFactory>(),
            host.Services.GetRequiredService<NhAssistantRegistrationState>(),
            NullLogger<NhAssistantMcpMaintenance>.Instance);

        people.Asked.Clear();
        people.Subjects.Clear();

        // A pass starts one interval after start-up, not at once.
        await maintenance.RunOnceAsync(CancellationToken.None);
        Assert.Equal(NhAssistantMcpConnectionStatuses.Active, (await FindPersonConnectionAsync(host, "member-3"))!.Status);
        Assert.Empty(people.Asked);

        // The sweep walks the owners in batches, each person once.
        var batches = new List<NhAssistantMcpOwnerSweepBatch>();
        string? cursor = null;
        do
        {
            await using var scope = host.Services.CreateAsyncScope();
            var batch = await scope.ServiceProvider.GetRequiredService<NhAssistantMcpOwnerSweep>().RunBatchAsync(cursor, 2, CancellationToken.None);
            batches.Add(batch);
            cursor = batch.Checked < 2 ? null : batch.LastOwner;
        }
        while (cursor is not null);

        Assert.Equal(4, batches.Sum(batch => batch.Checked));
        Assert.Equal(2, batches.Sum(batch => batch.Removed));
        Assert.Equal(["admin-1", "member-2", "member-3", AssistantTestHost.UserId], people.Asked.Order(StringComparer.Ordinal));
        Assert.All(people.Subjects, subject => Assert.True(NhAiSystemSubjects.IsGlobal(subject.Subject)));
        // The resolved people keep their connections.
        Assert.Equal(NhAssistantMcpConnectionStatuses.Active, (await FindPersonConnectionAsync(host, AssistantTestHost.UserId))!.Status);
        Assert.Equal(NhAssistantMcpConnectionStatuses.Active, (await FindPersonConnectionAsync(host, "admin-1"))!.Status);
        // The others lose their tokens, which are revoked; a consent to background use stays without them.
        Assert.Null(await FindPersonConnectionAsync(host, "member-3"));
        var consented = (await FindPersonConnectionAsync(host, "member-2"))!;
        Assert.Equal((null, null, null, true), (consented.Status, consented.ProtectedAccessToken, consented.ProtectedRefreshToken, consented.AllowBackgroundUse));
        Assert.Contains("refresh_token", authorization.Revocations);
        foreach (var person in new[] { "member-2", "member-3" })
        {
            Assert.Single(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.McpConnectionDisconnected
                && evt.ActorId == person
                && evt.ObjectId == "planning"
                && evt.ResultCode == NhAssistantMcpOwnerSweep.UnresolvedReason);
        }

        // A second pass finds nothing more to remove, and a maintenance run that is due starts one.
        people.Asked.Clear();
        await maintenance.RunOnceAsync(DateTimeOffset.UtcNow + NhAssistantMcpMaintenance.OwnerPassInterval, CancellationToken.None);
        Assert.Equal(["admin-1", AssistantTestHost.UserId], people.Asked.Order(StringComparer.Ordinal));
        Assert.Equal(2, host.Business.Events.Count(evt => evt.ResultCode == NhAssistantMcpOwnerSweep.UnresolvedReason));
    }

    [Fact]
    public async Task The_owner_sweep_removes_nothing_when_the_platform_cannot_resolve_anyone()
    {
        // Without the NewHeap user manager the built-in principal factory resolves nobody; the sweep
        // then leaves every connection alone instead of removing them all.
        await using var authorization = await NhAiTestAuthorizationServer.StartAsync();
        authorization.RegisterClient("assistant-web", WebClientSecret, RedirectUri);
        await using var mcp = await ProtectedMcpServer.StartAsync(authorization);
        await using var host = await CreateHostAsync(AssistantTestProvider.PostgreSql);
        await AddPersonalServerAsync(host, mcp.Url, authorization);
        await McpOAuthTestFlows.ConnectPersonAsync(host, AssistantTestHost.UserId, "planning");

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var batch = await scope.ServiceProvider.GetRequiredService<NhAssistantMcpOwnerSweep>().RunBatchAsync(null, 100, CancellationToken.None);
            Assert.Equal(0, batch.Checked);
        }

        Assert.Equal(NhAssistantMcpConnectionStatuses.Active, (await FindPersonConnectionAsync(host, AssistantTestHost.UserId))!.Status);
        Assert.DoesNotContain(host.Business.Events, evt => evt.ResultCode == NhAssistantMcpOwnerSweep.UnresolvedReason);
    }

    private Task<AssistantTestHost> CreateSweepHostAsync(AssistantTestProvider provider, SweepPrincipals people)
    {
        return AssistantTestHost.CreateAsync(
            database,
            provider,
            new NhAiScriptedChatClient(),
            configure: services => services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory>(people)),
            assistantBuilder: assistant => assistant.ConfigureMcp(options =>
            {
                options.RequireHttps = true;
                options.OAuth.RedirectUri = RedirectUri;
            }));
    }

    /// <summary>Resolves everyone except the people in <see cref="Unresolved"/>, and records who was asked.</summary>
    private sealed class SweepPrincipals : INhAiPrincipalFactory
    {
        public HashSet<string> Unresolved { get; } = new(StringComparer.Ordinal);

        public List<string> Asked { get; } = [];

        public List<NhAiSubjectInfo> Subjects { get; } = [];

        public ValueTask<ClaimsPrincipal?> CreateAsync(string actorId, NhAiSubjectInfo subject, CancellationToken cancellationToken = default)
        {
            lock (Asked)
            {
                Asked.Add(actorId);
                Subjects.Add(subject);
            }
            return ValueTask.FromResult(Unresolved.Contains(actorId)
                ? null
                : new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actorId)], "test")));
        }
    }
}
