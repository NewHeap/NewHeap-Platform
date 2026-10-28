using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

public sealed class AssistantToolPresentationTests
{
    [Fact]
    public async Task A_presenter_receives_the_requested_culture_and_exact_governed_values()
    {
        var arguments = new ProjectStatusInput(Guid.NewGuid(), "Active");
        var invocation = Context();
        var presenter = new RecordingPresenter();
        var resolver = Resolver(presenter);
        var culture = CultureInfo.GetCultureInfo("nl-NL");

        var presentation = await resolver.ResolveApprovalAsync(
            TestProjectToolCatalog.ChangeStatus,
            arguments,
            invocation,
            culture,
            CancellationToken.None);

        Assert.NotNull(presentation);
        Assert.Same(arguments, presenter.Context?.Arguments);
        Assert.Same(invocation, presenter.Context?.InvocationContext);
        Assert.Same(culture, presenter.Context?.Culture);
    }

    [Fact]
    public async Task Failures_oversize_output_and_confidential_data_use_the_safe_fallback()
    {
        var failing = new ThrowingPresenter();
        var oversize = new OversizePresenter();
        var resolver = Resolver(failing, oversize);

        var name = await resolver.ResolveDisplayNameAsync(
            TestProjectToolCatalog.ChangeStatus,
            CultureInfo.InvariantCulture,
            CancellationToken.None);
        var presentation = await resolver.ResolveApprovalAsync(
            TestProjectToolCatalog.ChangeStatus,
            new ProjectStatusInput(Guid.NewGuid(), "Active"),
            Context(),
            CultureInfo.InvariantCulture,
            CancellationToken.None);
        var confidential = await resolver.ResolveApprovalAsync(
            TestProjectToolCatalog.ChangeStatus with { DataClassification = NhAiDataClassification.Confidential },
            new ProjectStatusInput(Guid.NewGuid(), "Active"),
            Context(),
            CultureInfo.InvariantCulture,
            CancellationToken.None);

        Assert.Equal(TestProjectToolCatalog.ChangeStatus.Id, name);
        Assert.Null(presentation);
        Assert.Null(confidential);
    }

    [Fact]
    public void Stored_presentations_are_bounded_and_old_or_corrupt_values_are_optional()
    {
        var value = new NhAssistantApprovalPresentation(
            "Change project status",
            "Change Alpha to Active.",
            [new NhAssistantPresentationField("Project", "Alpha")]);

        var json = NhAssistantToolPresentationResolver.Serialize(value);

        var restored = NhAssistantToolPresentationResolver.Deserialize(json);
        Assert.NotNull(restored);
        Assert.Equal(value.ToolDisplayName, restored.ToolDisplayName);
        Assert.Equal(value.Summary, restored.Summary);
        Assert.Equal(value.Fields, restored.Fields);
        Assert.Null(NhAssistantToolPresentationResolver.Deserialize(null));
        Assert.Null(NhAssistantToolPresentationResolver.Deserialize("{not-json"));
        Assert.Null(NhAssistantToolPresentationResolver.Serialize(
            value with { Summary = new string('x', NhAssistantToolPresentationResolver.MaxSummaryLength + 1) }));
    }

    [Fact]
    public async Task A_presenter_timeout_falls_back_without_bypassing_the_approval()
    {
        var resolver = Resolver(new HangingPresenter());

        var presentation = await resolver.ResolveApprovalAsync(
            TestProjectToolCatalog.ChangeStatus,
            new ProjectStatusInput(Guid.NewGuid(), "Active"),
            Context(),
            CultureInfo.InvariantCulture,
            CancellationToken.None);

        Assert.Null(presentation);
    }

    [Fact]
    public async Task A_dropped_presentation_logs_the_presenter_the_tool_and_the_broken_rule_without_content()
    {
        var logs = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var nine = Enumerable.Range(1, 9).Select(index => new NhAssistantPresentationField($"Label {index}", $"secret value {index}")).ToArray();
        var resolver = new NhAssistantToolPresentationResolver(
            [new FixedPresenter(new string('n', 161), new NhAssistantApprovalPresentation("Status", "secret summary", nine))],
            factory.CreateLogger<NhAssistantToolPresentationResolver>());

        var presentation = await resolver.ResolveApprovalAsync(
            TestProjectToolCatalog.ChangeStatus,
            new ProjectStatusInput(Guid.NewGuid(), "Active"),
            Context(),
            CultureInfo.GetCultureInfo("nl-NL"),
            CancellationToken.None);
        var name = await resolver.ResolveDisplayNameAsync(
            TestProjectToolCatalog.ChangeStatus,
            CultureInfo.GetCultureInfo("nl-NL"),
            CancellationToken.None);

        // The cap of eight fields stays: the approver reads the generic summary, never part of the fields.
        Assert.Null(presentation);
        Assert.Equal(TestProjectToolCatalog.ChangeStatus.Id, name);
        var warnings = logs.Entries.Where(entry => entry.Level == LogLevel.Warning).Select(entry => entry.Message).ToArray();
        Assert.Equal(2, warnings.Length);
        Assert.Contains(warnings, message => message.Contains(NhAssistantPresentationRules.TooManyFields, StringComparison.Ordinal)
            && message.Contains(typeof(FixedPresenter).FullName!, StringComparison.Ordinal)
            && message.Contains(TestProjectToolCatalog.ChangeStatus.Id, StringComparison.Ordinal)
            && message.Contains("nl-NL", StringComparison.Ordinal));
        Assert.Contains(warnings, message => message.Contains(NhAssistantPresentationRules.DisplayNameTooLong, StringComparison.Ordinal));
        Assert.DoesNotContain(warnings, message => message.Contains("secret", StringComparison.Ordinal) || message.Contains("nnnn", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_presenter_that_does_not_handle_the_tool_or_a_presentation_within_the_caps_logs_nothing()
    {
        var logs = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var eight = Enumerable.Range(1, NhAssistantPresentationLimits.MaxFields)
            .Select(index => new NhAssistantPresentationField($"Label {index}", $"Value {index}"))
            .ToArray();
        var resolver = new NhAssistantToolPresentationResolver(
            [new FixedPresenter(null, null), new FixedPresenter("Change status", new NhAssistantApprovalPresentation("Status", "Summary", eight))],
            factory.CreateLogger<NhAssistantToolPresentationResolver>());

        var presentation = await resolver.ResolveApprovalAsync(
            TestProjectToolCatalog.ChangeStatus,
            new ProjectStatusInput(Guid.NewGuid(), "Active"),
            Context(),
            CultureInfo.InvariantCulture,
            CancellationToken.None);

        Assert.Equal(NhAssistantPresentationLimits.MaxFields, presentation!.Fields.Count);
        Assert.DoesNotContain(logs.Entries, entry => entry.Level >= LogLevel.Warning);
    }

    [Theory]
    [InlineData(NhAssistantPresentationRules.DisplayNameMissing)]
    [InlineData(NhAssistantPresentationRules.DisplayNameTooLong)]
    [InlineData(NhAssistantPresentationRules.SummaryMissing)]
    [InlineData(NhAssistantPresentationRules.SummaryTooLong)]
    [InlineData(NhAssistantPresentationRules.FieldsMissing)]
    [InlineData(NhAssistantPresentationRules.TooManyFields)]
    [InlineData(NhAssistantPresentationRules.FieldLabelMissing)]
    [InlineData(NhAssistantPresentationRules.FieldLabelTooLong)]
    [InlineData(NhAssistantPresentationRules.FieldValueMissing)]
    [InlineData(NhAssistantPresentationRules.FieldValueTooLong)]
    [InlineData(NhAssistantPresentationRules.NoticeTooLong)]
    [InlineData(NhAssistantPresentationRules.TooLong)]
    public void Each_cap_has_its_own_rule(string rule)
    {
        var valid = new NhAssistantApprovalPresentation("Status", "Summary", [new NhAssistantPresentationField("Project", "Alpha")]);
        var field = new NhAssistantPresentationField("Project", "Alpha");
        var broken = rule switch
        {
            NhAssistantPresentationRules.DisplayNameMissing => valid with { ToolDisplayName = " " },
            NhAssistantPresentationRules.DisplayNameTooLong => valid with { ToolDisplayName = new string('x', NhAssistantPresentationLimits.MaxDisplayNameLength + 1) },
            NhAssistantPresentationRules.SummaryMissing => valid with { Summary = "" },
            NhAssistantPresentationRules.SummaryTooLong => valid with { Summary = new string('x', NhAssistantPresentationLimits.MaxSummaryLength + 1) },
            NhAssistantPresentationRules.FieldsMissing => valid with { Fields = null! },
            NhAssistantPresentationRules.TooManyFields => valid with { Fields = Enumerable.Repeat(field, NhAssistantPresentationLimits.MaxFields + 1).ToArray() },
            NhAssistantPresentationRules.FieldLabelMissing => valid with { Fields = [field with { Label = "" }] },
            NhAssistantPresentationRules.FieldLabelTooLong => valid with { Fields = [field with { Label = new string('x', NhAssistantPresentationLimits.MaxFieldLabelLength + 1) }] },
            NhAssistantPresentationRules.FieldValueMissing => valid with { Fields = [field with { Value = "" }] },
            NhAssistantPresentationRules.FieldValueTooLong => valid with { Fields = [field with { Value = new string('x', NhAssistantPresentationLimits.MaxFieldValueLength + 1) }] },
            NhAssistantPresentationRules.NoticeTooLong => valid with { Notice = new string('x', NhAssistantPresentationLimits.MaxNoticeLength + 1) },
            // Within every field cap, but escaped characters make the stored JSON too long.
            _ => valid with
            {
                Summary = new string('"', NhAssistantPresentationLimits.MaxSummaryLength),
                Fields = Enumerable.Repeat(field with { Value = new string('"', NhAssistantPresentationLimits.MaxFieldValueLength) }, NhAssistantPresentationLimits.MaxFields).ToArray()
            }
        };

        Assert.Null(NhAssistantPresentationLimits.FindBrokenRule(valid));
        Assert.Equal(rule, NhAssistantPresentationLimits.FindBrokenRule(broken));
    }

    [Fact]
    public async Task The_public_check_finds_the_rule_by_which_the_resolver_drops_a_presentation()
    {
        // FB18: a presenter's unit test calls the same check the resolver drops a presentation by.
        var field = new NhAssistantPresentationField("Project", "Alpha");
        var kept = new NhAssistantApprovalPresentation(
            "Status",
            new string('x', NhAssistantPresentationLimits.MaxSummaryLength),
            Enumerable.Repeat(field, NhAssistantPresentationLimits.MaxFields).ToArray());
        var dropped = kept with { Fields = Enumerable.Repeat(field, NhAssistantPresentationLimits.MaxFields + 1).ToArray() };

        var keptResult = await Resolver(new FixedPresenter("Status", kept)).ResolveApprovalAsync(
            TestProjectToolCatalog.ChangeStatus,
            new ProjectStatusInput(Guid.NewGuid(), "Active"),
            Context(),
            CultureInfo.InvariantCulture,
            CancellationToken.None);
        var droppedResult = await Resolver(new FixedPresenter("Status", dropped)).ResolveApprovalAsync(
            TestProjectToolCatalog.ChangeStatus,
            new ProjectStatusInput(Guid.NewGuid(), "Active"),
            Context(),
            CultureInfo.InvariantCulture,
            CancellationToken.None);

        Assert.Null(NhAssistantPresentationLimits.FindBrokenRule(kept));
        Assert.Same(kept, keptResult);
        Assert.Equal(NhAssistantPresentationRules.TooManyFields, NhAssistantPresentationLimits.FindBrokenRule(dropped));
        Assert.Null(droppedResult);
        Assert.Throws<ArgumentNullException>(() => NhAssistantPresentationLimits.FindBrokenRule(null!));
    }

    [Fact]
    public void Approvals_are_presented_in_at_most_eight_supported_cultures_with_one_warning()
    {
        var logs = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var supported = new[] { "en", "nl", "de", "fr", "es", "it", "pt", "pl", "sv", "da" }
            .Select(CultureInfo.GetCultureInfo)
            .ToList();
        var cultures = new NewHeap.Platform.AI.Chat.AspNet.NhAssistantRequestPresentationCultures(
            Options.Create(new RequestLocalizationOptions { SupportedUICultures = supported }),
            factory.CreateLogger<NewHeap.Platform.AI.Chat.AspNet.NhAssistantRequestPresentationCultures>());

        var first = cultures.GetCultures();
        var second = cultures.GetCultures();

        Assert.Equal(NhAssistantPresentationLimits.MaxCultures, first.Count);
        Assert.Equal(supported.Take(NhAssistantPresentationLimits.MaxCultures).Select(culture => culture.Name), first.Select(culture => culture.Name));
        Assert.Equal(first.Count, second.Count);
        var warning = Assert.Single(logs.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains("supports 10 UI cultures", warning.Message, StringComparison.Ordinal);
    }

    private static NhAssistantToolPresentationResolver Resolver(params INhAssistantToolPresenter[] presenters)
    {
        return new NhAssistantToolPresentationResolver(
            presenters,
            NullLogger<NhAssistantToolPresentationResolver>.Instance);
    }

    private sealed class FixedPresenter(string? displayName, NhAssistantApprovalPresentation? presentation) : INhAssistantToolPresenter
    {
        public ValueTask<string?> GetDisplayNameAsync(
            NhAssistantToolDisplayNameContext context,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(displayName);
        }

        public ValueTask<NhAssistantApprovalPresentation?> PresentApprovalAsync(
            NhAssistantApprovalPresentationContext context,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(presentation);
        }
    }

    private static NhAiInvocationContext Context()
    {
        return new NhAiInvocationContext("agent-1", "assistant", new Dictionary<string, string>
        {
            ["division-id"] = "division-1"
        })
        {
            AccountableOwnerId = "user-1",
            RunId = Guid.NewGuid().ToString()
        };
    }

    private sealed class RecordingPresenter : INhAssistantToolPresenter
    {
        public NhAssistantApprovalPresentationContext? Context { get; private set; }

        public ValueTask<string?> GetDisplayNameAsync(
            NhAssistantToolDisplayNameContext context,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<string?>(null);
        }

        public ValueTask<NhAssistantApprovalPresentation?> PresentApprovalAsync(
            NhAssistantApprovalPresentationContext context,
            CancellationToken cancellationToken)
        {
            Context = context;
            return ValueTask.FromResult<NhAssistantApprovalPresentation?>(
                new NhAssistantApprovalPresentation("Status", "Summary", []));
        }
    }

    private sealed class ThrowingPresenter : INhAssistantToolPresenter
    {
        public ValueTask<string?> GetDisplayNameAsync(
            NhAssistantToolDisplayNameContext context,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("sensitive presenter failure");
        }

        public ValueTask<NhAssistantApprovalPresentation?> PresentApprovalAsync(
            NhAssistantApprovalPresentationContext context,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("sensitive presenter failure");
        }
    }

    private sealed class OversizePresenter : INhAssistantToolPresenter
    {
        public ValueTask<string?> GetDisplayNameAsync(
            NhAssistantToolDisplayNameContext context,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<string?>(new string('x', 161));
        }

        public ValueTask<NhAssistantApprovalPresentation?> PresentApprovalAsync(
            NhAssistantApprovalPresentationContext context,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<NhAssistantApprovalPresentation?>(
                new NhAssistantApprovalPresentation("Status", new string('x', 513), []));
        }
    }

    private sealed class HangingPresenter : INhAssistantToolPresenter
    {
        public async ValueTask<string?> GetDisplayNameAsync(
            NhAssistantToolDisplayNameContext context,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return null;
        }

        public async ValueTask<NhAssistantApprovalPresentation?> PresentApprovalAsync(
            NhAssistantApprovalPresentationContext context,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return null;
        }
    }
}
