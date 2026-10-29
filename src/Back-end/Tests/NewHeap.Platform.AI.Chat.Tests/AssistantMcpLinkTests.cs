using System.Security.Claims;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// FB59: the chain callers and context bindings the application stores (<see cref="INhAssistantMcpLinks"/>)
/// count next to the code registrations without a restart, within the cache time on the application's
/// clock. Every stored entry is checked as the same registration in code is checked at startup, an invalid
/// one is left out with a logged reason, a failing source leaves out every stored entry and keeps the code
/// registrations, and no stored entry grants more than a registration could.
/// </summary>
public sealed class AssistantMcpLinkTests
{
    private const string Projects = "https://projects.test";
    private const string Orders = "https://orders.test";
    private const string Planning = "https://planning.test";
    private const string Login = "https://login.test";
    private const string ResearchUrl = "https://research.test/mcp";

    private static readonly NhAssistantMcpChainCaller OrdersCaller = new(Orders, Orders);
    private static readonly NhAssistantMcpChainCaller PlanningCaller = new(Planning, Planning);
    private static readonly NhAssistantMcpContextBinding ResearchBinding = new("research", ResearchUrl, "research-ask");

    [Fact]
    public async Task A_caller_the_application_stores_counts_after_the_cache_time_and_stops_when_revoked()
    {
        using var links = Links.Create();

        // The code registration counts at once; the planning application is no reviewed caller yet.
        Assert.Equal(Orders, (await links.ViewAsync()).FindCaller(Principal(("iss", Orders)))?.ApplicationId);
        Assert.Null((await links.ViewAsync()).FindCaller(Principal(("iss", Planning))));

        links.Book.Callers.Add(PlanningCaller);
        Assert.Null((await links.ViewAsync()).FindCaller(Principal(("iss", Planning))));
        links.Clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(Planning, (await links.ViewAsync()).FindCaller(Principal(("iss", Planning)))?.ApplicationId);

        links.Book.Callers.Clear();
        Assert.NotNull((await links.ViewAsync()).FindCaller(Principal(("iss", Planning))));
        links.Clock.Advance(TimeSpan.FromSeconds(30));
        var view = await links.ViewAsync();
        Assert.Null(view.FindCaller(Principal(("iss", Planning))));
        Assert.Equal(Orders, view.FindCaller(Principal(("iss", Orders)))?.ApplicationId);
        // One read per cache time: the first, the one after adding and the one after revoking.
        Assert.Equal(3, links.Book.Reads);
    }

    [Fact]
    public async Task A_stored_binding_counts_after_the_cache_time_and_applies_only_to_its_server_id_url_and_tool()
    {
        using var links = Links.Create();
        Assert.Null((await links.ViewAsync()).FindStoredBinding(Server("research", ResearchUrl), "research-ask"));

        links.Book.Bindings.Add(ResearchBinding);
        links.Clock.Advance(TimeSpan.FromSeconds(30));
        var view = await links.ViewAsync();

        Assert.Equal(ResearchBinding, view.FindStoredBinding(Server("research", ResearchUrl), "research-ask"));
        // The URL is compared as a registration compares it: case of scheme and host, and a closing slash.
        Assert.NotNull(view.FindStoredBinding(Server("research", "HTTPS://Research.test/mcp/"), "research-ask"));
        Assert.Null(view.FindStoredBinding(Server("research", "https://elsewhere.test/mcp"), "research-ask"));
        Assert.Null(view.FindStoredBinding(Server("research-copy", ResearchUrl), "research-ask"));
        Assert.Null(view.FindStoredBinding(Server("research", ResearchUrl), "research-publish"));
        Assert.Null(view.FindStoredBinding(Server("research", "not a url"), "research-ask"));

        links.Book.Bindings.Clear();
        links.Clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Null((await links.ViewAsync()).FindStoredBinding(Server("research", ResearchUrl), "research-ask"));
    }

    [Fact]
    public async Task A_stored_entry_is_checked_as_a_registration_and_an_invalid_one_is_left_out_with_its_reason()
    {
        using var links = Links.Create(codeBinding: ResearchBinding with { ServerId = "research-code" });
        links.Book.Callers.AddRange(
        [
            PlanningCaller,
            null!,
            new NhAssistantMcpChainCaller("planning app", Planning),
            new NhAssistantMcpChainCaller("https://billing.test", " "),
            new NhAssistantMcpChainCaller("https://billing.test", Login) { ClientId = "billing-app" },
            // A caller cannot take this application's own name.
            new NhAssistantMcpChainCaller(Projects, Login) { ClientClaimType = "azp", ClientId = "projects-app" },
            // Code reviewed this issuer as the orders application; a stored entry cannot name it another.
            new NhAssistantMcpChainCaller("https://shadow.test", Orders),
            // Two stored entries cannot name one issuer and client as two applications.
            new NhAssistantMcpChainCaller("https://hr.test", Login) { ClientClaimType = "azp", ClientId = "people-app" },
            new NhAssistantMcpChainCaller("https://payroll.test", Login) { ClientClaimType = "azp", ClientId = "people-app" },
            // The code registration again, and a stored entry twice, add nothing and are no conflict.
            OrdersCaller,
            PlanningCaller
        ]);
        links.Book.Bindings.AddRange(
        [
            ResearchBinding,
            null!,
            ResearchBinding with { ServerId = "research-user", ServerUrl = "https://user:secret@research.test/mcp" },
            ResearchBinding with { ServerId = "research-ftp", ServerUrl = "ftp://research.test/mcp" },
            ResearchBinding with { ServerId = "Research Two" },
            ResearchBinding with { ServerId = "research-blank", RemoteName = " " },
            ResearchBinding with { ServerId = "research-big", Snapshot = new NhAssistantConversationSnapshotRequest { MaxBytes = 70_000 } },
            ResearchBinding with { ServerId = "research-none", Snapshot = null! },
            // Code binds this server and tool; its code binding applies.
            ResearchBinding with { ServerId = "research-code", ServerUrl = "https://other.test/mcp" },
            // One server and tool, two different bindings: neither applies.
            ResearchBinding with { ServerId = "research-twice" },
            ResearchBinding with { ServerId = "research-twice", IncludeSnapshot = false },
            // The same binding twice is one binding.
            ResearchBinding
        ]);

        var view = await links.ViewAsync();

        Assert.Equal([OrdersCaller, PlanningCaller], view.Callers);
        Assert.Equal([ResearchBinding], view.StoredBindings);
        Assert.Equal(Orders, view.FindCaller(Principal(("iss", Orders)))?.ApplicationId);
        Assert.Null(view.FindCaller(Principal(("iss", Login), ("azp", "people-app"))));
        Assert.Null(view.FindCaller(Principal(("iss", Login), ("azp", "projects-app"))));
        var warnings = links.Warnings();
        Assert.Equal(
            [
                "chain-caller 1 (-): entry-missing",
                "chain-caller 2 (-): application-id-invalid",
                "chain-caller 3 (https://billing.test): issuer-invalid",
                "chain-caller 4 (https://billing.test): client-invalid",
                "chain-caller 5 (https://projects.test): own-application-name",
                "chain-caller 6 (https://shadow.test): caller-conflict",
                "chain-caller 7 (https://hr.test): caller-conflict",
                "chain-caller 8 (https://payroll.test): caller-conflict",
                "context-binding 1 (-): entry-missing",
                "context-binding 2 (research-user): server-url-invalid",
                "context-binding 3 (research-ftp): server-url-invalid",
                "context-binding 4 (-): tool-invalid",
                "context-binding 5 (research-blank): tool-invalid",
                "context-binding 6 (research-big): snapshot-invalid",
                "context-binding 7 (research-none): snapshot-invalid",
                "context-binding 8 (research-code): code-binding",
                "context-binding 9 (research-twice): binding-conflict",
                "context-binding 10 (research-twice): binding-conflict"
            ],
            warnings.Select(Problem));

        // The next read logs only a reason that newly appears.
        links.Book.Callers.Add(new NhAssistantMcpChainCaller(Projects, "https://projects-login.test"));
        links.Clock.Advance(TimeSpan.FromSeconds(30));
        await links.ViewAsync();
        Assert.Equal(["chain-caller 11 (https://projects.test): own-application-name"], links.Warnings().Skip(warnings.Count).Select(Problem));
    }

    [Fact]
    public async Task Callers_and_chain_bindings_without_the_applications_name_in_chains_are_left_out()
    {
        using var links = Links.Create(named: false);
        links.Book.Callers.Add(PlanningCaller);
        links.Book.Bindings.Add(ResearchBinding with { IncludeChain = true });
        links.Book.Bindings.Add(ResearchBinding with { ServerId = "research-plain" });

        var view = await links.ViewAsync();

        Assert.False(view.ContinuesChains);
        Assert.Null(view.FindCaller(Principal(("iss", Planning))));
        Assert.Equal([ResearchBinding with { ServerId = "research-plain" }], view.StoredBindings);
        Assert.Equal(
            ["chain-caller 0 (https://planning.test): chain-application-missing", "context-binding 0 (research): chain-application-missing"],
            links.Warnings().Select(Problem));
    }

    [Fact]
    public async Task A_failing_source_leaves_out_every_stored_entry_and_keeps_the_code_registrations()
    {
        using var links = Links.Create(codeBinding: ResearchBinding with { ServerId = "research-code" });
        links.Book.Callers.Add(PlanningCaller);
        links.Book.Bindings.Add(ResearchBinding);
        links.Book.Failure = new InvalidOperationException("The link table is not reachable.");

        var failed = await links.ViewAsync();

        // Stored links fail closed; the code registrations still count.
        Assert.Null(failed.FindCaller(Principal(("iss", Planning))));
        Assert.Equal(Orders, failed.FindCaller(Principal(("iss", Orders)))?.ApplicationId);
        Assert.Empty(failed.StoredBindings);
        var warning = Assert.Single(links.Warnings());
        Assert.Contains(typeof(BookLinks).FullName!, warning, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), warning, StringComparison.Ordinal);
        Assert.DoesNotContain("not reachable", warning, StringComparison.Ordinal);

        // The failure counts for the cache time, then the next read counts again.
        links.Book.Failure = null;
        Assert.Null((await links.ViewAsync()).FindCaller(Principal(("iss", Planning))));
        links.Clock.Advance(TimeSpan.FromSeconds(30));
        var recovered = await links.ViewAsync();
        Assert.Equal(Planning, recovered.FindCaller(Principal(("iss", Planning)))?.ApplicationId);
        Assert.Equal([ResearchBinding], recovered.StoredBindings);
    }

    [Fact]
    public async Task A_stored_caller_is_only_the_principal_with_its_issuer_and_client()
    {
        using var links = Links.Create();
        links.Book.Callers.Add(new NhAssistantMcpChainCaller(Planning, Login) { ClientClaimType = "azp", ClientId = "planning-app" });

        var view = await links.ViewAsync();

        Assert.Equal(Planning, view.FindCaller(Principal(("iss", Login), ("azp", "planning-app")))?.ApplicationId);
        // Another client of the issuer, no client, two clients, two issuers and a caller who did not sign
        // in are no reviewed caller.
        Assert.Null(view.FindCaller(Principal(("iss", Login), ("azp", "orders-app"))));
        Assert.Null(view.FindCaller(Principal(("iss", Login))));
        Assert.Null(view.FindCaller(Principal(("iss", Login), ("azp", "planning-app"), ("azp", "orders-app"))));
        Assert.Null(view.FindCaller(Principal(("iss", Login), ("iss", Planning), ("azp", "planning-app"))));
        Assert.Null(view.FindCaller(new ClaimsPrincipal(new ClaimsIdentity([new Claim("iss", Login), new Claim("azp", "planning-app")]))));
        Assert.Null(view.FindCaller(null));
    }

    [Fact]
    public async Task A_stored_caller_for_one_client_is_recognized_before_the_issuers_other_callers()
    {
        using var links = Links.Create(codeCaller: new NhAssistantMcpChainCaller(Orders, Login));
        links.Book.Callers.Add(new NhAssistantMcpChainCaller(Planning, Login) { ClientClaimType = "azp", ClientId = "planning-app" });

        var view = await links.ViewAsync();

        Assert.Equal(Planning, view.FindCaller(Principal(("iss", Login), ("azp", "planning-app")))?.ApplicationId);
        Assert.Equal(Orders, view.FindCaller(Principal(("iss", Login), ("azp", "orders-app")))?.ApplicationId);
    }

    [Fact]
    public async Task Without_a_source_only_the_code_registrations_count_and_nothing_is_read()
    {
        using var links = Links.Create(useSource: false);

        var view = await links.ViewAsync();

        Assert.Equal([OrdersCaller], view.Callers);
        Assert.Empty(view.StoredBindings);
        Assert.Equal(0, links.Book.Reads);
    }

    [Fact]
    public async Task Without_a_cache_time_every_use_reads_the_source()
    {
        using var links = Links.Create(cacheDuration: TimeSpan.Zero);

        await links.ViewAsync();
        links.Book.Callers.Add(PlanningCaller);
        var view = await links.ViewAsync();

        Assert.Equal(Planning, view.FindCaller(Principal(("iss", Planning)))?.ApplicationId);
        Assert.Equal(2, links.Book.Reads);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(301)]
    public void A_cache_time_outside_zero_to_five_minutes_fails_the_host_start(int seconds)
    {
        var options = new NhAssistantMcpOptions { LinkCacheDuration = TimeSpan.FromSeconds(seconds) };

        var failure = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains("LinkCacheDuration", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_registration_refuses_the_callers_a_stored_entry_is_left_out_for()
    {
        var invalid = new[]
        {
            new NhAssistantMcpChainCaller("planning app", Planning),
            new NhAssistantMcpChainCaller(Planning, string.Empty),
            new NhAssistantMcpChainCaller(Planning, Planning) { IssuerClaimType = new string('i', 257) },
            new NhAssistantMcpChainCaller(Planning, Planning) { ClientId = "planning-app" },
            new NhAssistantMcpChainCaller(Planning, Planning) { ClientClaimType = "azp", ClientId = string.Empty }
        };
        var services = new ServiceCollection();
        services.AddNewHeapAssistant(assistant =>
        {
            foreach (var caller in invalid)
            {
                Assert.NotNull(NhAssistantMcpContext.CallerProblem(caller));
                Assert.Throws<ArgumentException>(() => assistant.ContinueMcpChain(caller));
            }

            Assert.Null(NhAssistantMcpContext.CallerProblem(PlanningCaller));
            foreach (var binding in new[]
                     {
                         ResearchBinding with { ServerUrl = "research.test/mcp" },
                         ResearchBinding with { ServerId = "Research" },
                         ResearchBinding with { Snapshot = new NhAssistantConversationSnapshotRequest { MaxRecentMessages = 21 } }
                     })
            {
                Assert.NotNull(NhAssistantMcpContext.BindingProblem(binding));
                Assert.ThrowsAny<ArgumentException>(() => assistant.AddMcpContextBinding(binding));
            }

            Assert.Null(NhAssistantMcpContext.BindingProblem(ResearchBinding));
        });
    }

    private static string Problem(string warning)
    {
        const string prefix = "A stored MCP link was left out: ";
        return warning[prefix.Length..].TrimEnd('.');
    }

    private static AssistantMcpServer Server(string id, string url)
    {
        return new AssistantMcpServer { Id = id, DisplayName = id, Url = url };
    }

    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims)
    {
        return new ClaimsPrincipal(new ClaimsIdentity(
            claims.Select(claim => new Claim(claim.Type, claim.Value)).Append(new Claim(ClaimTypes.NameIdentifier, "member-1")),
            "test"));
    }

    /// <summary>
    /// The link cache of an assistant named <see cref="Projects"/> in chains that reviews the orders
    /// application in code, over a book of stored links and a clock the test moves.
    /// </summary>
    private sealed class Links : IDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly CapturingLoggerProvider _logs;

        private Links(ServiceProvider provider, CapturingLoggerProvider logs, LinkBook book, AssistantTestClock clock)
        {
            _provider = provider;
            _logs = logs;
            Book = book;
            Clock = clock;
        }

        public LinkBook Book { get; }

        public AssistantTestClock Clock { get; }

        public static Links Create(
            bool named = true,
            bool useSource = true,
            NhAssistantMcpChainCaller? codeCaller = null,
            NhAssistantMcpContextBinding? codeBinding = null,
            TimeSpan? cacheDuration = null)
        {
            var book = new LinkBook();
            var clock = new AssistantTestClock(new DateTimeOffset(2031, 5, 6, 7, 8, 0, TimeSpan.Zero));
            var logs = new CapturingLoggerProvider();
            var services = new ServiceCollection();
            services.AddLogging(logging => logging.AddProvider(logs));
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton(book);
            services.AddNewHeapAssistant(assistant =>
            {
                if (named)
                {
                    assistant.UseMcpChainApplication(Projects).ContinueMcpChain(codeCaller ?? OrdersCaller);
                }

                if (codeBinding is not null)
                {
                    assistant.AddMcpContextBinding(codeBinding);
                }

                if (useSource)
                {
                    assistant.UseMcpLinks<BookLinks>();
                }

                if (cacheDuration is { } duration)
                {
                    assistant.ConfigureMcp(mcp => mcp.LinkCacheDuration = duration);
                }
            });
            return new Links(services.BuildServiceProvider(), logs, book, clock);
        }

        public ValueTask<NhAssistantMcpLinkView> ViewAsync()
        {
            return _provider.GetRequiredService<NhAssistantMcpLinkCache>().GetAsync(CancellationToken.None);
        }

        public IReadOnlyList<string> Warnings()
        {
            return _logs.Entries
                .Where(entry => entry.Level == LogLevel.Warning && entry.Category.EndsWith(nameof(NhAssistantMcpLinkCache), StringComparison.Ordinal))
                .Select(entry => entry.Message)
                .ToArray();
        }

        public void Dispose()
        {
            _provider.Dispose();
        }
    }

    /// <summary>
    /// The application's table of reviewed links, which it changes while the host runs.
    /// </summary>
    private sealed class LinkBook
    {
        private int _reads;

        public List<NhAssistantMcpChainCaller> Callers { get; } = [];

        public List<NhAssistantMcpContextBinding> Bindings { get; } = [];

        public Exception? Failure { get; set; }

        public int Reads => _reads;

        public NhAssistantMcpLinkList Read()
        {
            Interlocked.Increment(ref _reads);
            if (Failure is not null)
            {
                throw Failure;
            }

            return new NhAssistantMcpLinkList(Callers.ToArray(), Bindings.ToArray());
        }
    }

    private sealed class BookLinks(LinkBook book) : INhAssistantMcpLinks
    {
        public ValueTask<NhAssistantMcpLinkList> ListAsync(CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(book.Read());
        }
    }
}
