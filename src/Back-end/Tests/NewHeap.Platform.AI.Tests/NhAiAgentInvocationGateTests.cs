using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.AI.Tests;

public sealed class NhAiAgentInvocationGateTests
{
    private const string InitiatorId = "7a0f6d4c-2d0e-4f53-9a43-5f1d2c9e8b11";
    private static readonly Guid DivisionId = Guid.NewGuid();

    [Fact]
    public async Task An_agent_grant_authorizes_an_opted_in_tool_without_a_request()
    {
        await using var provider = CreateProvider(httpContext: null, new PolicyAuthorizationService());
        await using var scope = provider.CreateAsyncScope();
        var gate = scope.ServiceProvider.GetRequiredService<INhAiToolInvocationGate>();

        using var _ = NhAiAgentInvocationScopes.Begin(CreateScope(grants: [new NhAiAgentGrant("projects.*")]));
        var result = await gate.AuthorizeAsync(MutationTool(allowAgentGrant: true));

        Assert.True(result.Success);
        var context = result.Data;
        Assert.Equal(NhAiGrantSource.Agent, context.GrantSource);
        Assert.Equal(NhAiActorKind.Agent, context.ActorKind);
        Assert.Equal("assistant-agent:project-intake", context.ActorId);
        Assert.Equal("assistant-agent:project-intake", context.AccountableOwnerId);
        Assert.Equal("project-intake", context.AgentId);
        Assert.Equal("run-1", context.RunId);
        Assert.Equal(DivisionId.ToString(), context.Scope["division-id"]);
        Assert.Contains("projects-manage", context.CapabilityGrants);
        Assert.Contains(context.ExecutionScopes, entry => entry.Type == "project" && entry.Id == "alpha");
    }

    [Fact]
    public async Task A_run_for_the_whole_application_concerns_no_object_and_works_in_its_division()
    {
        await using var provider = CreateProvider(httpContext: null, new PolicyAuthorizationService());
        await using var scope = provider.CreateAsyncScope();
        var gate = scope.ServiceProvider.GetRequiredService<INhAiToolInvocationGate>();
        var contexts = scope.ServiceProvider.GetRequiredService<INhAiAgentInvocationContextFactory>();
        var appWide = CreateScope(grants: [new NhAiAgentGrant("projects.*")]) with
        {
            Subject = new NhAiSubjectInfo(NhAiSystemSubjects.Global, NhAiSubjectInfo.CreateScopeKey(null, null))
        };
        var inDivision = appWide with
        {
            Subject = new NhAiSubjectInfo(NhAiSystemSubjects.ForDivision(DivisionId), NhAiSubjectInfo.CreateScopeKey(null, DivisionId))
            {
                DivisionId = DivisionId
            }
        };

        TaskResult<NhAiInvocationContext> global;
        using (NhAiAgentInvocationScopes.Begin(appWide))
        {
            global = await gate.AuthorizeAsync(MutationTool(allowAgentGrant: true));
        }
        TaskResult<NhAiInvocationContext> division;
        using (NhAiAgentInvocationScopes.Begin(inDivision))
        {
            division = await gate.AuthorizeAsync(MutationTool(allowAgentGrant: true));
        }

        // App-wide there is no object and no division; in a division only the division, as for a
        // person with that active division.
        Assert.Empty(global.Data.ExecutionScopes);
        Assert.False(global.Data.Scope.ContainsKey("division-id"));
        var scopeEntry = Assert.Single(division.Data.ExecutionScopes);
        Assert.Equal(("division", DivisionId.ToString()), (scopeEntry.Type, scopeEntry.Id));
        Assert.Equal(DivisionId.ToString(), division.Data.Scope["division-id"]);
        Assert.Empty(contexts.Create(appWide).ExecutionScopes);
        Assert.Equal(["division"], contexts.Create(inDivision).ExecutionScopes.Select(entry => entry.Type));
    }

    [Fact]
    public async Task A_grant_needs_the_tool_to_opt_in_and_agent_exposure()
    {
        await using var provider = CreateProvider(httpContext: null, new PolicyAuthorizationService());
        await using var scope = provider.CreateAsyncScope();
        var gate = scope.ServiceProvider.GetRequiredService<INhAiToolInvocationGate>();
        using var _ = NhAiAgentInvocationScopes.Begin(CreateScope(grants: [new NhAiAgentGrant("projects.*")]));

        var notOptedIn = await gate.AuthorizeAsync(MutationTool(allowAgentGrant: false));
        var notExposed = await gate.AuthorizeAsync(MutationTool(allowAgentGrant: true) with
        {
            Exposure = NhAiToolExposure.Local
        });

        Assert.Equal(NhAiAgentFailureCodes.NotGranted, FailureCode(notOptedIn));
        Assert.Equal(NhAiAgentFailureCodes.NotAgentExposed, FailureCode(notExposed));
    }

    [Fact]
    public async Task A_trigger_only_grant_does_not_apply_to_a_request_turn()
    {
        await using var provider = CreateProvider(httpContext: null, new PolicyAuthorizationService());
        await using var scope = provider.CreateAsyncScope();
        var gate = scope.ServiceProvider.GetRequiredService<INhAiToolInvocationGate>();
        var grants = new[] { new NhAiAgentGrant("projects.change-status", triggerOnly: true) };

        TaskResult<NhAiInvocationContext> inRequest;
        using (NhAiAgentInvocationScopes.Begin(CreateScope(grants: grants) with { IsRequestTurn = true }))
        {
            inRequest = await gate.AuthorizeAsync(MutationTool(allowAgentGrant: true));
        }

        TaskResult<NhAiInvocationContext> inTrigger;
        using (NhAiAgentInvocationScopes.Begin(CreateScope(grants: grants)))
        {
            inTrigger = await gate.AuthorizeAsync(MutationTool(allowAgentGrant: true));
        }

        Assert.False(inRequest.Success);
        Assert.True(inTrigger.Success);
    }

    [Fact]
    public async Task Delegation_uses_the_initiator_principal_against_the_subject_resource()
    {
        var authorization = new PolicyAuthorizationService();
        authorization.Allow("project-manage", InitiatorId);
        authorization.Allow("project-read", InitiatorId);
        await using var provider = CreateProvider(httpContext: null, authorization);
        await using var scope = provider.CreateAsyncScope();
        var gate = scope.ServiceProvider.GetRequiredService<INhAiToolInvocationGate>();

        using var _ = NhAiAgentInvocationScopes.Begin(CreateScope(grants: []) with
        {
            InitiatorActorId = InitiatorId,
            DelegateFromInitiator = true
        });
        var result = await gate.AuthorizeAsync(MutationTool(allowAgentGrant: false));

        Assert.True(result.Success);
        Assert.Equal(NhAiGrantSource.Delegated, result.Data.GrantSource);
        Assert.Contains("projects-read", result.Data.CapabilityGrants);
        var resource = Assert.IsType<NhAiSubjectAuthorizationResource>(authorization.LastResource);
        Assert.Equal(DivisionId, resource.DivisionId);
        Assert.Equal(new NhAiSubjectRef("project", "alpha"), resource.Subject);
    }

    [Fact]
    public async Task An_agent_that_acts_with_accountable_rights_uses_what_its_technical_account_may_use()
    {
        var authorization = new PolicyAuthorizationService();
        authorization.Allow("project-manage", "service-1");
        authorization.Allow("project-read", "service-1");
        await using var provider = CreateProvider(httpContext: null, authorization);
        await using var scope = provider.CreateAsyncScope();
        var gate = scope.ServiceProvider.GetRequiredService<INhAiToolInvocationGate>();

        // No grant and no opt-in of the tool: the account's own rights decide.
        using var _ = NhAiAgentInvocationScopes.Begin(AccountableScope("service-1"));
        var result = await gate.AuthorizeAsync(MutationTool(allowAgentGrant: false));

        Assert.True(result.Success);
        Assert.Equal(NhAiGrantSource.Accountable, result.Data.GrantSource);
        Assert.Equal("service-1", result.Data.AccountableOwnerId);
        Assert.Contains("projects-read", result.Data.CapabilityGrants);
        var resource = Assert.IsType<NhAiSubjectAuthorizationResource>(authorization.LastResource);
        Assert.Equal(new NhAiSubjectRef("project", "alpha"), resource.Subject);
    }

    [Fact]
    public async Task Accountable_rights_lend_nothing_beyond_the_account_its_policies_or_an_agent_that_answers_to_itself()
    {
        var authorization = new PolicyAuthorizationService();
        authorization.Allow("project-manage", "service-1");
        await using var provider = CreateProvider(httpContext: null, authorization);
        await using var scope = provider.CreateAsyncScope();
        var gate = scope.ServiceProvider.GetRequiredService<INhAiToolInvocationGate>();
        var readTool = MutationTool(allowAgentGrant: false) with
        {
            Id = "projects.search",
            Effect = NhAiToolEffect.ReadOnly
        };

        using (NhAiAgentInvocationScopes.Begin(AccountableScope("service-2")))
        {
            Assert.Equal(NhAiAgentFailureCodes.NotGranted, FailureCode(await gate.AuthorizeAsync(MutationTool(allowAgentGrant: false))));
        }
        using (NhAiAgentInvocationScopes.Begin(AccountableScope("service-1") with { Accountability = NhAiAgentAccountability.Agent }))
        {
            Assert.Equal(NhAiAgentFailureCodes.NotGranted, FailureCode(await gate.AuthorizeAsync(MutationTool(allowAgentGrant: false))));
        }
        using (NhAiAgentInvocationScopes.Begin(AccountableScope("service-1") with { ActWithAccountableRights = false }))
        {
            Assert.Equal(NhAiAgentFailureCodes.NotGranted, FailureCode(await gate.AuthorizeAsync(MutationTool(allowAgentGrant: false))));
        }
        using (NhAiAgentInvocationScopes.Begin(AccountableScope("service-1") with { ElevatedReads = NhAiElevatedReads.Deny }))
        {
            Assert.Equal(NhAiAgentFailureCodes.NotGranted, FailureCode(await gate.AuthorizeAsync(readTool)));
        }
    }

    [Fact]
    public async Task Elevated_reads_can_be_denied()
    {
        await using var provider = CreateProvider(httpContext: null, new PolicyAuthorizationService());
        await using var scope = provider.CreateAsyncScope();
        var gate = scope.ServiceProvider.GetRequiredService<INhAiToolInvocationGate>();
        var readTool = MutationTool(allowAgentGrant: true) with
        {
            Id = "projects.search",
            Effect = NhAiToolEffect.ReadOnly
        };

        using var _ = NhAiAgentInvocationScopes.Begin(CreateScope(grants: [new NhAiAgentGrant("projects.*")]) with
        {
            ElevatedReads = NhAiElevatedReads.Deny
        });
        var result = await gate.AuthorizeAsync(readTool);

        Assert.Equal(NhAiAgentFailureCodes.ElevatedReadDenied, FailureCode(result));
    }

    [Fact]
    public async Task Outside_an_agent_turn_the_request_gate_still_applies()
    {
        await using var provider = CreateProvider(httpContext: null, new PolicyAuthorizationService());
        await using var scope = provider.CreateAsyncScope();
        var gate = scope.ServiceProvider.GetRequiredService<INhAiToolInvocationGate>();

        var result = await gate.AuthorizeAsync(MutationTool(allowAgentGrant: true));

        Assert.Equal("ai-tool-authentication-required", FailureCode(result));
    }

    [Fact]
    public void Proposal_execution_keeps_the_origin_run_and_fences_only_when_asked()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAuthorizationService>(new PolicyAuthorizationService());
        services.AddNewHeapPlatformAIAspNet(_ => { });
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var adapter = scope.ServiceProvider.GetRequiredService<INhAiBackgroundOperationRunAdapter>();
        var operation = new ExecutionOperationContext();
        var context = new NhAiInvocationContext("assistant-agent:project-intake", "agent-run", new Dictionary<string, string>())
        {
            ActorKind = NhAiActorKind.Agent,
            AccountableOwnerId = "assistant-agent:project-intake"
        };

        var fenced = adapter.BindProposalExecution(context, operation, "origin-run", fenced: true);
        var replayable = adapter.BindProposalExecution(context, operation, "origin-run", fenced: false);

        Assert.Equal("origin-run", fenced.RunId);
        Assert.Equal(operation.OperationId.ToString("N"), fenced.CorrelationId);
        Assert.Equal("42", fenced.FencingToken);
        Assert.Null(replayable.FencingToken);
        Assert.Equal(operation.IdempotencyKey, replayable.IdempotencyKey);
    }

    private static string FailureCode(TaskResult result)
    {
        Assert.False(result.Success);
        var message = Assert.Single(result.AllErrorMessages).ToString();
        return message[1..message.IndexOf('\'', 1)];
    }

    private static ServiceProvider CreateProvider(HttpContext? httpContext, PolicyAuthorizationService authorization)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = httpContext });
        services.AddSingleton<IAuthorizationService>(authorization);
        services.AddNewHeapPlatformAIAspNet(ai => ai
            .AddActiveDivisionScope("division-access")
            .AddCapabilityGrant("projects-read", "project-read"));
        services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, TestPrincipalFactory>());
        return services.BuildServiceProvider();
    }

    private static NhAiAgentInvocationScope CreateScope(IReadOnlyList<NhAiAgentGrant> grants)
    {
        var subject = new NhAiSubjectInfo(
            new NhAiSubjectRef("project", "alpha"),
            NhAiSubjectInfo.CreateScopeKey(null, DivisionId))
        {
            DivisionId = DivisionId,
            DisplayName = "Alpha"
        };
        return new NhAiAgentInvocationScope(
            "project-intake",
            "assistant-agent:project-intake",
            1,
            subject,
            "assistant-agent:project-intake",
            "run-1",
            "agent-run")
        {
            Grants = grants
        };
    }

    private static NhAiAgentInvocationScope AccountableScope(string accountActorId)
    {
        return CreateScope(grants: []) with
        {
            AccountableOwnerId = accountActorId,
            Accountability = NhAiAgentAccountability.ServiceAccount,
            ActWithAccountableRights = true
        };
    }

    private static NhAiToolDescriptor MutationTool(bool allowAgentGrant)
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
            ["project-manage"])
        {
            AllowAgentGrant = allowAgentGrant,
            RequiredCapabilities = ["projects-manage"]
        };
    }

    private sealed class TestPrincipalFactory : INhAiPrincipalFactory
    {
        public ValueTask<ClaimsPrincipal?> CreateAsync(
            string actorId,
            NhAiSubjectInfo subject,
            CancellationToken cancellationToken = default)
        {
            var principal = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, actorId)],
                "test"));
            return ValueTask.FromResult<ClaimsPrincipal?>(principal);
        }
    }

    /// <summary>
    /// Allows a policy only for the actors registered with <see cref="Allow"/>.
    /// </summary>
    private sealed class PolicyAuthorizationService : IAuthorizationService
    {
        private readonly Dictionary<string, HashSet<string>> _allowed = new(StringComparer.Ordinal);

        public object? LastResource { get; private set; }

        public void Allow(string policy, string actorId)
        {
            if (!_allowed.TryGetValue(policy, out var actors))
            {
                actors = new HashSet<string>(StringComparer.Ordinal);
                _allowed[policy] = actors;
            }

            actors.Add(actorId);
        }

        public Task<AuthorizationResult> AuthorizeAsync(
            ClaimsPrincipal user,
            object? resource,
            IEnumerable<IAuthorizationRequirement> requirements)
        {
            return Task.FromResult(AuthorizationResult.Failed());
        }

        public Task<AuthorizationResult> AuthorizeAsync(
            ClaimsPrincipal user,
            object? resource,
            string policyName)
        {
            LastResource = resource;
            var actorId = user.FindFirstValue(ClaimTypes.NameIdentifier);
            var allowed = actorId is not null
                          && _allowed.TryGetValue(policyName, out var actors)
                          && actors.Contains(actorId);
            return Task.FromResult(allowed ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        }
    }

    private sealed class ExecutionOperationContext : INhBackgroundOperationContext
    {
        public Guid OperationId { get; } = Guid.NewGuid();
        public Guid AttemptId { get; } = Guid.NewGuid();
        public int AttemptNumber => 2;
        public long FencingToken => 42;
        public string IdempotencyKey => "proposal:execute";
        public INhBackgroundOperationProgressContext Progress => throw new NotSupportedException();
        public INhBackgroundOperationMessageSink Messages => throw new NotSupportedException();
        public INhBackgroundOperationCheckpointStore Checkpoints => throw new NotSupportedException();
        public INhBackgroundOperationLeaseManager Leases => throw new NotSupportedException();
        public INhBackgroundOperationIdempotencyManager Idempotency => throw new NotSupportedException();
        public INhBackgroundOperationFanOutContext FanOut => throw new NotSupportedException();
        public INhBackgroundOperationSuspensionContext Suspension => throw new NotSupportedException();

        public Task ThrowIfCancellationRequestedAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SetResultAsync(
            NhBackgroundOperationResultReference result,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}
