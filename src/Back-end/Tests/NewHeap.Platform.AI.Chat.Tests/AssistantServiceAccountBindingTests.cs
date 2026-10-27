using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common;
using NewHeap.Platform.Common.Identity.Claims;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// The account an agent answers to lends its rights to the agent's headless runs, so a person binds it
/// only when they may act for it: the person themself, the agent's account in code, or an account
/// whose rights to the agent's tools the person holds as well; a person who may act as any user binds
/// any. Application code that binds with a
/// technical actor id, which the platform knows as no person, decides on its own. Both relational
/// providers.
/// </summary>
public sealed class AssistantServiceAccountBindingTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string AgentId = "project-assistant";
    private const string Follower = "project-follower";
    private const string ManagePermission = "projects.manage";
    private const string AccountInCode = "service-1";
    private const string PrivilegedAccount = "service-2";
    private const string LimitedAdmin = "admin-limited";
    private const string FullAdmin = "admin-full";
    private const string DeployJob = "deploy-job";
    private const string Impersonator = "admin-impersonator";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task An_administrator_lets_an_agent_answer_only_to_an_account_they_may_act_for(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);

        // The privileged account may manage projects; the limited administrator may not, so binding it
        // would lend the agent's runs a right that administrator does not hold.
        var refused = await ControlAsync(host, LimitedAdmin, user: null, control => control.SetServiceAccountAsync(AgentId, PrivilegedAccount, LimitedAdmin));
        Assert.Equal(NhAssistantErrorCodes.ServiceAccountNotPermitted, Code(refused));
        Assert.Equal(AccountInCode, (await StateAsync(host)).ServiceAccount!.ActorId);
        Assert.DoesNotContain(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentServiceAccountChanged);

        // The request's own user decides when the administrator sent the request.
        var asRequest = await ControlAsync(
            host,
            LimitedAdmin,
            user: Person(LimitedAdmin, ManagePermission),
            control => control.SetServiceAccountAsync(AgentId, PrivilegedAccount, LimitedAdmin));
        Assert.True(asRequest.Success);

        // The limited administrator may bind their own account and the account in code again.
        Assert.True((await ControlAsync(host, LimitedAdmin, user: null, control => control.SetServiceAccountAsync(AgentId, LimitedAdmin, LimitedAdmin))).Success);
        Assert.True((await ControlAsync(host, LimitedAdmin, user: null, control => control.SetServiceAccountAsync(AgentId, AccountInCode, LimitedAdmin))).Success);

        // An administrator who holds the account's rights binds it, so does one who may act as any user,
        // and so does the application's own code with a technical actor id.
        Assert.True((await ControlAsync(host, FullAdmin, user: null, control => control.SetServiceAccountAsync(AgentId, PrivilegedAccount, FullAdmin))).Success);
        Assert.True((await ControlAsync(host, Impersonator, user: null, control => control.SetServiceAccountAsync(AgentId, AccountInCode, Impersonator))).Success);
        Assert.True((await ControlAsync(host, Impersonator, user: null, control => control.SetServiceAccountAsync(AgentId, PrivilegedAccount, Impersonator))).Success);
        Assert.True((await ControlAsync(host, DeployJob, user: null, control => control.SetServiceAccountAsync(AgentId, LimitedAdmin, DeployJob))).Success);
        Assert.True((await ControlAsync(host, DeployJob, user: null, control => control.SetServiceAccountAsync(AgentId, PrivilegedAccount, DeployJob))).Success);

        // An account that may not sign in stays unknown, whoever binds it.
        Assert.Equal(
            NhAssistantErrorCodes.ServiceAccountNotFound,
            Code(await ControlAsync(host, FullAdmin, user: null, control => control.SetServiceAccountAsync(AgentId, "nobody", FullAdmin))));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Run_settings_name_only_an_account_the_administrator_may_act_for(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        await CreateFollowerAsync(host);

        var refused = await SaveSettingsAsync(host, LimitedAdmin, null);
        var saved = await SaveSettingsAsync(host, FullAdmin, null);
        // Saving the bound account again lends nothing new, also for the limited administrator.
        var again = await SaveSettingsAsync(host, LimitedAdmin, saved.Data?.Version);

        Assert.Equal(NhAssistantErrorCodes.ServiceAccountNotPermitted, Code(refused));
        Assert.True(saved.Success);
        Assert.Equal(PrivilegedAccount, saved.Data!.ServiceAccountActorId);
        Assert.True(again.Success);
    }

    private async Task<AssistantTestHost> CreateHostAsync(AssistantTestProvider provider)
    {
        return await AssistantTestHost.CreateAsync(
            database,
            provider,
            new NhAiScriptedChatClient(),
            configure: services =>
            {
                // Only an account with the permission manages projects.
                services.AddAuthorization(options => options.AddPolicy(
                    TestProjectToolCatalog.ManagePolicy,
                    policy => policy.RequireClaim("permission", ManagePermission)));
                services.AddSingleton(new TestSubjects());
                services.AddSingleton<TestSkillRuleScript>();
                services.AddSingleton<RecordingOperations>();
                services.AddSingleton(provider => provider.GetRequiredService<RecordingOperations>().Service);
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, RightsPrincipalFactory>());
            },
            assistantBuilder: assistant => assistant
                .AddSubjectType<TestSubjectResolver>()
                .AddApprovalPolicy("project-review", policy => policy
                    .Mode(NhAiApprovalMode.Deferred)
                    .Lifetime(TimeSpan.FromDays(3))
                    .Approvers(NhAiApprovers.SubjectRole(NhAiSubjectRole.Approver)))
                .AddRuleHandler<TestSkillRules>("project-follow-up", "Follows up a project by rule.")
                .ConfigureAgent(AgentId, agent => agent
                    .ServiceAccount(AccountInCode)
                    .ActWithAccountableRights()
                    .UseApprovalPolicy("project-review")));
    }

    private static async Task CreateFollowerAsync(AssistantTestHost host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var created = await scope.ServiceProvider.GetRequiredService<NhAssistantAgentAdministration>().CreateAsync(
            new NhAssistantAgentInput(
                Follower,
                "Project follower",
                "Follows up changed projects.",
                "Follow up the project.",
                ["projects.*"],
                [],
                null,
                NhAiAutonomyLevel.Execute),
            FullAdmin,
            CancellationToken.None);
        Assert.True(created.Success);
    }

    private static async Task<TaskResult<NhAssistantAgentRunSettingsDto>> SaveSettingsAsync(AssistantTestHost host, string actorId, int? expectedVersion)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INhAssistantAgentConfiguration>().SaveRunSettingsAsync(
            Follower,
            new NhAssistantAgentRunSettingsRequest(
                NhAssistantAccountabilities.ServiceAccount,
                PrivilegedAccount,
                ApprovalPolicy: "project-review",
                RuleHandler: "project-follow-up",
                Reason: "Follow up projects",
                ExpectedVersion: expectedVersion),
            actorId);
    }

    /// <summary>
    /// Changes the agent as <paramref name="actorId"/>: in that person's own request when
    /// <paramref name="user"/> is given, otherwise from code without a request.
    /// </summary>
    private static async Task<T> ControlAsync<T>(
        AssistantTestHost host,
        string actorId,
        ClaimsPrincipal? user,
        Func<INhAssistantAgentControl, Task<T>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = user is null ? null : new DefaultHttpContext { RequestServices = scope.ServiceProvider, User = user };
        try
        {
            return await action(scope.ServiceProvider.GetRequiredService<INhAssistantAgentControl>());
        }
        finally
        {
            accessor.HttpContext = null;
        }
    }

    private static async Task<NhAssistantAgentState> StateAsync(AssistantTestHost host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<INhAssistantAgentControl>().GetAsync(AgentId, CancellationToken.None))!;
    }

    private static string? Code(TaskResult result)
    {
        Assert.False(result.Success);
        return result.GetResultItems().Select(item => item.Name).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
    }

    private static ClaimsPrincipal Person(string actorId, params string[] permissions)
    {
        return new ClaimsPrincipal(new ClaimsIdentity(
            permissions.Select(permission => new Claim("permission", permission)).Prepend(new Claim(ClaimTypes.NameIdentifier, actorId)),
            "test"));
    }

    /// <summary>
    /// The people and accounts as the application rebuilds them: the privileged account and the full
    /// administrator may manage projects; a technical actor id such as a deployment job is no person.
    /// </summary>
    private sealed class RightsPrincipalFactory : INhAiPrincipalFactory
    {
        public ValueTask<ClaimsPrincipal?> CreateAsync(string actorId, NhAiSubjectInfo subject, CancellationToken cancellationToken = default)
        {
            ClaimsPrincipal? principal = actorId switch
            {
                PrivilegedAccount or FullAdmin => Person(actorId, ManagePermission),
                AccountInCode or LimitedAdmin => Person(actorId),
                Impersonator => new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.NameIdentifier, actorId),
                        new Claim(NhPlatformClaimTypes.Permission, Constants.PermissionClaimValues.AuthImpersonateAllowed)
                    ],
                    "test")),
                _ => null
            };
            return ValueTask.FromResult(principal);
        }
    }
}
