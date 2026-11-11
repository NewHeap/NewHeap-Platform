using System.Text.Json;
using Microsoft.Extensions.AI;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runtime;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// The plan an agent keeps with <c>update_plan</c> (<c>run.KeepPlan()</c>) without a database: the bounds
/// and checks of an update, advancing, the update limit of a turn, reading a continued turn's plan back,
/// and the pinned JSON of <c>plan.updated</c> and of the message part.
/// </summary>
public sealed class AssistantPlanContractTests
{
    [Fact]
    public void An_update_sets_bounded_plain_steps_with_at_most_one_in_progress()
    {
        var (plan, refusal) = NhAssistantPlans.Apply(
            Arguments(new
            {
                steps = new object[]
                {
                    new { text = "  Find the\nopen   projects​ ", status = "completed" },
                    new { text = "Check <b>dates</b> of [the plan](https://example.test)", status = "in_progress" },
                    new { text = "Write the summary", status = "PENDING" }
                }
            }),
            null);

        Assert.Null(refusal);
        Assert.Equal(
            [
                new NhAssistantPlanStep("Find the open projects", NhAssistantPlanStepStatuses.Completed),
                // Markup stays text: clients never render it.
                new NhAssistantPlanStep("Check <b>dates</b> of [the plan](https://example.test)", NhAssistantPlanStepStatuses.InProgress),
                new NhAssistantPlanStep("Write the summary", NhAssistantPlanStepStatuses.Pending)
            ],
            plan);

        var tooMany = Enumerable.Range(1, NhAssistantPlanLimits.MaxSteps + 1).Select(index => new { text = "Step " + index, status = "pending" }).ToArray();
        var invalid = new object[]
        {
            new { steps = tooMany },
            new { steps = Array.Empty<object>() },
            new { steps = new[] { new { text = new string('a', NhAssistantPlanLimits.MaxStepLength + 1), status = "pending" } } },
            new { steps = new[] { new { text = " \n\t ", status = "pending" } } },
            new { steps = new[] { new { text = "Read", status = "done" } } },
            new { steps = new[] { new { text = "Read", status = "in_progress" }, new { text = "Write", status = "in-progress" } } },
            new { steps = new[] { new { text = "Read", status = "pending" } }, advance = true },
            new { advance = false },
            new { advance = "yes" },
            new { }
        };
        foreach (var arguments in invalid)
        {
            var (none, why) = NhAssistantPlans.Apply(Arguments(arguments), null);
            Assert.Null(none);
            Assert.StartsWith("The plan was not changed", why, StringComparison.Ordinal);
        }

        // Exactly the bound fits.
        var fits = Enumerable.Range(1, NhAssistantPlanLimits.MaxSteps).Select(index => new { text = new string('s', NhAssistantPlanLimits.MaxStepLength), status = "pending" }).ToArray();
        Assert.Equal(NhAssistantPlanLimits.MaxSteps, NhAssistantPlans.Apply(Arguments(new { steps = fits }), null).Plan!.Count);
    }

    [Fact]
    public void Advancing_completes_the_step_in_progress_and_starts_the_next_pending_one()
    {
        IReadOnlyList<NhAssistantPlanStep> plan =
        [
            new("Read", NhAssistantPlanStepStatuses.InProgress),
            new("Check", NhAssistantPlanStepStatuses.Pending),
            new("Write", NhAssistantPlanStepStatuses.Pending)
        ];
        var advance = Arguments(new { advance = true });

        plan = NhAssistantPlans.Apply(advance, plan).Plan!;
        Assert.Equal([NhAssistantPlanStepStatuses.Completed, NhAssistantPlanStepStatuses.InProgress, NhAssistantPlanStepStatuses.Pending], plan.Select(step => step.Status));
        plan = NhAssistantPlans.Apply(advance, plan).Plan!;
        plan = NhAssistantPlans.Apply(advance, plan).Plan!;
        Assert.All(plan, step => Assert.Equal(NhAssistantPlanStepStatuses.Completed, step.Status));

        // A finished plan, or none, cannot advance.
        Assert.Contains("every step is completed", NhAssistantPlans.Apply(advance, plan).Refusal, StringComparison.Ordinal);
        Assert.Contains("no plan yet", NhAssistantPlans.Apply(advance, null).Refusal, StringComparison.Ordinal);

        // Without a step in progress, advancing starts the first pending one.
        IReadOnlyList<NhAssistantPlanStep> waiting = [new("Read", NhAssistantPlanStepStatuses.Completed), new("Write", NhAssistantPlanStepStatuses.Pending)];
        Assert.Equal(NhAssistantPlanStepStatuses.InProgress, NhAssistantPlans.Apply(advance, waiting).Plan![1].Status);
    }

    [Fact]
    public void A_turn_updates_its_plan_at_most_the_limit_and_the_model_reads_why_not()
    {
        var steps = Arguments(new { steps = new[] { new { text = "Read", status = "in_progress" } } });

        var (last, none) = NhAssistantPlans.Update(steps, null, NhAssistantPlanLimits.MaxUpdatesPerTurn - 1);
        var (over, limit) = NhAssistantPlans.Update(steps, last, NhAssistantPlanLimits.MaxUpdatesPerTurn);
        var (invalid, refused) = NhAssistantPlans.Update(Arguments(new { advance = true }), null, 0);

        Assert.NotNull(last);
        Assert.Null(none);
        Assert.Null(over);
        Assert.Equal(NhAssistantErrorCodes.PlanUpdateLimitReached, limit!.Value.GetProperty("code").GetString());
        Assert.False(limit.Value.GetProperty("success").GetBoolean());
        Assert.Null(invalid);
        Assert.Equal(NhAssistantErrorCodes.PlanInvalid, refused!.Value.GetProperty("code").GetString());
    }

    [Fact]
    public void A_continued_turn_reads_its_plan_and_updates_back_from_its_calls()
    {
        IReadOnlyList<NhAssistantPlanStep> plan = [new("Read", NhAssistantPlanStepStatuses.Completed), new("Write", NhAssistantPlanStepStatuses.InProgress)];
        var result = NhAssistantPlans.Updated(plan);

        // The model reads the plan in the words of the function's schema.
        Assert.Equal(NhAssistantPlans.UpdatedCode, result.GetProperty("code").GetString());
        Assert.Equal((1, 2), (result.GetProperty("completed").GetInt32(), result.GetProperty("total").GetInt32()));
        Assert.Equal("in_progress", result.GetProperty("steps")[1].GetProperty("status").GetString());

        var calls = new[]
        {
            new NhAssistantTurnToolResult(Guid.NewGuid(), "projects.search", "{}", "{}"),
            new NhAssistantTurnToolResult(Guid.NewGuid(), PlanToolId, "{}", NhAssistantContent.SerializeResult(NhAssistantPlans.Updated([new("Read", NhAssistantPlanStepStatuses.InProgress)]))),
            new NhAssistantTurnToolResult(Guid.NewGuid(), PlanToolId, "{}", NhAssistantContent.SerializeResult(result))
        };
        var (read, updates) = NhAssistantPlans.OfTurn(calls);

        Assert.Equal(2, updates);
        Assert.Equal(plan, read);
        Assert.Equal((null, 0), NhAssistantPlans.OfTurn([calls[0]]));
    }

    [Fact]
    public void Plan_updated_and_the_plan_part_keep_their_json_names()
    {
        var turnId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var messageId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var updatedAt = new DateTimeOffset(2031, 5, 6, 7, 8, 9, TimeSpan.Zero);
        var view = new NhAssistantPlanPartView(
            [new NhAssistantPlanStep("Read", NhAssistantPlanStepStatuses.Completed), new NhAssistantPlanStep("Write", NhAssistantPlanStepStatuses.InProgress)],
            updatedAt);

        var (name, data) = NhAssistantServerSentEventsResult.Serialize(new NhAssistantPlanUpdatedEvent(turnId, messageId, view));
        var part = JsonSerializer.Serialize(NhAssistantDtoMapper.ToDto(view), NhAssistantJsonSerializerContext.Default.NhAssistantMessagePartDto);

        Assert.Equal("plan.updated", name);
        Assert.Equal(
            """{"turnId":"11111111-1111-1111-1111-111111111111","messageId":"22222222-2222-2222-2222-222222222222","plan":{"steps":[{"text":"Read","status":"completed"},{"text":"Write","status":"in-progress"}],"updatedAt":"2031-05-06T07:08:09+00:00"}}""",
            data);
        Assert.Equal(
            """{"type":"plan","steps":[{"text":"Read","status":"completed"},{"text":"Write","status":"in-progress"}],"updatedAt":"2031-05-06T07:08:09+00:00"}""",
            part);
    }

    [Fact]
    public void A_stored_plan_part_reads_back_and_a_broken_one_is_left_out()
    {
        var runId = Guid.NewGuid();
        var part = NhAssistantPlans.PartOf([new NhAssistantPlanStep("Read", NhAssistantPlanStepStatuses.Pending)], runId, DateTimeOffset.UnixEpoch);
        var stored = NhAssistantContent.SerializeParts([new NhAssistantStoredPart(NhAssistantStoredPart.TextType, Text: "Hi"), part]);

        Assert.Equal("Read", NhAssistantPlans.LatestOfRun([stored], runId)!.Steps.Single().Text);
        Assert.Null(NhAssistantPlans.LatestOfRun([stored], Guid.NewGuid()));
        Assert.Null(NhAssistantPlans.ViewOf(part with { Steps = [new NhAssistantPlanStep("Read", "done")] }));
        Assert.Null(NhAssistantPlans.ViewOf(part with { Steps = [] }));
        Assert.True(NhAssistantPlans.IsPlanCall(PlanToolId));
        Assert.False(new NhAssistantAgentRunOptions("any-agent").KeepsPlan);
    }

    private static readonly string PlanToolId = Runs.NhAssistantMemoryFunction.PlanDescriptor.Id;

    private static AIFunctionArguments Arguments(object value)
    {
        var arguments = new AIFunctionArguments();
        foreach (var property in JsonSerializer.SerializeToElement(value).EnumerateObject())
        {
            arguments[property.Name] = property.Value.Clone();
        }
        return arguments;
    }
}
