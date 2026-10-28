using System.Security.Claims;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// The <c>chain</c> of the assistant context without a host (FB28): how an application writes the chain
/// of a turn, reads the chain of a reviewed caller, recognizes that caller and refuses an incomplete
/// registration. A malformed chain is read as no chain, never as a refusal.
/// </summary>
public sealed class AssistantMcpChainPayloadTests
{
    private const string Planning = "https://planning.test";
    private const string Projects = "https://projects.test";
    private const string Orders = "https://orders.test";

    private static readonly NhAssistantMcpChainCaller OrdersCaller = new(Orders, Orders);

    [Fact]
    public void A_chain_names_this_applications_agents_by_their_id_and_qualifies_the_others()
    {
        var context = Context(Chain(2, (Planning, "intake"), (Projects, "reviewer"), (Orders, "planner")));

        var inherited = NhAssistantMcpChains.Parse(context, OrdersCaller, Projects, out var problem);

        Assert.Null(problem);
        Assert.NotNull(inherited);
        Assert.Equal(["intake@" + Planning, "reviewer", "planner@" + Orders], inherited.Agents);
        Assert.Equal(2, inherited.ConsultationDepth);
    }

    [Fact]
    public void Members_a_receiver_does_not_know_are_left_alone()
    {
        var chain = Chain(0, (Orders, "planner"));
        chain["handOffDepth"] = 1;
        chain["future"] = new JsonObject { ["anything"] = true };
        var context = Context(chain);
        context["somethingNew"] = "value";

        var inherited = NhAssistantMcpChains.Parse(context, OrdersCaller, Projects, out _);

        Assert.Equal(["planner@" + Orders], inherited!.Agents);
    }

    [Theory]
    [MemberData(nameof(MalformedChains))]
    public void A_malformed_chain_is_read_as_no_chain(string json, string expectedProblem)
    {
        var context = (JsonObject)JsonNode.Parse(json)!;

        var inherited = NhAssistantMcpChains.Parse(context, OrdersCaller, Projects, out var problem);

        Assert.Null(inherited);
        Assert.Equal(expectedProblem, problem);
    }

    public static TheoryData<string, string> MalformedChains()
    {
        static string Agent(string application, string agentId)
        {
            return "{\"application\":\"" + application + "\",\"agentId\":\"" + agentId + "\"}";
        }

        static string Chain(string agents, string depth = "0")
        {
            return "{\"agents\":" + agents + ",\"consultationDepth\":" + depth + "}";
        }

        static string Context(string chain, string version = "1")
        {
            return "{\"version\":" + version + ",\"chain\":" + chain + "}";
        }

        var planner = Agent(Orders, "planner");
        var many = "[" + string.Join(",", Enumerable.Repeat(planner, NhAssistantMcpContext.MaxChainAgents + 1)) + "]";
        return new TheoryData<string, string>
        {
            { "{\"chain\":" + Chain("[" + planner + "]") + "}", "version-unsupported" },
            { Context(Chain("[" + planner + "]"), "2"), "version-unsupported" },
            { Context(Chain("[" + planner + "]"), "\"1\""), "version-unsupported" },
            { Context("[" + planner + "]"), "chain-invalid" },
            { Context("null"), "chain-invalid" },
            { Context("{\"agents\":[" + planner + "]}"), "depth-invalid" },
            { Context(Chain("[" + planner + "]", "-1")), "depth-invalid" },
            { Context(Chain("[" + planner + "]", "\"1\"")), "depth-invalid" },
            { Context(Chain("[" + planner + "]", "1.5")), "depth-invalid" },
            { Context(Chain("[" + planner + "]", "99999999999")), "depth-invalid" },
            { Context("{\"consultationDepth\":0}"), "agents-invalid" },
            { Context(Chain("[]")), "agents-invalid" },
            { Context(Chain(planner)), "agents-invalid" },
            { Context(Chain(many)), "agents-invalid" },
            { Context(Chain("[\"planner\"]")), "agent-invalid" },
            { Context(Chain("[{\"agentId\":\"planner\"}]")), "agent-invalid" },
            { Context(Chain("[" + Agent("orders app", "planner") + "]")), "agent-invalid" },
            { Context(Chain("[" + Agent(new string('o', NhAssistantMcpContext.MaxApplicationIdLength + 1), "planner") + "]")), "agent-invalid" },
            { Context(Chain("[" + Agent(Orders, "Planner") + "]")), "agent-invalid" },
            { Context(Chain("[" + Agent(Orders, "planner@" + Projects) + "]")), "agent-invalid" },
            { Context(Chain("[{\"application\":\"" + Orders + "\",\"agentId\":7}]")), "agent-invalid" },
            { Context(Chain("[" + Agent(Orders, new string('a', 129)) + "]")), "agent-invalid" },
            // The calling agent is the caller's own: a chain that ends in another application was not sent
            // by the application the caller was reviewed as.
            { Context(Chain("[" + planner + "," + Agent(Projects, "reviewer") + "]", "1")), "caller-mismatch" }
        };
    }

    [Fact]
    public void A_caller_is_recognized_by_one_issuer_claim_and_its_client_before_the_issuers_other_callers()
    {
        var chains = new NhAssistantMcpChains(
            [new NhAssistantMcpChainApplicationRegistration(Projects)],
            [
                new NhAssistantMcpChainCallerRegistration(new NhAssistantMcpChainCaller(Orders, "https://login.test")),
                new NhAssistantMcpChainCallerRegistration(new NhAssistantMcpChainCaller(Planning, "https://login.test")
                {
                    ClientClaimType = "azp",
                    ClientId = "planning-app"
                })
            ]);

        Assert.True(chains.ContinuesChains);
        Assert.Equal(Planning, chains.FindCaller(Principal(("iss", "https://login.test"), ("azp", "planning-app")))?.ApplicationId);
        Assert.Equal(Orders, chains.FindCaller(Principal(("iss", "https://login.test"), ("azp", "orders-app")))?.ApplicationId);
        Assert.Equal(Orders, chains.FindCaller(Principal(("iss", "https://login.test")))?.ApplicationId);
        // Another issuer, two issuer claims and a caller who did not sign in are no reviewed caller.
        Assert.Null(chains.FindCaller(Principal(("iss", "https://elsewhere.test"))));
        Assert.Null(chains.FindCaller(Principal(("iss", "https://login.test"), ("iss", "https://elsewhere.test"))));
        Assert.Null(chains.FindCaller(new ClaimsPrincipal(new ClaimsIdentity([new Claim("iss", "https://login.test")]))));
        Assert.Null(chains.FindCaller(null));
    }

    [Fact]
    public void The_chain_of_a_turn_names_every_agent_with_its_application()
    {
        var chains = new NhAssistantMcpChains([new NhAssistantMcpChainApplicationRegistration(Projects)], []);
        var consulted = new NhAssistantConsultationScope(
            Guid.NewGuid(),
            new NhAssistantConsultationOptions("project-assistant", "project"),
            ["intake@" + Planning, "project-assistant"],
            1,
            false,
            null,
            new AllowAllViewer());

        var fromChat = chains.ChainOf(Turn(null));
        var fromConsultation = chains.ChainOf(Turn(consulted));

        Assert.Equal(
            """{"agents":[{"application":"https://projects.test","agentId":"project-assistant"}],"consultationDepth":0}""",
            fromChat.ToJsonString());
        Assert.Equal(
            """{"agents":[{"application":"https://planning.test","agentId":"intake"},{"application":"https://projects.test","agentId":"project-assistant"}],"consultationDepth":1}""",
            fromConsultation.ToJsonString());
    }

    [Fact]
    public void A_chain_needs_the_applications_one_name_and_a_reviewed_caller_another()
    {
        var unnamed = new NhAssistantMcpChains([], []);
        unnamed.Validate(bindingsIncludeChain: false);
        Assert.Throws<InvalidOperationException>(() => unnamed.Validate(bindingsIncludeChain: true));
        Assert.Throws<InvalidOperationException>(() => new NhAssistantMcpChains(
            [],
            [new NhAssistantMcpChainCallerRegistration(OrdersCaller)]).Validate(bindingsIncludeChain: false));
        Assert.Throws<InvalidOperationException>(() => new NhAssistantMcpChains(
            [new NhAssistantMcpChainApplicationRegistration(Orders)],
            [new NhAssistantMcpChainCallerRegistration(OrdersCaller)]).Validate(bindingsIncludeChain: false));
        Assert.Throws<InvalidOperationException>(() => new NhAssistantMcpChains(
            [new NhAssistantMcpChainApplicationRegistration(Orders), new NhAssistantMcpChainApplicationRegistration(Projects)],
            []));
        Assert.Throws<InvalidOperationException>(() => new NhAssistantMcpChains(
            [new NhAssistantMcpChainApplicationRegistration(Projects)],
            [
                new NhAssistantMcpChainCallerRegistration(new NhAssistantMcpChainCaller(Orders, "https://login.test")),
                new NhAssistantMcpChainCallerRegistration(new NhAssistantMcpChainCaller(Planning, "https://login.test"))
            ]));
    }

    [Fact]
    public void The_registration_refuses_names_and_callers_it_could_not_compare()
    {
        var services = new ServiceCollection();
        services.AddNewHeapAssistant(assistant =>
        {
            Assert.Throws<ArgumentException>(() => assistant.UseMcpChainApplication("projects app"));
            Assert.Throws<ArgumentException>(() => assistant.UseMcpChainApplication(string.Empty));
            assistant.UseMcpChainApplication(Projects);
            // The same name again changes nothing; another name is refused.
            assistant.UseMcpChainApplication(Projects);
            Assert.Throws<InvalidOperationException>(() => assistant.UseMcpChainApplication(Orders));
            Assert.Throws<ArgumentException>(() => assistant.ContinueMcpChain(new NhAssistantMcpChainCaller("orders\tapp", Orders)));
            Assert.Throws<ArgumentException>(() => assistant.ContinueMcpChain(new NhAssistantMcpChainCaller(Orders, " ")));
            Assert.Throws<ArgumentException>(() => assistant.ContinueMcpChain(OrdersCaller with { ClientId = "orders-app" }));
            Assert.Throws<ArgumentException>(() => assistant.ContinueMcpChain(OrdersCaller with { ClientClaimType = "azp" }));
            assistant.ContinueMcpChain(OrdersCaller);
        });

        using var provider = services.BuildServiceProvider();
        var chains = provider.GetRequiredService<NhAssistantMcpChains>();
        Assert.Equal(Projects, chains.ApplicationId);
        Assert.Equal([OrdersCaller], chains.Callers);
    }

    private static JsonObject Chain(int depth, params (string Application, string AgentId)[] agents)
    {
        return new JsonObject
        {
            ["agents"] = new JsonArray(agents
                .Select(agent => (JsonNode)new JsonObject { ["application"] = agent.Application, ["agentId"] = agent.AgentId })
                .ToArray()),
            ["consultationDepth"] = depth
        };
    }

    private static JsonObject Context(JsonObject chain)
    {
        return new JsonObject
        {
            ["version"] = NhAssistantMcpContext.Version,
            ["invocation"] = new JsonObject { ["invocationId"] = Guid.NewGuid().ToString() },
            ["chain"] = chain
        };
    }

    private static NhAssistantTurnScope Turn(NhAssistantConsultationScope? consultation)
    {
        return new NhAssistantTurnScope
        {
            ConversationId = Guid.NewGuid(),
            TurnId = Guid.NewGuid(),
            Agent = AssistantTestData.Agent(),
            OwnerActorId = "member-1",
            AccountableOwnerId = "member-1",
            ModelProfileName = "project-chat",
            PromptVersion = "1",
            PromptHash = "hash",
            Deadline = DateTimeOffset.UtcNow.AddMinutes(2),
            Limits = new NhAssistantLimits(),
            Consultation = consultation
        };
    }

    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims)
    {
        return new ClaimsPrincipal(new ClaimsIdentity(
            claims.Select(claim => new Claim(claim.Type, claim.Value)).Append(new Claim(ClaimTypes.NameIdentifier, "member-1")),
            "test"));
    }

    private sealed class AllowAllViewer : INhAssistantConsultationViewer
    {
        public ValueTask<bool> MayViewAsync(NhAiGrantSource grantSource, string toolId, string? readForActorId, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(true);
        }

        public ValueTask<bool> MayDecideAsync(NhAiApprovalPolicy policy, NewHeap.Platform.AI.Chat.Entities.AssistantThread thread, string toolId, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(true);
        }

        public ValueTask<bool> MayUseAgentAsync(NhAssistantAgentDefinition agent, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(true);
        }
    }
}
