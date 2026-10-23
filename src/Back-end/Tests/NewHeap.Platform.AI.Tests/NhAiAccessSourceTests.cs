using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.AI.Tests;

/// <summary>
/// Principals outside a request from the application's access source (S6): the application keeps the
/// claims its claims transformation produced and when, NewHeap stores nothing, and every decision outside
/// a person's request that asks <see cref="INhAiPrincipalFactory"/> gets those claims only while they are
/// younger than <see cref="NhAiAccessSourceOptions.MaxAge"/> on the application's clock.
/// </summary>
public sealed class NhAiAccessSourceTests
{
    private const string Initiator = "oidc:initiator";
    private const string Account = "oidc:account";
    private const string PermissionClaim = "permission";
    private const string ManagePermission = "projects.manage";
    private const string ReadPermission = "projects.read";
    private const string ToolPolicy = "project-manage";
    private const string CapabilityPolicy = "project-ai-manage";
    private const string Issuer = "https://identity.example";
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(30);
    private static readonly DateTimeOffset SignedIn = new(2026, 10, 23, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid DivisionId = Guid.NewGuid();

    [Fact]
    public async Task A_fresh_context_lends_exactly_the_sources_claims_to_the_initiator_of_a_run()
    {
        var clock = new TestClock(SignedIn.AddMinutes(10));
        var contexts = new ApplicationContexts();
        contexts.Remember(Initiator, SignedIn, Permission(ManagePermission), new Claim("name", "Ada"), new Claim("role", "planner"));
        await using var provider = CreateProvider(contexts, clock, ai => ai.UsePrincipalsFromAccessSource<ApplicationContexts>(options =>
        {
            options.MaxAge = MaxAge;
            options.NameClaimType = "name";
            options.RoleClaimType = "role";
        }));
        await using var scope = provider.CreateAsyncScope();
        var gate = scope.ServiceProvider.GetRequiredService<INhAiToolInvocationGate>();

        // A run that delegates from its initiator, without a request of that person: the source's claims
        // pass the tool's policy and cover its capability.
        TaskResult<NhAiInvocationContext> result;
        using (NhAiAgentInvocationScopes.Begin(DelegatingRun()))
        {
            result = await gate.AuthorizeAsync(MutationTool());
        }

        Assert.True(result.Success);
        Assert.Equal(NhAiGrantSource.Delegated, result.Data.GrantSource);
        Assert.Contains("projects-manage", result.Data.CapabilityGrants);
        var asked = Assert.Single(contexts.Asked.Distinct());
        Assert.Equal((Initiator, new NhAiSubjectRef("project", "alpha")), (asked.ActorId, asked.Subject.Subject));

        // The principal is one authenticated identity of the rebuilt kind with the source's claims and
        // nothing else: no actor id, tenant or right of NewHeap's own.
        var principal = await scope.ServiceProvider.GetRequiredService<INhAiPrincipalFactory>()
            .CreateAsync(Initiator, Project(), CancellationToken.None);
        Assert.NotNull(principal);
        var identity = Assert.Single(principal.Identities);
        Assert.True(identity.IsAuthenticated);
        Assert.Equal("nh-ai-delegation", identity.AuthenticationType);
        Assert.Equal(
            [(PermissionClaim, ManagePermission), ("name", "Ada"), ("role", "planner")],
            identity.Claims.Select(claim => (claim.Type, claim.Value)));
        Assert.Equal("Ada", identity.Name);
        Assert.True(principal.IsInRole("planner"));
    }

    [Fact]
    public async Task The_account_an_agent_answers_to_lends_its_rights_only_while_its_context_is_fresh()
    {
        var clock = new TestClock(SignedIn);
        var contexts = new ApplicationContexts();
        contexts.Remember(Account, SignedIn, Permission(ManagePermission));
        await using var provider = CreateProvider(contexts, clock);
        await using var scope = provider.CreateAsyncScope();
        var gate = scope.ServiceProvider.GetRequiredService<INhAiToolInvocationGate>();
        var authorizer = scope.ServiceProvider.GetRequiredService<INhAiAgentToolAuthorizer>();

        TaskResult<NhAiInvocationContext> fresh;
        using (NhAiAgentInvocationScopes.Begin(AccountableRun()))
        {
            fresh = await gate.AuthorizeAsync(MutationTool());
        }
        Assert.True(fresh.Success);
        Assert.Equal(NhAiGrantSource.Accountable, fresh.Data.GrantSource);
        Assert.Equal(NhAiGrantSource.Accountable, await authorizer.AuthorizeAsync(MutationTool(), AccountableRun(), CancellationToken.None));

        // Once the account's context is older than the maximum age, the account lends nothing: the tool is
        // not offered and a call is refused with today's code, until the application has a fresh context.
        clock.Advance(MaxAge + TimeSpan.FromSeconds(1));
        using (NhAiAgentInvocationScopes.Begin(AccountableRun()))
        {
            Assert.Equal(NhAiAgentFailureCodes.NotGranted, FailureCode(await gate.AuthorizeAsync(MutationTool())));
        }
        Assert.Equal(NhAiGrantSource.None, await authorizer.AuthorizeAsync(MutationTool(), AccountableRun(), CancellationToken.None));

        contexts.Remember(Account, clock.GetUtcNow(), Permission(ManagePermission));
        using (NhAiAgentInvocationScopes.Begin(AccountableRun()))
        {
            Assert.Equal(NhAiGrantSource.Accountable, (await gate.AuthorizeAsync(MutationTool())).Data.GrantSource);
        }
    }

    [Fact]
    public async Task Without_a_context_there_is_no_principal_and_the_run_is_refused_with_todays_code()
    {
        await using var provider = CreateProvider(new ApplicationContexts(), new TestClock(SignedIn));
        await using var scope = provider.CreateAsyncScope();
        var gate = scope.ServiceProvider.GetRequiredService<INhAiToolInvocationGate>();
        var authorizer = scope.ServiceProvider.GetRequiredService<INhAiAgentToolAuthorizer>();

        Assert.Null(await scope.ServiceProvider.GetRequiredService<INhAiPrincipalFactory>()
            .CreateAsync(Initiator, Project(), CancellationToken.None));
        foreach (var run in new[] { DelegatingRun(), AccountableRun() })
        {
            using (NhAiAgentInvocationScopes.Begin(run))
            {
                Assert.Equal(NhAiAgentFailureCodes.NotGranted, FailureCode(await gate.AuthorizeAsync(MutationTool())));
            }
            Assert.Equal(NhAiGrantSource.None, await authorizer.AuthorizeAsync(MutationTool(), run, CancellationToken.None));
        }
    }

    [Fact]
    public async Task A_context_is_fresh_up_to_its_maximum_age_and_never_by_a_date_beyond_it()
    {
        var clock = new TestClock(SignedIn);
        var contexts = new ApplicationContexts();
        await using var provider = CreateProvider(contexts, clock);
        await using var scope = provider.CreateAsyncScope();
        var principals = scope.ServiceProvider.GetRequiredService<INhAiPrincipalFactory>();

        contexts.Remember(Initiator, SignedIn, Permission(ManagePermission));
        clock.Advance(MaxAge);
        Assert.NotNull(await principals.CreateAsync(Initiator, Project(), CancellationToken.None));
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.Null(await principals.CreateAsync(Initiator, Project(), CancellationToken.None));

        // A date a little ahead of the application's clock, as another server may write it, is fresh; a
        // date further ahead than the maximum age never keeps a context fresh.
        contexts.Remember(Initiator, clock.GetUtcNow().AddMinutes(1), Permission(ManagePermission));
        Assert.NotNull(await principals.CreateAsync(Initiator, Project(), CancellationToken.None));
        contexts.Remember(Initiator, DateTimeOffset.MaxValue, Permission(ManagePermission));
        Assert.Null(await principals.CreateAsync(Initiator, Project(), CancellationToken.None));
    }

    [Fact]
    public async Task A_context_whose_claims_lack_the_tools_policy_or_capability_lends_nothing()
    {
        var contexts = new ApplicationContexts();
        contexts.Remember(Initiator, SignedIn, Permission(ReadPermission));
        contexts.Remember(Account, SignedIn, Permission(ManagePermission));
        await using var provider = CreateProvider(
            contexts,
            new TestClock(SignedIn),
            ai => ai.UsePrincipalsFromAccessSource<ApplicationContexts>(options => options.MaxAge = MaxAge),
            capabilityPermission: "projects.ai");
        await using var scope = provider.CreateAsyncScope();
        var gate = scope.ServiceProvider.GetRequiredService<INhAiToolInvocationGate>();

        // The initiator's context reads projects only: the tool's policy fails.
        using (NhAiAgentInvocationScopes.Begin(DelegatingRun()))
        {
            Assert.Equal(NhAiAgentFailureCodes.NotGranted, FailureCode(await gate.AuthorizeAsync(MutationTool())));
        }

        // The account passes the tool's policy, but its claims do not hold the capability's permission.
        using (NhAiAgentInvocationScopes.Begin(AccountableRun()))
        {
            Assert.Equal(NhAiToolFailureCodes.CapabilityDenied, FailureCode(await gate.AuthorizeAsync(MutationTool())));
        }
    }

    [Fact]
    public async Task Where_tenants_come_from_a_claim_the_context_must_name_exactly_the_subjects_tenant()
    {
        var contexts = new ApplicationContexts();
        await using var provider = CreateProvider(contexts, new TestClock(SignedIn), ai => ai
            .UseAuthenticatedClaims(Issuer)
            .UsePrincipalsFromAccessSource<ApplicationContexts>(options => options.MaxAge = MaxAge));
        await using var scope = provider.CreateAsyncScope();
        var principals = scope.ServiceProvider.GetRequiredService<INhAiPrincipalFactory>();
        var ofTenantA = Project() with { TenantId = "tenant-a" };

        contexts.Remember(Initiator, SignedIn, Permission(ManagePermission), new Claim("tenant_id", "tenant-a"));
        Assert.NotNull(await principals.CreateAsync(Initiator, ofTenantA, CancellationToken.None));

        // Another tenant's rights never act on the subject, nor a context without a tenant or with two.
        contexts.Remember(Initiator, SignedIn, Permission(ManagePermission), new Claim("tenant_id", "tenant-b"));
        Assert.Null(await principals.CreateAsync(Initiator, ofTenantA, CancellationToken.None));
        contexts.Remember(Initiator, SignedIn, Permission(ManagePermission));
        Assert.Null(await principals.CreateAsync(Initiator, ofTenantA, CancellationToken.None));
        contexts.Remember(Initiator, SignedIn, Permission(ManagePermission), new Claim("tenant_id", "tenant-a"), new Claim("tenant_id", "tenant-b"));
        Assert.Null(await principals.CreateAsync(Initiator, ofTenantA, CancellationToken.None));

        // A subject without a tenant, such as the whole application, compares nothing.
        Assert.NotNull(await principals.CreateAsync(Initiator, Project(), CancellationToken.None));
    }

    [Fact]
    public async Task A_host_of_one_fixed_tenant_reads_no_tenant_claim()
    {
        var contexts = new ApplicationContexts();
        contexts.Remember(Initiator, SignedIn, Permission(ManagePermission));
        await using var provider = CreateProvider(contexts, new TestClock(SignedIn), ai => ai
            .UseAuthenticatedClaimsForSingleTenant(Issuer, "customer-1")
            .UsePrincipalsFromAccessSource<ApplicationContexts>(options => options.MaxAge = MaxAge));
        await using var scope = provider.CreateAsyncScope();

        var principal = await scope.ServiceProvider.GetRequiredService<INhAiPrincipalFactory>()
            .CreateAsync(Initiator, Project() with { TenantId = "customer-1" }, CancellationToken.None);

        Assert.NotNull(principal);
        Assert.DoesNotContain(principal.Claims, claim => claim.Type == "tenant_id");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Registration_refuses_a_missing_or_negative_maximum_age(int minutes)
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(() => services.AddNewHeapPlatformAIAspNet(ai =>
            ai.UsePrincipalsFromAccessSource<ApplicationContexts>(options => options.MaxAge = TimeSpan.FromMinutes(minutes))));

        Assert.Contains("needs a positive MaxAge", exception.Message, StringComparison.Ordinal);
        Assert.Contains("It has no default", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Registration_refuses_a_blank_claim_type_and_other_options_but_repeats_the_same_ones()
    {
        var services = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() => services.AddNewHeapPlatformAIAspNet(ai =>
            ai.UsePrincipalsFromAccessSource<ApplicationContexts>(options =>
            {
                options.MaxAge = MaxAge;
                options.RoleClaimType = " ";
            })));

        services.AddNewHeapPlatformAIAspNet(ai =>
            ai.UsePrincipalsFromAccessSource<ApplicationContexts>(options => options.MaxAge = MaxAge));
        services.AddNewHeapPlatformAIAspNet(ai =>
            ai.UsePrincipalsFromAccessSource<ApplicationContexts>(options => options.MaxAge = MaxAge));
        Assert.Throws<InvalidOperationException>(() => services.AddNewHeapPlatformAIAspNet(ai =>
            ai.UsePrincipalsFromAccessSource<ApplicationContexts>(options => options.MaxAge = MaxAge * 2)));

        // The access source replaced the built-in factory; one factory builds the principals.
        var factory = Assert.Single(services, descriptor => descriptor.ServiceType == typeof(INhAiPrincipalFactory));
        Assert.Equal("NhAiAccessSourcePrincipalFactory", factory.ImplementationType?.Name);
    }

    [Fact]
    public void A_principal_factory_of_the_hosts_own_registered_before_is_refused()
    {
        var services = new ServiceCollection();
        services.AddSingleton<INhAiPrincipalFactory, OwnPrincipals>();

        var exception = Assert.Throws<InvalidOperationException>(() => services.AddNewHeapPlatformAIAspNet(ai =>
            ai.UsePrincipalsFromAccessSource<ApplicationContexts>(options => options.MaxAge = MaxAge)));

        Assert.Contains("also registers INhAiPrincipalFactory 'OwnPrincipals'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_principal_factory_of_the_hosts_own_registered_after_fails_the_start()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.Services.AddAuthorizationCore();
        builder.Services.AddNewHeapPlatformAIAspNet(ai =>
            ai.UsePrincipalsFromAccessSource<ApplicationContexts>(options => options.MaxAge = MaxAge));
        builder.Services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, OwnPrincipals>());
        using var host = builder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());

        Assert.Contains("also registers INhAiPrincipalFactory 'OwnPrincipals'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_host_with_only_the_access_source_starts()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.Services.AddAuthorizationCore();
        builder.Services.AddNewHeapPlatformAIAspNet(ai =>
            ai.UsePrincipalsFromAccessSource<ApplicationContexts>(options => options.MaxAge = MaxAge));
        using var host = builder.Build();

        await host.StartAsync();
        await host.StopAsync();
    }

    private static ServiceProvider CreateProvider(
        ApplicationContexts contexts,
        TimeProvider clock,
        Action<NhAiAspNetBuilder>? configure = null,
        string capabilityPermission = ManagePermission)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor());
        services.AddSingleton(clock);
        services.AddAuthorization(options =>
        {
            options.AddPolicy(ToolPolicy, policy => policy.RequireAuthenticatedUser().RequireClaim(PermissionClaim, ManagePermission));
            options.AddPolicy(CapabilityPolicy, policy => policy.RequireAuthenticatedUser().RequireClaim(PermissionClaim, capabilityPermission));
        });
        // The application keeps its contexts in memory, so it registers the source as a singleton.
        services.AddSingleton(contexts);
        services.AddNewHeapPlatformAIAspNet(ai =>
        {
            ai.AddCapabilityGrant("projects-manage", CapabilityPolicy);
            if (configure is null)
            {
                ai.UsePrincipalsFromAccessSource<ApplicationContexts>(options => options.MaxAge = MaxAge);
            }
            else
            {
                configure(ai);
            }
        });
        return services.BuildServiceProvider();
    }

    private static Claim Permission(string permission)
    {
        return new Claim(PermissionClaim, permission);
    }

    private static NhAiSubjectInfo Project()
    {
        return new NhAiSubjectInfo(new NhAiSubjectRef("project", "alpha"), NhAiSubjectInfo.CreateScopeKey(null, DivisionId))
        {
            DivisionId = DivisionId,
            DisplayName = "Alpha"
        };
    }

    /// <summary>A headless run on the project, without grants, that delegates from its initiator.</summary>
    private static NhAiAgentInvocationScope DelegatingRun()
    {
        return Run() with
        {
            InitiatorActorId = Initiator,
            DelegateFromInitiator = true
        };
    }

    /// <summary>A headless run on the project, without grants, with the rights of the account it answers to.</summary>
    private static NhAiAgentInvocationScope AccountableRun()
    {
        return Run() with
        {
            AccountableOwnerId = Account,
            Accountability = NhAiAgentAccountability.ServiceAccount,
            ActWithAccountableRights = true
        };
    }

    private static NhAiAgentInvocationScope Run()
    {
        return new NhAiAgentInvocationScope(
            "project-planner",
            "assistant-agent:project-planner",
            1,
            Project(),
            "assistant-agent:project-planner",
            "run-1",
            "agent-run");
    }

    private static NhAiToolDescriptor MutationTool()
    {
        return new NhAiToolDescriptor(
            "projects.change-status",
            1,
            "Change a project status.",
            typeof(string),
            typeof(string),
            NhAiToolEffect.Mutation,
            NhAiToolExposure.Agent,
            true,
            [ToolPolicy])
        {
            AllowAgentGrant = true,
            RequiredCapabilities = ["projects-manage"]
        };
    }

    private static string FailureCode(TaskResult result)
    {
        Assert.False(result.Success);
        var message = Assert.Single(result.AllErrorMessages).ToString();
        return message[1..message.IndexOf('\'', 1)];
    }

    /// <summary>
    /// The application's contexts as its claims transformation records them; NewHeap keeps none.
    /// </summary>
    private sealed class ApplicationContexts : INhAiAccessSource
    {
        private readonly ConcurrentDictionary<string, NhAiAccessContext> _contexts = new(StringComparer.Ordinal);

        public ConcurrentQueue<(string ActorId, NhAiSubjectInfo Subject)> Asked { get; } = new();

        public void Remember(string actorId, DateTimeOffset validAt, params Claim[] claims)
        {
            _contexts[actorId] = new NhAiAccessContext(claims, validAt);
        }

        public ValueTask<NhAiAccessContext?> GetAsync(
            string actorId,
            NhAiSubjectInfo subject,
            CancellationToken cancellationToken = default)
        {
            Asked.Enqueue((actorId, subject));
            return ValueTask.FromResult(_contexts.GetValueOrDefault(actorId));
        }
    }

    private sealed class OwnPrincipals : INhAiPrincipalFactory
    {
        public ValueTask<ClaimsPrincipal?> CreateAsync(
            string actorId,
            NhAiSubjectInfo subject,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<ClaimsPrincipal?>(null);
        }
    }

    /// <summary>The application's clock, which the test moves instead of waiting.</summary>
    private sealed class TestClock(DateTimeOffset start) : TimeProvider
    {
        private long _ticks = start.UtcTicks;

        public override DateTimeOffset GetUtcNow()
        {
            return new DateTimeOffset(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        }

        public void Advance(TimeSpan duration)
        {
            Interlocked.Add(ref _ticks, duration.Ticks);
        }
    }
}
