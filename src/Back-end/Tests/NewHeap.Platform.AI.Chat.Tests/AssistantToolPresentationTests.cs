using System.Globalization;
using System.Text.Json;
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
    [InlineData(NhAssistantPresentationRules.FieldCurrentValueTooLong)]
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
            NhAssistantPresentationRules.FieldCurrentValueTooLong => valid with { Fields = [field with { CurrentValue = new string('x', NhAssistantPresentationLimits.MaxFieldValueLength + 1) }] },
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

    [Fact]
    public void A_field_of_a_change_states_its_current_value_and_old_fields_stay_plain_facts()
    {
        var plain = new NhAssistantPresentationField("Project", "Alpha");
        var changed = NhAssistantPresentationField.Changing("Status", "Active", "On hold");
        var unchanged = NhAssistantPresentationField.Changing("Owner", "Ann", "Ann");
        var added = NhAssistantPresentationField.Changing("Deadline", null, "1 December");
        var cleared = NhAssistantPresentationField.Changing("Description", "Launch plan", null);
        var presentation = new NhAssistantApprovalPresentation(
            "Change project",
            "Change Alpha.",
            [plain, changed, unchanged, added, cleared]);

        Assert.Equal(NhAssistantPresentationFieldChange.None, plain.Change);
        Assert.Equal(NhAssistantPresentationFieldChange.Changed, changed.Change);
        Assert.Equal(NhAssistantPresentationFieldChange.Unchanged, unchanged.Change);
        Assert.Equal(NhAssistantPresentationFieldChange.Added, added.Change);
        Assert.Equal("", added.CurrentValue);
        Assert.Equal(NhAssistantPresentationFieldChange.Cleared, cleared.Change);
        Assert.Equal("", cleared.Value);
        // A cleared field keeps within the caps; a field without any value does not.
        Assert.Null(NhAssistantPresentationLimits.FindBrokenRule(presentation));
        Assert.Equal(
            NhAssistantPresentationRules.FieldValueMissing,
            NhAssistantPresentationLimits.FindBrokenRule(presentation with { Fields = [NhAssistantPresentationField.Changing("Description", "", "")] }));

        // The stored JSON of a plain fact is what it was before current values existed.
        var json = NhAssistantToolPresentationResolver.Serialize(presentation)!;
        Assert.Contains("{\"label\":\"Project\",\"value\":\"Alpha\"}", json, StringComparison.Ordinal);
        Assert.Contains("\"currentValue\":\"Active\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"change\"", json, StringComparison.Ordinal);
        var restored = NhAssistantToolPresentationResolver.Deserialize(json)!;
        Assert.Equal(presentation.Fields, restored.Fields);
        Assert.Equal(NhAssistantPresentationFieldChange.Cleared, restored.Fields[4].Change);

        // An approval stored before current values existed reads as plain facts.
        var older = NhAssistantToolPresentationResolver.Deserialize(
            "{\"toolDisplayName\":\"Change project status\",\"summary\":\"Change Alpha.\",\"fields\":[{\"label\":\"New status\",\"value\":\"Active\"}],\"notice\":null}");
        var olderField = Assert.Single(older!.Fields);
        Assert.Null(olderField.CurrentValue);
        Assert.Equal(NhAssistantPresentationFieldChange.None, olderField.Change);
    }

    [Fact]
    public void Approval_fields_send_the_current_value_and_the_change_only_when_stated()
    {
        var dto = NewHeap.Platform.AI.Chat.AspNet.NhAssistantApprovalPresentationDto.From(new NhAssistantApprovalPresentation(
            "Change project",
            "Change Alpha.",
            [
                new NhAssistantPresentationField("Project", "Alpha"),
                NhAssistantPresentationField.Changing("Status", "Active", "On hold"),
                NhAssistantPresentationField.Changing("Deadline", "1 December", null)
            ]))!;

        var json = JsonSerializer.SerializeToElement(
            dto,
            NewHeap.Platform.AI.Chat.AspNet.NhAssistantJsonSerializerContext.Default.NhAssistantApprovalPresentationDto);

        var fields = json.GetProperty("fields");
        AssertNames(fields[0], "label", "value");
        AssertNames(fields[1], "label", "value", "currentValue", "change");
        Assert.Equal("Active", fields[1].GetProperty("currentValue").GetString());
        Assert.Equal("changed", fields[1].GetProperty("change").GetString());
        Assert.Equal("", fields[2].GetProperty("value").GetString());
        Assert.Equal("cleared", fields[2].GetProperty("change").GetString());
    }

    [Theory]
    [InlineData(NhAssistantPresentationRules.TitleMissing)]
    [InlineData(NhAssistantPresentationRules.TitleTooLong)]
    [InlineData(NhAssistantPresentationRules.SummaryTooLong)]
    [InlineData(NhAssistantPresentationRules.FieldsMissing)]
    [InlineData(NhAssistantPresentationRules.TooManyFields)]
    [InlineData(NhAssistantPresentationRules.FieldLabelMissing)]
    [InlineData(NhAssistantPresentationRules.FieldValueMissing)]
    [InlineData(NhAssistantPresentationRules.FieldValueTooLong)]
    [InlineData(NhAssistantPresentationRules.FieldCurrentValueTooLong)]
    [InlineData(NhAssistantPresentationRules.LinkInvalid)]
    [InlineData(NhAssistantPresentationRules.LinkLabelMissing)]
    [InlineData(NhAssistantPresentationRules.LinkLabelTooLong)]
    [InlineData(NhAssistantPresentationRules.TooLong)]
    public void Each_cap_of_a_result_card_has_its_own_rule(string rule)
    {
        var field = new NhAssistantPresentationField("Status", "Active");
        var valid = new NhAssistantToolResultPresentation(
            "Alpha",
            [field],
            "One project",
            new NhAssistantPresentationLink("/projects/alpha?tab=tasks", "Open project"));
        var broken = rule switch
        {
            NhAssistantPresentationRules.TitleMissing => valid with { Title = " " },
            NhAssistantPresentationRules.TitleTooLong => valid with { Title = new string('x', NhAssistantPresentationLimits.MaxTitleLength + 1) },
            NhAssistantPresentationRules.SummaryTooLong => valid with { Summary = new string('x', NhAssistantPresentationLimits.MaxSummaryLength + 1) },
            NhAssistantPresentationRules.FieldsMissing => valid with { Fields = null! },
            NhAssistantPresentationRules.TooManyFields => valid with { Fields = Enumerable.Repeat(field, NhAssistantPresentationLimits.MaxFields + 1).ToArray() },
            NhAssistantPresentationRules.FieldLabelMissing => valid with { Fields = [field with { Label = "" }] },
            NhAssistantPresentationRules.FieldValueMissing => valid with { Fields = [field with { Value = "" }] },
            NhAssistantPresentationRules.FieldValueTooLong => valid with { Fields = [field with { Value = new string('x', NhAssistantPresentationLimits.MaxFieldValueLength + 1) }] },
            NhAssistantPresentationRules.FieldCurrentValueTooLong => valid with { Fields = [field with { CurrentValue = new string('x', NhAssistantPresentationLimits.MaxFieldValueLength + 1) }] },
            NhAssistantPresentationRules.LinkInvalid => valid with { Link = valid.Link! with { Url = "https://example.com/projects" } },
            NhAssistantPresentationRules.LinkLabelMissing => valid with { Link = valid.Link! with { Label = "" } },
            NhAssistantPresentationRules.LinkLabelTooLong => valid with { Link = valid.Link! with { Label = new string('x', NhAssistantPresentationLimits.MaxLinkLabelLength + 1) } },
            _ => valid with
            {
                Summary = new string('"', NhAssistantPresentationLimits.MaxSummaryLength),
                Fields = Enumerable.Repeat(field with { Value = new string('"', NhAssistantPresentationLimits.MaxFieldValueLength) }, NhAssistantPresentationLimits.MaxFields).ToArray()
            }
        };

        Assert.Null(NhAssistantPresentationLimits.FindBrokenResultRule(valid));
        Assert.Null(NhAssistantPresentationLimits.FindBrokenResultRule(valid with { Summary = null, Link = null, Fields = [] }));
        Assert.Equal(rule, NhAssistantPresentationLimits.FindBrokenResultRule(broken));
        Assert.Throws<ArgumentNullException>(() => NhAssistantPresentationLimits.FindBrokenResultRule(null!));
    }

    [Theory]
    [InlineData("//evil.example/projects")]
    [InlineData("/\\evil.example")]
    [InlineData("projects/42")]
    [InlineData("javascript:alert(1)")]
    [InlineData("/projects/4 2")]
    [InlineData("")]
    public void A_result_card_links_only_to_a_path_within_the_application(string url)
    {
        var card = new NhAssistantToolResultPresentation("Alpha", [], Link: new NhAssistantPresentationLink(url, "Open"));

        Assert.Equal(NhAssistantPresentationRules.LinkInvalid, NhAssistantPresentationLimits.FindBrokenResultRule(card));
        Assert.Equal(
            NhAssistantPresentationRules.LinkInvalid,
            NhAssistantPresentationLimits.FindBrokenResultRule(card with { Link = new NhAssistantPresentationLink("/" + new string('a', NhAssistantPresentationLimits.MaxLinkUrlLength), "Open") }));
    }

    [Fact]
    public async Task A_result_presenter_receives_the_data_and_arguments_and_presents_in_every_culture()
    {
        var presenter = new RecordingResultPresenter();
        var resolver = new NhAssistantToolPresentationResolver(
            [],
            NullLogger<NhAssistantToolPresentationResolver>.Instance,
            new FixedCultures("en-US", "nl-NL", "de-DE"),
            resultPresenters: [new FixedResultPresenter(null), presenter]);
        var data = JsonSerializer.SerializeToElement(new[] { new { id = Guid.Empty, name = "Alpha" } });
        var arguments = JsonSerializer.SerializeToElement(new { input = new { query = "alpha" } });

        var card = await resolver.ResolveResultInCulturesAsync(
            TestProjectToolCatalog.Search,
            data,
            arguments,
            CultureInfo.GetCultureInfo("en-US"),
            CancellationToken.None);

        Assert.NotNull(card);
        Assert.Equal("Found in en-US", card.Presentation.Title);
        Assert.Equal(["en-US", "nl-NL", "de-DE"], presenter.Cultures);
        // The presenter ran with the current culture set to each other culture it presented in.
        Assert.Equal(["nl-NL", "de-DE"], presenter.CurrentCultures.Skip(1));
        Assert.Equal("Alpha", presenter.Context!.ReadData<ProjectSearchItem[]>()![0].Name);
        Assert.Equal("alpha", presenter.Context.Arguments!.Value.GetProperty("input").GetProperty("query").GetString());
        Assert.Same(TestProjectToolCatalog.Search, presenter.Context.Descriptor);
        Assert.Null(presenter.Context.ReadData<ProjectStatusInput>());
        Assert.Equal(3, card.Localized!.Count);

        // Each viewer reads the card in their language, a regional variant in its language, and otherwise
        // the card of the turn's culture.
        Assert.Equal("Found in nl-NL", NhAssistantToolPresentationResolver.PickResult(card.Presentation, card.Localized, CultureInfo.GetCultureInfo("nl-NL"))!.Title);
        Assert.Equal("Found in nl-NL", NhAssistantToolPresentationResolver.PickResult(card.Presentation, card.Localized, CultureInfo.GetCultureInfo("nl-BE"))!.Title);
        Assert.Equal("Found in en-US", NhAssistantToolPresentationResolver.PickResult(card.Presentation, card.Localized, CultureInfo.GetCultureInfo("fr-FR"))!.Title);
        Assert.Equal("Found in en-US", NhAssistantToolPresentationResolver.PickResult(card.Presentation, null, CultureInfo.GetCultureInfo("nl-NL"))!.Title);
        Assert.Null(NhAssistantToolPresentationResolver.PickResult(card.Presentation with { Title = "" }, null, CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task A_broken_failing_or_slow_result_card_and_confidential_data_show_no_card_with_a_content_free_warning()
    {
        var logs = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var broken = new FixedResultPresenter(new NhAssistantToolResultPresentation(
            "secret title",
            [],
            Link: new NhAssistantPresentationLink("https://secret.example", "secret")));
        var kept = new FixedResultPresenter(new NhAssistantToolResultPresentation("Alpha", []));
        var data = JsonSerializer.SerializeToElement(new { name = "Alpha" });
        var resolver = new NhAssistantToolPresentationResolver(
            [],
            factory.CreateLogger<NhAssistantToolPresentationResolver>(),
            resultPresenters: [new ThrowingResultPresenter(), new HangingResultPresenter(), broken, kept]);

        var card = await resolver.ResolveResultInCulturesAsync(TestProjectToolCatalog.Search, data, null, CultureInfo.GetCultureInfo("nl-NL"), CancellationToken.None);
        var confidential = await resolver.ResolveResultInCulturesAsync(
            TestProjectToolCatalog.Search with { DataClassification = NhAiDataClassification.Confidential },
            data,
            null,
            CultureInfo.InvariantCulture,
            CancellationToken.None);
        var none = await new NhAssistantToolPresentationResolver([], NullLogger<NhAssistantToolPresentationResolver>.Instance, resultPresenters: [broken])
            .ResolveResultInCulturesAsync(TestProjectToolCatalog.Search, data, null, CultureInfo.InvariantCulture, CancellationToken.None);

        // The next presenter's card within the limits is shown; nothing of the dropped one.
        Assert.Same(kept.Presentation, card!.Presentation);
        Assert.Null(card.Localized);
        // A confidential tool asks no presenter.
        Assert.Null(confidential);
        Assert.Equal(1, kept.Calls);
        Assert.Null(none);
        var warnings = logs.Entries.Where(entry => entry.Level == LogLevel.Warning).Select(entry => entry.Message).ToArray();
        Assert.Contains(warnings, message => message.Contains(NhAssistantPresentationRules.LinkInvalid, StringComparison.Ordinal)
            && message.Contains(typeof(FixedResultPresenter).FullName!, StringComparison.Ordinal)
            && message.Contains(TestProjectToolCatalog.Search.Id, StringComparison.Ordinal)
            && message.Contains("nl-NL", StringComparison.Ordinal)
            && message.Contains("result presentation", StringComparison.Ordinal));
        Assert.Contains(warnings, message => message.Contains(typeof(ThrowingResultPresenter).FullName!, StringComparison.Ordinal));
        Assert.Contains(warnings, message => message.Contains(typeof(HangingResultPresenter).FullName!, StringComparison.Ordinal)
            && message.Contains("timed out", StringComparison.Ordinal));
        Assert.DoesNotContain(warnings, message => message.Contains("secret", StringComparison.Ordinal));
    }

    [Fact]
    public void Tool_completed_carries_a_result_card_only_when_there_is_one()
    {
        var invocationId = Guid.NewGuid();
        var card = new NhAssistantToolResultPresentation(
            "Alpha",
            [new NhAssistantPresentationField("Status", "Active"), NhAssistantPresentationField.Changing("Owner", "Ann", "Bob")],
            "One project",
            new NhAssistantPresentationLink("/projects/alpha", "Open project"));

        var (plainName, plainData) = NewHeap.Platform.AI.Chat.AspNet.NhAssistantServerSentEventsResult.Serialize(
            new NhAssistantToolCompletedEvent(invocationId, "succeeded", null, "[]"));
        var (name, data) = NewHeap.Platform.AI.Chat.AspNet.NhAssistantServerSentEventsResult.Serialize(
            new NhAssistantToolCompletedEvent(invocationId, "succeeded", null, "[]") { ResultPresentation = card });

        Assert.Equal("tool.completed", plainName);
        Assert.Equal("tool.completed", name);
        using var plain = JsonDocument.Parse(plainData);
        AssertNames(plain.RootElement, "invocationId", "status", "resultCode", "resultPreview");
        using var presented = JsonDocument.Parse(data);
        AssertNames(presented.RootElement, "invocationId", "status", "resultCode", "resultPreview", "resultPresentation");
        var json = presented.RootElement.GetProperty("resultPresentation");
        AssertNames(json, "title", "summary", "fields", "link");
        AssertNames(json.GetProperty("link"), "url", "label");
        AssertNames(json.GetProperty("fields")[1], "label", "value", "currentValue", "change");
        Assert.Equal("/projects/alpha", json.GetProperty("link").GetProperty("url").GetString());

        // The conversation's tool-call part carries the same card, and nothing of it when shielded.
        var part = new NhAssistantToolCallPartView(invocationId, "projects.search", 1, "Search", "succeeded", null, "[]", null)
        {
            ResultPresentation = card
        };
        var partJson = JsonSerializer.SerializeToElement(
            NewHeap.Platform.AI.Chat.AspNet.NhAssistantDtoMapper.ToDto(part),
            NewHeap.Platform.AI.Chat.AspNet.NhAssistantJsonSerializerContext.Default.NhAssistantMessagePartDto);
        Assert.Equal("Alpha", partJson.GetProperty("resultPresentation").GetProperty("title").GetString());
        var shielded = JsonSerializer.SerializeToElement(
            NewHeap.Platform.AI.Chat.AspNet.NhAssistantDtoMapper.ToDto(part with { ResultPresentation = null }),
            NewHeap.Platform.AI.Chat.AspNet.NhAssistantJsonSerializerContext.Default.NhAssistantMessagePartDto);
        Assert.False(shielded.TryGetProperty("resultPresentation", out _));
    }

    private static void AssertNames(JsonElement element, params string[] expected)
    {
        Assert.Equal(
            expected.Order(StringComparer.Ordinal),
            element.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
    }

    private sealed class FixedCultures(params string[] names) : INhAssistantPresentationCultures
    {
        public IReadOnlyList<CultureInfo> GetCultures()
        {
            return names.Select(CultureInfo.GetCultureInfo).ToArray();
        }
    }

    private sealed class FixedResultPresenter(NhAssistantToolResultPresentation? presentation) : INhAssistantToolResultPresenter
    {
        public NhAssistantToolResultPresentation? Presentation { get; } = presentation;

        public int Calls { get; private set; }

        public ValueTask<NhAssistantToolResultPresentation?> PresentResultAsync(
            NhAssistantToolResultPresentationContext context,
            CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(Presentation);
        }
    }

    private sealed class RecordingResultPresenter : INhAssistantToolResultPresenter
    {
        public NhAssistantToolResultPresentationContext? Context { get; private set; }

        public List<string> Cultures { get; } = [];

        public List<string> CurrentCultures { get; } = [];

        public ValueTask<NhAssistantToolResultPresentation?> PresentResultAsync(
            NhAssistantToolResultPresentationContext context,
            CancellationToken cancellationToken)
        {
            Context = context;
            Cultures.Add(context.Culture.Name);
            CurrentCultures.Add(CultureInfo.CurrentUICulture.Name);
            return ValueTask.FromResult<NhAssistantToolResultPresentation?>(
                new NhAssistantToolResultPresentation("Found in " + context.Culture.Name, []));
        }
    }

    private sealed class ThrowingResultPresenter : INhAssistantToolResultPresenter
    {
        public ValueTask<NhAssistantToolResultPresentation?> PresentResultAsync(
            NhAssistantToolResultPresentationContext context,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("secret presenter failure");
        }
    }

    private sealed class HangingResultPresenter : INhAssistantToolResultPresenter
    {
        public async ValueTask<NhAssistantToolResultPresentation?> PresentResultAsync(
            NhAssistantToolResultPresentationContext context,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return null;
        }
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
