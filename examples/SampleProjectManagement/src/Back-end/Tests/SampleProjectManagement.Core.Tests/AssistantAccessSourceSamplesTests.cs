using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.Common.Models;
using SampleProjectManagement.Api.Composition;
using SampleProjectManagement.Core.Services;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// SPM-315: one customer's deployment whose people sign in at the customer's identity provider, while the
/// application adds their rights in its own claims transformation at every request
/// (<see cref="SampleAccessSourceComposition"/>). The application records the rights of each person's last
/// request, and an agent's background work acts with them only while they are younger than the maximum
/// age: the run that delegates from its initiator and the agent that acts with the rights of the
/// project's responsible person. The sample's own API signs in with the NewHeap user store, so this
/// composition is focused on the AI integration.
/// </summary>
public sealed class AssistantAccessSourceSamplesTests
{
    private const string Issuer = "https://identity.customer.example";
    private const string TenantId = "customer-1";
    private const string PermissionClaim = "permission";
    private const string ManagePermission = "app.project.manage";
    private const string Manager = "manager-1";
    private const string Colleague = "colleague-1";
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(30);
    private static readonly Guid DivisionId = Guid.NewGuid();
    private static readonly Guid ProjectId = Guid.NewGuid();

    [Fact]
    public async Task Background_work_acts_with_the_rights_of_the_persons_last_request()
    {
        await using var provider = CreateProvider(new SampleTestClock());
        await using var scope = provider.CreateAsyncScope();
        var gate = scope.ServiceProvider.GetRequiredService<INhAiToolInvocationGate>();
        var manager = await SignInAsync(scope.ServiceProvider, Manager);
        var colleague = await SignInAsync(scope.ServiceProvider, Colleague);

        // A run the manager started on the project delegates from the manager: the rights the claims
        // transformation gave the manager at the last request allow the status change.
        var delegated = await AuthorizeAsync(gate, DelegatingRun(manager));
        Assert.True(delegated.Success);
        Assert.Equal(NhAiGrantSource.Delegated, delegated.Data.GrantSource);
        Assert.Contains(ProjectAiTools.ManageCapability, delegated.Data.CapabilityGrants);

        // An agent that acts with the rights of the project's responsible person uses the same record.
        var accountable = await AuthorizeAsync(gate, ResponsibleRun(manager));
        Assert.True(accountable.Success);
        Assert.Equal(NhAiGrantSource.Accountable, accountable.Data.GrantSource);

        // The colleague may not manage projects, and someone the application never saw has no record: their
        // runs are refused with the platform's usual code.
        Assert.Equal(NhAiAgentFailureCodes.NotGranted, Code(await AuthorizeAsync(gate, DelegatingRun(colleague))));
        Assert.Equal(NhAiAgentFailureCodes.NotGranted, Code(await AuthorizeAsync(gate, DelegatingRun("oidc:someone-else"))));
    }

    [Fact]
    public async Task Rights_older_than_the_maximum_age_or_of_a_person_who_signed_out_lend_nothing()
    {
        var clock = new SampleTestClock();
        await using var provider = CreateProvider(clock);
        await using var scope = provider.CreateAsyncScope();
        var gate = scope.ServiceProvider.GetRequiredService<INhAiToolInvocationGate>();
        var authorizer = scope.ServiceProvider.GetRequiredService<INhAiAgentToolAuthorizer>();
        var statusTool = Assert.Single(new ProjectAiToolsNhAiCatalog().Descriptors, tool => tool.Id == ProjectAiTools.ChangeStatusToolId);
        var manager = await SignInAsync(scope.ServiceProvider, Manager);

        // Half an hour after the manager's last request the rights are too old: the run is not offered the
        // tool and a call is refused, without waiting, because the test moves the application's clock.
        clock.Advance(MaxAge + TimeSpan.FromMinutes(1));
        Assert.Equal(NhAiGrantSource.None, await authorizer.AuthorizeAsync(statusTool, DelegatingRun(manager), TestContext.Current.CancellationToken));
        Assert.Equal(NhAiAgentFailureCodes.NotGranted, Code(await AuthorizeAsync(gate, DelegatingRun(manager))));

        // The manager's next request records fresh rights, and the run may change the status again.
        await SignInAsync(scope.ServiceProvider, Manager);
        Assert.Equal(NhAiGrantSource.Delegated, await authorizer.AuthorizeAsync(statusTool, DelegatingRun(manager), TestContext.Current.CancellationToken));

        // Signing out forgets them at once.
        scope.ServiceProvider.GetRequiredService<SampleAccessContexts>().Forget(manager);
        Assert.Equal(NhAiAgentFailureCodes.NotGranted, Code(await AuthorizeAsync(gate, DelegatingRun(manager))));
    }

    private static ServiceProvider CreateProvider(SampleTestClock clock)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor());
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<IClaimsTransformation, CustomerRights>();
        services.AddAuthorization(options =>
        {
            // The people's rights come from the application's claims, which its claims transformation adds.
            options.AddPolicy(ProjectAiTools.ManagePolicy, policy => policy.RequireClaim(PermissionClaim, ManagePermission));
        });
        services.AddNewHeapPlatformAIAspNet(ai => ai
            .UseToolInvocationPurpose("project-assistance")
            .UseAuthenticatedClaimsForSingleTenant(Issuer, TenantId)
            .AddCapabilityGrant(ProjectAiTools.ManageCapability, ProjectAiTools.ManagePolicy));
        services.AddSampleAccessSource(MaxAge);
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// A request of <paramref name="subject"/> as the host handles it: the identity provider's token, the
    /// application's claims transformation and the middleware that records the rights. Returns the
    /// platform's actor id of the person.
    /// </summary>
    private static async Task<string> SignInAsync(IServiceProvider services, string subject)
    {
        var token = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("iss", Issuer), new Claim("sub", subject)],
            "customer-identity"));
        var httpContext = new DefaultHttpContext
        {
            RequestServices = services,
            User = await services.GetRequiredService<IClaimsTransformation>().TransformAsync(token)
        };

        var application = new ApplicationBuilder(services);
        application.UseSampleAccessContexts();
        application.Run(_ => Task.CompletedTask);
        await application.Build()(httpContext);

        var resolved = await services.GetRequiredService<INhAiAuthenticatedInvocationContextResolver>()
            .ResolveAsync(httpContext, TestContext.Current.CancellationToken);
        Assert.True(resolved.Success);
        return resolved.Data.ActorId;
    }

    private static async Task<TaskResult<NhAiInvocationContext>> AuthorizeAsync(INhAiToolInvocationGate gate, NhAiAgentInvocationScope run)
    {
        var statusTool = Assert.Single(new ProjectAiToolsNhAiCatalog().Descriptors, tool => tool.Id == ProjectAiTools.ChangeStatusToolId);
        using (NhAiAgentInvocationScopes.Begin(run))
        {
            return await gate.AuthorizeAsync(statusTool, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>A run on the project, without grants, that delegates from the person who started it.</summary>
    private static NhAiAgentInvocationScope DelegatingRun(string initiatorActorId)
    {
        return Run() with
        {
            InitiatorActorId = initiatorActorId,
            DelegateFromInitiator = true
        };
    }

    /// <summary>A run of an agent that answers to the project's responsible person and acts with that person's rights.</summary>
    private static NhAiAgentInvocationScope ResponsibleRun(string responsibleActorId)
    {
        return Run() with
        {
            AccountableOwnerId = responsibleActorId,
            Accountability = NhAiAgentAccountability.SubjectResponsible,
            ActWithAccountableRights = true
        };
    }

    private static NhAiAgentInvocationScope Run()
    {
        // In a deployment for one customer the project belongs to that customer's tenant; the rights carry
        // no tenant claim, because the integration projects the one tenant instead of reading a claim.
        var project = new NhAiSubjectInfo(ProjectSubjects.For(ProjectId), NhAiSubjectInfo.CreateScopeKey(TenantId, DivisionId))
        {
            DivisionId = DivisionId,
            TenantId = TenantId,
            DisplayName = "Website relaunch"
        };
        return new NhAiAgentInvocationScope(
            "sample-project-steward",
            "assistant-agent:sample-project-steward",
            1,
            project,
            "assistant-agent:sample-project-steward",
            "run-1",
            "project-assistance");
    }

    private static string? Code(TaskResult result)
    {
        Assert.False(result.Success);
        return result.GetResultItems().Select(item => item.Name).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
    }

    /// <summary>
    /// The application's claims transformation: the identity provider says who signs in, the application
    /// says what they may do. The manager may manage projects; the colleague may not.
    /// </summary>
    private sealed class CustomerRights : IClaimsTransformation
    {
        public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
        {
            var subject = principal.FindFirst("sub")?.Value;
            var identity = new ClaimsIdentity(principal.Claims, principal.Identity?.AuthenticationType);
            if (subject == Manager)
            {
                identity.AddClaim(new Claim(PermissionClaim, ManagePermission));
            }

            return Task.FromResult(new ClaimsPrincipal(identity));
        }
    }
}
