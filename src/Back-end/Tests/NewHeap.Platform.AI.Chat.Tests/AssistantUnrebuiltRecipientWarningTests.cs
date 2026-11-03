using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.Notifications;
using NewHeap.Platform.AI.Chat.Collaboration;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// A delivery that skips a person because the application's principal factory rebuilds nobody for them
/// logs one content-free warning per person per hour, without the actor id, with a hint at a factory of
/// the application's own (FB89). A person the factory rebuilds is never warned about, and the answers of
/// the recipient rules, and so who receives what, stay as they were. No database: the recipient rules
/// ask only the factory and the policy.
/// </summary>
public sealed class AssistantUnrebuiltRecipientWarningTests
{
    private const string Policy = "assistant";

    private static readonly NhAiSubjectInfo Project = new(new NhAiSubjectRef("project", "p-1"), NhAiSubjectInfo.CreateScopeKey(null, null));

    [Fact]
    public async Task A_person_the_factory_does_not_rebuild_is_skipped_with_one_warning_per_hour()
    {
        await using var host = Host.Create();
        var pat = Guid.NewGuid().ToString("D");
        host.People.Unresolved[pat] = true;

        for (var round = 0; round < 2; round++)
        {
            await using var scope = host.Services.CreateAsyncScope();
            var rules = scope.ServiceProvider.GetRequiredService<INhAssistantRecipientRules>();
            Assert.False(await rules.IsResolvedAsync(pat, CancellationToken.None));
            Assert.False(await rules.IsResolvedForAsync(pat, Project, CancellationToken.None));
            Assert.False(await rules.PassesPolicyAsync(pat, Project, Policy, CancellationToken.None));
        }

        var warning = Assert.Single(host.Warnings());
        Assert.DoesNotContain(pat, warning.Message, StringComparison.Ordinal);
        Assert.Contains("INhAiPrincipalFactory", warning.Message, StringComparison.Ordinal);

        // Within the hour nothing more; after it, the next skip warns again.
        host.Clock.Advance(TimeSpan.FromMinutes(59));
        await host.SkipAsync(pat);
        Assert.Single(host.Warnings());
        host.Clock.Advance(TimeSpan.FromMinutes(1));
        await host.SkipAsync(pat);
        Assert.Equal(2, host.Warnings().Count);
    }

    [Fact]
    public async Task A_person_the_factory_rebuilds_is_never_warned_about_and_receives_as_before()
    {
        await using var host = Host.Create();
        var quinn = Guid.NewGuid().ToString("D");
        var pat = Guid.NewGuid().ToString("D");
        var alice = Guid.NewGuid().ToString("D");
        host.People.Unresolved[pat] = true;
        host.People.Unresolved[alice] = true;

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var rules = scope.ServiceProvider.GetRequiredService<INhAssistantRecipientRules>();
            Assert.True(await rules.IsResolvedAsync(quinn, CancellationToken.None));
            Assert.True(await rules.IsResolvedForAsync(quinn, Project, CancellationToken.None));
            Assert.True(await rules.PassesPolicyAsync(quinn, Project, Policy, CancellationToken.None));
        }

        Assert.Empty(host.Warnings());

        // Each person the factory does not rebuild gets a warning of their own.
        await host.SkipAsync(pat);
        await host.SkipAsync(alice);
        await host.SkipAsync(pat);
        Assert.Equal(2, host.Warnings().Count);
    }

    [Fact]
    public async Task The_warnings_remember_a_bounded_number_of_people()
    {
        await using var host = Host.Create();
        var warnings = host.Services.GetRequiredService<NhAssistantUnrebuiltRecipientWarnings>();

        for (var person = 0; person < NhAssistantUnrebuiltRecipientWarnings.MaxPeople; person++)
        {
            warnings.Skipped("person-" + person);
        }
        Assert.Equal(NhAssistantUnrebuiltRecipientWarnings.MaxPeople, host.Warnings().Count);

        // A memo that is full warns about nobody new until the window of its entries ended.
        warnings.Skipped("one-more");
        Assert.Equal(NhAssistantUnrebuiltRecipientWarnings.MaxPeople, host.Warnings().Count);
        host.Clock.Advance(NhAssistantUnrebuiltRecipientWarnings.Window);
        warnings.Skipped("one-more");
        Assert.Equal(NhAssistantUnrebuiltRecipientWarnings.MaxPeople + 1, host.Warnings().Count);
    }

    /// <summary>
    /// The recipient rules of the ASP.NET integration with the people of <see cref="LiveReadPeople"/>, the
    /// assistant's policy, a test clock and captured logs.
    /// </summary>
    private sealed class Host : IAsyncDisposable
    {
        private Host(ServiceProvider services, LiveReadPeople people, AssistantTestClock clock, CapturingLoggerProvider logs)
        {
            Services = services;
            People = people;
            Clock = clock;
            Logs = logs;
        }

        public ServiceProvider Services { get; }

        public LiveReadPeople People { get; }

        public AssistantTestClock Clock { get; }

        public CapturingLoggerProvider Logs { get; }

        public static Host Create()
        {
            var people = new LiveReadPeople();
            var clock = new AssistantTestClock(new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero));
            var logs = new CapturingLoggerProvider();
            var services = new ServiceCollection();
            services.AddLogging(logging => logging.AddProvider(logs));
            services.AddAuthorization(options => options.AddPolicy(Policy, policy => policy.RequireClaim("permission", "assistant")));
            services.AddSingleton<INhAiPrincipalFactory>(people);
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton<NhAssistantUnrebuiltRecipientWarnings>();
            services.AddScoped<INhAssistantRecipientRules, NhAssistantPrincipalRecipientRules>();
            return new Host(services.BuildServiceProvider(validateScopes: true), people, clock, logs);
        }

        /// <summary>The warnings logged so far.</summary>
        public IReadOnlyList<CapturedLogEntry> Warnings()
        {
            return Logs.Entries
                .Where(entry => entry.Category == typeof(NhAssistantUnrebuiltRecipientWarnings).FullName && entry.Level == LogLevel.Warning)
                .ToArray();
        }

        /// <summary>A delivery asks whether <paramref name="actorId"/> is resolved and skips them.</summary>
        public async Task SkipAsync(string actorId)
        {
            await using var scope = Services.CreateAsyncScope();
            Assert.False(await scope.ServiceProvider.GetRequiredService<INhAssistantRecipientRules>()
                .IsResolvedAsync(actorId, CancellationToken.None));
        }

        public ValueTask DisposeAsync()
        {
            return Services.DisposeAsync();
        }
    }
}
