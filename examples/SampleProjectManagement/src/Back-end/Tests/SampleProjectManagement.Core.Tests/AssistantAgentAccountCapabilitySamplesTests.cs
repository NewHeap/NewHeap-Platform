using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Models;
using NSubstitute;
using SampleProjectManagement.Api.Composition;
using SampleProjectManagement.Core.Models.AI;
using SampleProjectManagement.Core.Services;
using SampleProjectManagement.DAL.Entities;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// SPM-296: an agent that acts with the rights of the technical account it answers to gets them only
/// when the account passes the tool's policies and holds the tool's capabilities. The sample's API ties
/// each capability to the tool's own policy, as the guidance recommends: a capability names the right the
/// tool itself needs. This composition shows an application that ties the status tool's manage capability
/// to another right, the application-wide project manage permission, which the operations account lacks
/// although it passes the status tool's own policy.
/// </summary>
public sealed class AssistantAgentAccountCapabilitySamplesTests
{
    /// <summary>The operations account: it may change project statuses, without the application-wide manage permission.</summary>
    private const string OperationsAccount = "operations-account";

    /// <summary>A technical account that also holds the application-wide manage permission.</summary>
    private const string PortfolioAccount = "portfolio-account";

    private const string PermissionClaim = "permission";
    private static readonly Guid DivisionId = Guid.NewGuid();
    private static readonly Guid ProjectId = Guid.NewGuid();

    [Fact]
    public async Task An_account_with_the_status_tools_policy_but_not_its_capability_lends_no_rights_and_a_grant_still_decides()
    {
        await using var provider = CreateProvider();
        await using var scope = provider.CreateAsyncScope();
        var gate = scope.ServiceProvider.GetRequiredService<INhAiToolInvocationGate>();
        var authorizer = scope.ServiceProvider.GetRequiredService<INhAiAgentToolAuthorizer>();
        var statusTool = Assert.Single(new ProjectAiToolsNhAiCatalog().Descriptors, tool => tool.Id == ProjectAiTools.ChangeStatusToolId);

        // An agent in code that holds a grant for the status tool, as the intake reviewer does, and acts
        // with the operations account's rights: the account lends nothing for the tool, so the grant
        // decides, as when the account misses one of the tool's policies.
        var inCode = AgentOf(OperationsAccount) with { Grants = [new NhAiAgentGrant(ProjectAiTools.ChangeStatusToolId)] };
        var granted = await AuthorizeAsync(gate, statusTool, inCode);
        Assert.True(granted.Success);
        Assert.Equal(NhAiGrantSource.Agent, granted.Data.GrantSource);
        Assert.Contains(ProjectAiTools.ManageCapability, granted.Data.CapabilityGrants);

        // The project starter an administrator created has no grant: it is not offered the tool, and a
        // call is refused with the code that names the missing capability.
        var starter = AgentOf(OperationsAccount);
        Assert.Equal(NhAiGrantSource.None, await authorizer.AuthorizeAsync(statusTool, starter, TestContext.Current.CancellationToken));
        Assert.Equal(NhAiToolFailureCodes.CapabilityDenied, Code(await AuthorizeAsync(gate, statusTool, starter)));

        // An account that also holds the application-wide manage permission lends its own rights.
        var portfolio = await AuthorizeAsync(gate, statusTool, AgentOf(PortfolioAccount));
        Assert.True(portfolio.Success);
        Assert.Equal(NhAiGrantSource.Accountable, portfolio.Data.GrantSource);
    }

    [Fact]
    public async Task The_model_and_the_audit_read_the_missing_capability_of_a_refused_status_change()
    {
        await using var provider = CreateProvider();
        await using var scope = provider.CreateAsyncScope();
        var audit = new NhAiCapturedAuditSink();
        var tools = new ServiceCollection()
            .AddSingleton(new ProjectAiTools(
                Substitute.For<IProjectAiReadService>(),
                Substitute.For<IProjectAiMutationService>(),
                Substitute.For<IProjectAiStatusReceiptService>()))
            .AddSingleton<INhAiToolInvoker>(new NhAiToolInvoker(
                scope.ServiceProvider.GetRequiredService<INhAiToolInvocationGate>(),
                [audit]))
            .BuildServiceProvider();
        var changeStatus = Assert.Single(new ProjectAiToolsNhAiCatalog().CreateFunctions(tools), function => function.Name == "projects_change_status_v1");

        object? output;
        using (NhAiAgentInvocationScopes.Begin(AgentOf(OperationsAccount)))
        {
            output = await changeStatus.InvokeAsync(
                new AIFunctionArguments { ["input"] = new ProjectAiStatusChangeInput(ProjectId, ProjectStatus.Active) },
                TestContext.Current.CancellationToken);
        }

        // The model reads the stable code, and the audit records it as the result of the refusal.
        var result = Assert.IsType<JsonElement>(output);
        Assert.Equal(NhAiToolFailureCodes.CapabilityDenied, result.GetProperty(NhAiToolResult.CodePropertyName).GetString());
        var record = Assert.Single(audit.Records);
        Assert.Equal((NhAiOutcomeKind.AuthorizationDenied, NhAiToolFailureCodes.CapabilityDenied), (record.Outcome, record.ResultCode));
    }

    private static ServiceProvider CreateProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor());
        services.AddAuthorization(options =>
        {
            // The status tool's own policy: every account of the division may change its project statuses,
            // as in the sample's test hosts.
            options.AddPolicy(ProjectAiTools.ManagePolicy, policy => policy.RequireAuthenticatedUser());
            options.AddPolicy(
                SampleProjectStarterComposition.ProjectManagePolicy,
                policy => policy.RequireClaim(PermissionClaim, SampleProjectStarterComposition.ProjectManagePolicy));
        });
        // The application rebuilds its technical accounts for every decision.
        services.AddSingleton<INhAiPrincipalFactory, SampleAccounts>();
        services.AddNewHeapPlatformAIAspNet(ai => ai
            .UseToolInvocationPurpose("project-assistance")
            // Another right than the tool's own policy: the application-wide project manage permission.
            .AddCapabilityGrant(ProjectAiTools.ManageCapability, SampleProjectStarterComposition.ProjectManagePolicy));
        return services.BuildServiceProvider();
    }

    private static async Task<TaskResult<NhAiInvocationContext>> AuthorizeAsync(
        INhAiToolInvocationGate gate,
        NhAiToolDescriptor tool,
        NhAiAgentInvocationScope scope)
    {
        using (NhAiAgentInvocationScopes.Begin(scope))
        {
            return await gate.AuthorizeAsync(tool, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// A headless run of an agent on a project that answers to <paramref name="account"/> and acts with its
    /// rights, as the project starter of the administration does.
    /// </summary>
    private static NhAiAgentInvocationScope AgentOf(string account)
    {
        var project = new NhAiSubjectInfo(ProjectSubjects.For(ProjectId), NhAiSubjectInfo.CreateScopeKey(null, DivisionId))
        {
            DivisionId = DivisionId,
            DisplayName = "Website relaunch"
        };
        return new NhAiAgentInvocationScope(
            "sample-project-starter",
            "assistant-agent:sample-project-starter",
            1,
            project,
            account,
            "run-1",
            "project-assistance")
        {
            Accountability = NhAiAgentAccountability.ServiceAccount,
            ActWithAccountableRights = true
        };
    }

    private static string? Code(TaskResult result)
    {
        Assert.False(result.Success);
        return result.GetResultItems().Select(item => item.Name).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
    }

    /// <summary>
    /// The application's technical accounts with their permissions.
    /// </summary>
    private sealed class SampleAccounts : INhAiPrincipalFactory
    {
        public ValueTask<ClaimsPrincipal?> CreateAsync(
            string actorId,
            NhAiSubjectInfo subject,
            CancellationToken cancellationToken = default)
        {
            List<Claim> claims = [new Claim(ClaimTypes.NameIdentifier, actorId)];
            if (actorId == PortfolioAccount)
            {
                claims.Add(new Claim(PermissionClaim, SampleProjectStarterComposition.ProjectManagePolicy));
            }
            else if (actorId != OperationsAccount)
            {
                return ValueTask.FromResult<ClaimsPrincipal?>(null);
            }

            return ValueTask.FromResult<ClaimsPrincipal?>(new ClaimsPrincipal(new ClaimsIdentity(claims, "sample-account")));
        }
    }
}
