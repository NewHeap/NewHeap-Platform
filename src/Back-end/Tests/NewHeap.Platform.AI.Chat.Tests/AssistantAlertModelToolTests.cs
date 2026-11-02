using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.Alerts;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NewHeap.Platform.Common.Models;
using NSubstitute;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// The model tool of alerts (FB87-9, lane AM): an agent's model raises an alert only of a kind that allows
/// it in code, that its agent lists and whose switch for the agent an administrator turned on, first in
/// shadow, through a template with typed parameters that the platform checks before anything is stored.
/// The model never chooses the subject or the recipients, a shadow switch reaches nobody, and the alert is
/// marked as the model's in the store, the audit and the list; on both relational providers.
/// </summary>
public sealed class AssistantAlertModelToolTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string AgentId = "project-assistant";
    private const string ModelKind = "document-deadline";
    private const string RulesKind = "task-due-today";
    private const string UnlistedModelKind = "task-escalated";
    private const string Administrator = "admin-1";
    private const string Responsible = "owner-1";
    private const string ToolName = NhAssistantAlertModelTool.ExportName;

    private static readonly DateTimeOffset Start = new(2026, 11, 2, 7, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_switch_needs_the_kind_s_code_and_the_agent_s_list_goes_live_only_from_shadow_and_keeps_each_version(
        AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider, new NhAiScriptedChatClient());

        // The first key is code: a kind without a template, or one the agent does not list, cannot be switched.
        var withoutTemplate = await SwitchAsync(host, RulesKind, NhAssistantAlertModelModes.Shadow, 0);
        var unlisted = await SwitchAsync(host, UnlistedModelKind, NhAssistantAlertModelModes.Shadow, 0);
        var unknown = await SwitchAsync(host, "no-such-kind", NhAssistantAlertModelModes.Shadow, 0);
        var noAgent = await SwitchAsync(host, ModelKind, NhAssistantAlertModelModes.Shadow, 0, agentId: "nobody");
        // Shadow first: off never goes live directly.
        var liveFirst = await SwitchAsync(host, ModelKind, NhAssistantAlertModelModes.Live, 0);
        var invalid = await SwitchAsync(host, ModelKind, "always", 0);

        Assert.Equal(NhAssistantAdminErrorCodes.AlertModelNotAllowed, CodeOf(withoutTemplate));
        Assert.Equal(NhAssistantAdminErrorCodes.AlertModelNotAllowed, CodeOf(unlisted));
        Assert.Equal(NhAssistantAlertCodes.KindUnknown, CodeOf(unknown));
        Assert.Equal(NhAssistantErrorCodes.AgentNotFound, CodeOf(noAgent));
        Assert.Equal(NhAssistantAdminErrorCodes.AlertModelShadowFirst, CodeOf(liveFirst));
        Assert.Contains(invalid.GetResultItems(), item => item.Name == "field:mode");

        var shadow = await SwitchAsync(host, ModelKind, NhAssistantAlertModelModes.Shadow, 0, reason: "  See what it raises  ");
        var stale = await SwitchAsync(host, ModelKind, NhAssistantAlertModelModes.Live, 0);
        var live = await SwitchAsync(host, ModelKind, NhAssistantAlertModelModes.Live, 1);
        var again = await SwitchAsync(host, ModelKind, NhAssistantAlertModelModes.Live, 2);

        Assert.True(shadow.Success);
        Assert.Equal((NhAssistantAlertModelModes.Shadow, 1, "See what it raises"), (shadow.Data!.Mode, shadow.Data.Version, shadow.Data.Reason));
        Assert.Equal(NhAssistantAdminErrorCodes.VersionConflict, CodeOf(stale));
        Assert.Equal((NhAssistantAlertModelModes.Live, 2, Administrator), (live.Data!.Mode, live.Data.Version, live.Data.ChangedBy));
        // The same mode again writes no version.
        Assert.Equal(2, again.Data!.Version);

        await using var scope = host.Services.CreateAsyncScope();
        var switches = scope.ServiceProvider.GetRequiredService<INhAssistantAlertModelSwitches>();
        var listed = (await switches.GetAsync(AgentId))!;
        var versions = await switches.GetVersionsAsync(AgentId, ModelKind);

        // Only the listed kind with a template lists, with what the model fills.
        Assert.True(listed.Applies);
        var kind = Assert.Single(listed.Kinds);
        Assert.Equal((ModelKind, NhAssistantAlertModelModes.Live, false, true), (kind.KindId, kind.Mode, kind.ContractChanged, kind.KindEnabled));
        Assert.Equal(["deadline", "daysLeft", "document", "severity"], kind.Parameters.Select(parameter => parameter.Name));
        Assert.Equal(["date", "whole-number", "text", "choice"], kind.Parameters.Select(parameter => parameter.Type));
        Assert.Equal([true, false, true, false], kind.Parameters.Select(parameter => parameter.Key));
        Assert.Equal([2, 1], versions.Select(version => version.Version));
        Assert.Equal([NhAssistantAlertModelModes.Live, NhAssistantAlertModelModes.Shadow], versions.Select(version => version.Mode));

        // Content-free audit: who, which agent and kind, the mode and the version; never the reason.
        var changes = host.Business.Events.Where(evt => evt.Kind == NhAssistantAuditEventKind.AdminAlertModelSwitchChanged).ToArray();
        Assert.Equal(
            [(NhAssistantAlertModelModes.Shadow, 1), (NhAssistantAlertModelModes.Live, 2)],
            changes.Select(evt => (evt.ResultCode, evt.AlertModelSwitchVersion!.Value)));
        Assert.All(changes, evt => Assert.Equal((Administrator, AgentId, ModelKind), (evt.ActorId, evt.ObjectId, evt.AlertKind)));
        Assert.DoesNotContain(host.Business.Events, evt => evt.ResultCode?.Contains("raises", StringComparison.Ordinal) == true);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task The_tool_is_offered_only_in_a_run_on_one_subject_for_listed_kinds_that_allow_a_model_and_are_switched_on(
        AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithText("Nothing to warn about.")
            .RespondWithText("Nothing to warn about.");
        await using var host = await CreateHostAsync(provider, model);

        // Off by default: the agent lists a kind that allows a model, and still the model gets no tool.
        await RunAsync(host, TaskSubject(Guid.NewGuid()), "Look at the deadlines.");
        Assert.DoesNotContain(ToolName, Offered(model, 0));

        // In shadow the tool is offered, with only the listed kind that allows a model: not the rules'
        // kind without a template, nor the kind with a template that the agent does not list.
        Assert.True((await SwitchAsync(host, ModelKind, NhAssistantAlertModelModes.Shadow, 0)).Success);
        await RunAsync(host, TaskSubject(Guid.NewGuid()), "Look at the deadlines.");
        var tool = Assert.Single(model.Requests[1].Options!.Tools!.OfType<AIFunction>(), item => item.Name == ToolName);
        var kinds = tool.JsonSchema.GetProperty("properties").GetProperty("kind").GetProperty("enum").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Equal([ModelKind], kinds);
        // Neither the subject nor the recipients are the model's to give.
        var arguments = tool.JsonSchema.GetProperty("properties").EnumerateObject().Select(property => property.Name).ToArray();
        Assert.Equal(["kind", "parameters"], arguments);
        Assert.False(tool.JsonSchema.GetProperty("additionalProperties").GetBoolean());

        // A run on the whole application gets no tool, whatever the switch says.
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var options = services.GetRequiredService<NhAssistantRegistrationState>().AgentRuns[AgentId];
        var modelTool = services.GetRequiredService<NhAssistantAlertModelTool>();
        var onSubject = await modelTool.OfferedKindsAsync(options, (await DescribeAsync(services, TaskSubject(Guid.NewGuid())))!, CancellationToken.None);
        var onApplication = await modelTool.OfferedKindsAsync(
            options,
            new NhAiSubjectInfo(NhAiSystemSubjects.Global, NhAiSubjectInfo.CreateScopeKey(null, null)),
            CancellationToken.None);
        Assert.Equal(NhAssistantAlertModelModes.Shadow, onSubject[ModelKind]);
        Assert.Empty(onApplication);

        // Switched off again, the next run offers nothing.
        Assert.True((await SwitchAsync(host, ModelKind, NhAssistantAlertModelModes.Off, 1)).Success);
        Assert.Empty(await modelTool.OfferedKindsAsync(options, (await DescribeAsync(services, TaskSubject(Guid.NewGuid())))!, CancellationToken.None));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task In_shadow_the_model_records_a_shadow_alert_that_reaches_nobody_and_reads_as_recorded(
        AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ToolName, new { kind = ModelKind, parameters = ValidParameters() }, "call-shadow")
            .RespondWithText("I warned about the budget plan.");
        await using var host = await CreateHostAsync(provider, model);
        Assert.True((await SwitchAsync(host, ModelKind, NhAssistantAlertModelModes.Shadow, 0)).Success);
        var task = TaskSubject(Guid.NewGuid());

        var turn = await RunAsync(host, task, "Look at the deadlines.");

        Assert.Equal(NhAssistantTurnStatuses.Completed, turn.Outcome);
        var result = ResultOf(model, "call-shadow");
        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Equal(NhAssistantAlertCodes.Raised, result.GetProperty("outcome").GetString());
        // The result says nothing about whom it reaches, nor that it is a shadow alert.
        Assert.False(result.TryGetProperty("shadow", out _));
        Assert.False(result.TryGetProperty("recipients", out _));
        Assert.False(result.TryGetProperty("recipientCount", out _));

        var alert = Assert.Single(await AlertsAboutAsync(host, task));
        Assert.True(alert.Shadow);
        Assert.True(alert.RaisedByModel);
        Assert.Empty(alert.Deliveries);
        Assert.StartsWith("model:", alert.Key, StringComparison.Ordinal);
        Assert.Equal(AgentId, alert.AgentId);
        var parameters = NhAssistantAlertTexts.ParametersOf(alert.ParametersJson);
        Assert.Equal(("2026-11-06", "4", "Budget plan", "soon"), (parameters["deadline"], parameters["daysLeft"], parameters["document"], parameters["severity"]));
        var raised = Assert.Single(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AgentAlertRaised);
        Assert.Equal((true, true, 0), (raised.Shadow!.Value, raised.AlertRaisedByModel!.Value, raised.AlertRecipientCount!.Value));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Live_the_model_s_alert_reaches_only_the_kind_s_checked_recipients_and_the_list_says_the_model_raised_it(
        AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            // The model tries to choose the recipients and another subject: refused before anything is stored.
            .RespondWithFunctionCall(ToolName, new { kind = ModelKind, parameters = ValidParameters(), recipients = new[] { "outsider-1" } }, "call-recipients")
            .RespondWithFunctionCall(ToolName, new { kind = ModelKind, parameters = ValidParameters(), subjectId = Guid.NewGuid().ToString("D") }, "call-subject")
            .RespondWithFunctionCall(ToolName, new { kind = ModelKind, parameters = ValidParameters() }, "call-live")
            // The same key values again update the same open alert.
            .RespondWithFunctionCall(ToolName, new { kind = ModelKind, parameters = ValidParameters() with { daysLeft = 3 } }, "call-again")
            .RespondWithText("I warned the owner.");
        await using var host = await CreateHostAsync(provider, model);
        await SwitchAsync(host, ModelKind, NhAssistantAlertModelModes.Shadow, 0);
        Assert.True((await SwitchAsync(host, ModelKind, NhAssistantAlertModelModes.Live, 1)).Success);
        var task = TaskSubject(Guid.NewGuid());

        await RunAsync(host, task, "Look at the deadlines.");

        Assert.Equal(
            (NhAssistantAlertCodes.ParameterInvalid, "recipients", NhAssistantAlertParameterErrors.Unknown),
            Refusal(ResultOf(model, "call-recipients")));
        Assert.Equal(
            (NhAssistantAlertCodes.ParameterInvalid, "subjectId", NhAssistantAlertParameterErrors.Unknown),
            Refusal(ResultOf(model, "call-subject")));
        Assert.Equal(NhAssistantAlertCodes.Raised, ResultOf(model, "call-live").GetProperty("outcome").GetString());
        Assert.Equal(NhAssistantAlertCodes.Updated, ResultOf(model, "call-again").GetProperty("outcome").GetString());

        // One live alert about the run's own subject, for the kind's recipient only, marked as the model's.
        var alert = Assert.Single(await AlertsAboutAsync(host, task));
        Assert.Equal((false, true, 2), (alert.Shadow, alert.RaisedByModel, alert.Revision));
        var delivery = Assert.Single(alert.Deliveries);
        Assert.Equal(Responsible, delivery.RecipientActorId);
        Assert.DoesNotContain(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AgentAlertRecipientDropped);
        Assert.All(
            host.Business.Events.Where(evt => evt.Kind == NhAssistantAuditEventKind.AgentAlertRaised),
            evt => Assert.True(evt.AlertRaisedByModel));

        // The person's list shows that a model raised it, with the kind's title from the checked values.
        await using var scope = host.Services.CreateAsyncScope();
        var list = scope.ServiceProvider.GetRequiredService<NhAssistantAlertList>();
        var query = NhAssistantAlertQuery.Read(null, null, task.Type, task.Id, null).Data!;
        var listed = await list.ListAsync(new NhAiInvocationContext(Responsible, "alerts", new Dictionary<string, string>()), query, 1, 20, false, CancellationToken.None);
        var item = Assert.Single(listed.Items);
        Assert.True(item.RaisedByModel);
        Assert.Equal("Budget plan is due on 2026-11-06", item.Title);
        // The values the model gave stay out of the logs.
        Assert.DoesNotContain(host.Logs.Messages, message => message.Contains("Budget plan", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Links_markup_control_characters_values_outside_the_template_and_kinds_not_offered_are_refused_before_anything_is_stored(
        AssistantTestProvider provider)
    {
        var refusals = new (string CallId, object Arguments, string Code, string? Parameter, string? Reason)[]
        {
            ("call-url", new { kind = ModelKind, parameters = ValidParameters() with { document = "Read https://evil.example/fix" } }, NhAssistantAlertCodes.ParameterInvalid, "document", NhAssistantAlertParameterErrors.Link),
            ("call-domain", new { kind = ModelKind, parameters = ValidParameters() with { document = "Mail evil.example now" } }, NhAssistantAlertCodes.ParameterInvalid, "document", NhAssistantAlertParameterErrors.Link),
            ("call-markup", new { kind = ModelKind, parameters = ValidParameters() with { document = "<b>Budget</b>" } }, NhAssistantAlertCodes.ParameterInvalid, "document", NhAssistantAlertParameterErrors.Markup),
            ("call-control", new { kind = ModelKind, parameters = ValidParameters() with { document = "Budget\u202Enalp" } }, NhAssistantAlertCodes.ParameterInvalid, "document", NhAssistantAlertParameterErrors.ControlCharacter),
            ("call-long", new { kind = ModelKind, parameters = ValidParameters() with { document = new string('a', 81) } }, NhAssistantAlertCodes.ParameterInvalid, "document", NhAssistantAlertParameterErrors.TooLong),
            ("call-bounds", new { kind = ModelKind, parameters = ValidParameters() with { daysLeft = 31 } }, NhAssistantAlertCodes.ParameterInvalid, "daysLeft", NhAssistantAlertParameterErrors.OutOfRange),
            ("call-choice", new { kind = ModelKind, parameters = ValidParameters() with { severity = "panic" } }, NhAssistantAlertCodes.ParameterInvalid, "severity", NhAssistantAlertParameterErrors.NotAChoice),
            ("call-date", new { kind = ModelKind, parameters = ValidParameters() with { deadline = "next friday" } }, NhAssistantAlertCodes.ParameterInvalid, "deadline", NhAssistantAlertParameterErrors.OutOfRange),
            ("call-unknown", new { kind = ModelKind, parameters = new { deadline = "2026-11-06", daysLeft = 4, document = "Budget plan", severity = "soon", link = "x" } }, NhAssistantAlertCodes.ParameterInvalid, "link", NhAssistantAlertParameterErrors.Unknown),
            ("call-missing", new { kind = ModelKind, parameters = new { deadline = "2026-11-06", daysLeft = 4, severity = "soon" } }, NhAssistantAlertCodes.ParameterInvalid, "document", NhAssistantAlertParameterErrors.Missing),
            ("call-rules-kind", new { kind = RulesKind, parameters = new { task = "Write the report" } }, NhAssistantAlertCodes.NotOfferedToModel, null, null),
            ("call-unlisted", new { kind = UnlistedModelKind, parameters = new { } }, NhAssistantAlertCodes.NotOfferedToModel, null, null)
        };
        var model = new NhAiScriptedChatClient();
        foreach (var refusal in refusals)
        {
            model.RespondWithFunctionCall(ToolName, refusal.Arguments, refusal.CallId);
        }
        // With a limit of one alert per run, the first valid alert is raised, raising it again is no new
        // alert, and the next one is refused.
        model
            .RespondWithFunctionCall(ToolName, new { kind = ModelKind, parameters = ValidParameters() }, "call-first")
            .RespondWithFunctionCall(ToolName, new { kind = ModelKind, parameters = ValidParameters() }, "call-repeat")
            .RespondWithFunctionCall(ToolName, new { kind = ModelKind, parameters = ValidParameters() with { document = "Annual report" } }, "call-over-limit")
            .RespondWithText("Done.");
        await using var host = await CreateHostAsync(provider, model, limits => limits.MaxAlertsPerRun = 1);
        await SwitchAsync(host, ModelKind, NhAssistantAlertModelModes.Shadow, 0);
        await SwitchAsync(host, ModelKind, NhAssistantAlertModelModes.Live, 1);
        var task = TaskSubject(Guid.NewGuid());

        await RunAsync(host, task, "Look at the deadlines.");

        foreach (var refusal in refusals)
        {
            var result = ResultOf(model, refusal.CallId);
            Assert.False(result.GetProperty("success").GetBoolean());
            Assert.Equal(refusal.Code, result.GetProperty("code").GetString());
            if (refusal.Parameter is not null)
            {
                Assert.Equal((refusal.Parameter, refusal.Reason), (result.GetProperty("parameter").GetString(), result.GetProperty("reason").GetString()));
            }
            // The refused value never comes back.
            Assert.DoesNotContain("evil", result.GetRawText(), StringComparison.Ordinal);
        }
        Assert.True(ResultOf(model, "call-first").GetProperty("success").GetBoolean());
        Assert.Equal("unchanged", ResultOf(model, "call-repeat").GetProperty("outcome").GetString());
        Assert.Equal(NhAssistantAlertCodes.LimitReached, ResultOf(model, "call-over-limit").GetProperty("code").GetString());

        // Only the one valid alert was stored.
        var stored = Assert.Single(await AlertsAboutAsync(host, task));
        Assert.Equal("Budget plan", NhAssistantAlertTexts.ParametersOf(stored.ParametersJson)["document"]);
        Assert.Single(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AgentAlertRaised);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Only_an_administrator_reads_and_switches_the_model_tool_over_http(AssistantTestProvider provider)
    {
        // The web host runs no agent work, so its chat agent lists no kinds and cannot be switched; the
        // switches themselves are proven on the agent-runs host above and in SPM-392.
        await using var app = await AssistantWebApplication.StartAsync(
            database,
            new NhAiScriptedChatClient(),
            configure: ConfigureServices,
            assistantBuilder: assistant => assistant
                .AddSubjectType<AlertTestSubjectResolver>()
                .AddAlertKind(ModelKind, kind => kind
                    .DisplayName("tests.alerts.document-deadline")
                    .Title((_, parameters) => $"{parameters["document"]} is due")
                    .RaisedByModel(template => template
                        .Describe("A document is due soon.")
                        .Text("document", "The document's name.", 80))),
            provider: provider);
        using var user = app.CreateClient();
        using var admin = app.CreateClient(admin: true);
        var list = $"/api/assistant/admin/agents/{AgentId}/alert-model";
        var route = $"/api/assistant/admin/agents/{AgentId}/alert-kinds/{ModelKind}/model";

        // A person without the administration policy can neither read nor switch.
        using var forbiddenRead = await user.GetAsync(list);
        using var forbiddenSwitch = await user.PutAsync(route, ServerSentEventReader.Json(new { mode = "shadow", expectedVersion = 0 }));
        using var forbiddenVersions = await user.GetAsync(route + "/versions");
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, forbiddenRead.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, forbiddenSwitch.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, forbiddenVersions.StatusCode);
        Assert.Equal("assistant-forbidden", (await ReadJsonAsync(forbiddenSwitch)).GetProperty("code").GetString());

        var listed = await ReadJsonAsync(await admin.GetAsync(list));
        using var notListed = await admin.PutAsync(route, ServerSentEventReader.Json(new { mode = "shadow", expectedVersion = 0 }));
        using var unknownKind = await admin.PutAsync(
            $"/api/assistant/admin/agents/{AgentId}/alert-kinds/no-such-kind/model",
            ServerSentEventReader.Json(new { mode = "shadow", expectedVersion = 0 }));
        using var invalid = await admin.PutAsync(route, ServerSentEventReader.Json(new { mode = "always", expectedVersion = 0 }));
        using var unknownAgent = await admin.GetAsync("/api/assistant/admin/agents/nobody/alert-model");
        var versions = await ReadJsonAsync(await admin.GetAsync(route + "/versions"));

        Assert.False(listed.GetProperty("applies").GetBoolean());
        Assert.Empty(listed.GetProperty("kinds").EnumerateArray());
        Assert.Equal(System.Net.HttpStatusCode.Conflict, notListed.StatusCode);
        Assert.Equal(NhAssistantAdminErrorCodes.AlertModelNotAllowed, (await ReadJsonAsync(notListed)).GetProperty("code").GetString());
        Assert.Equal(System.Net.HttpStatusCode.NotFound, unknownKind.StatusCode);
        Assert.Equal(NhAssistantAlertCodes.KindUnknown, (await ReadJsonAsync(unknownKind)).GetProperty("code").GetString());
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, unknownAgent.StatusCode);
        Assert.Empty(versions.EnumerateArray());
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private sealed record DocumentParameters(string deadline, int daysLeft, string document, string severity);

    private static DocumentParameters ValidParameters()
    {
        return new DocumentParameters("2026-11-06", 4, "Budget plan", "soon");
    }

    private static (string?, string?, string?) Refusal(JsonElement result)
    {
        Assert.False(result.GetProperty("success").GetBoolean());
        return (result.GetProperty("code").GetString(), result.GetProperty("parameter").GetString(), result.GetProperty("reason").GetString());
    }

    private static IReadOnlyList<string> Offered(NhAiScriptedChatClient model, int request)
    {
        return (model.Requests[request].Options?.Tools ?? []).Select(tool => tool.Name).ToArray();
    }

    private static JsonElement ResultOf(NhAiScriptedChatClient model, string callId)
    {
        var result = model.Requests
            .SelectMany(request => request.Messages)
            .SelectMany(message => message.Contents.OfType<FunctionResultContent>())
            .Last(content => content.CallId == callId)
            .Result;
        return JsonSerializer.SerializeToElement(result);
    }

    private static string? CodeOf(TaskResult result)
    {
        return result.GetResultItems().Select(item => item.Name).FirstOrDefault(name => name?.StartsWith("assistant-", StringComparison.Ordinal) == true);
    }

    private static NhAiSubjectRef TaskSubject(Guid taskId)
    {
        return new NhAiSubjectRef("task", taskId.ToString("D"));
    }

    private static async Task<NhAiSubjectInfo?> DescribeAsync(IServiceProvider services, NhAiSubjectRef subject)
    {
        return await services.GetRequiredService<NhAssistantSubjectAccessService>().DescribeAsync(subject, CancellationToken.None);
    }

    private static async Task<IReadOnlyList<AssistantAlert>> AlertsAboutAsync(AssistantTestHost host, NhAiSubjectRef subject)
    {
        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        return await context.Alerts
            .AsNoTracking()
            .Include(alert => alert.Deliveries)
            .Where(alert => alert.SubjectType == subject.Type && alert.SubjectId == subject.Id)
            .ToListAsync();
    }

    private static async Task<TaskResult<NhAssistantAlertModelSwitchDto>> SwitchAsync(
        AssistantTestHost host,
        string kindId,
        string mode,
        int expectedVersion,
        string? reason = null,
        string agentId = AgentId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INhAssistantAlertModelSwitches>().SetAsync(
            agentId,
            kindId,
            new NhAssistantAlertModelRequest(mode, expectedVersion, reason),
            Administrator);
    }

    /// <summary>
    /// A run of the agent, without an initiator, in its live thread of <paramref name="subject"/>.
    /// </summary>
    private static async Task<NhAssistantAgentTurnResult> RunAsync(AssistantTestHost host, NhAiSubjectRef subject, string message)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var info = (await DescribeAsync(services, subject))!;
        var agent = (await services.GetRequiredService<NhAssistantAgentCatalog>().FindAsync(AgentId, includeDisabled: true, CancellationToken.None))!;
        var thread = (await services.GetRequiredService<NhAssistantThreadService>().GetOrCreateAsync(agent, info, CancellationToken.None)).Data!;
        var run = new AssistantRun
        {
            Id = Guid.NewGuid(),
            ConversationId = thread.ConversationId,
            TriggerId = "deadline-review",
            AccountableOwnerId = thread.AccountableOwnerId,
            Status = NhAssistantRunStatuses.Queued,
            AgentVersion = agent.Definition.Version,
            CreatedAt = DateTimeOffset.UtcNow,
            ConcurrencyStamp = Guid.NewGuid()
        };
        await services.GetRequiredService<NhAssistantRunStore>().AddRunAsync(run, CancellationToken.None);
        var options = services.GetRequiredService<NhAssistantRegistrationState>().AgentRuns[AgentId];
        var agentTurn = new NhAssistantAgentTurnContext(
            thread,
            info,
            options,
            run.Id,
            run.TriggerId,
            null,
            thread.AccountableOwnerId,
            IsRequestTurn: false);
        return await services.GetRequiredService<INhAssistantTurnRunner>().RunAgentTurnAsync(
            new NhAssistantAgentTurnRequest(agentTurn, [new NhAssistantAgentTurnInput(Guid.NewGuid(), null, run.TriggerId, message)]),
            CancellationToken.None);
    }

    private async Task<AssistantTestHost> CreateHostAsync(
        AssistantTestProvider provider,
        NhAiScriptedChatClient model,
        Action<NhAssistantLimits>? limits = null)
    {
        return await AssistantTestHost.CreateAsync(
            database,
            provider,
            model,
            assistantLimits =>
            {
                // Each refusal is one call of the turn.
                assistantLimits.MaxToolCallsPerTurn = 20;
                limits?.Invoke(assistantLimits);
            },
            configure: services =>
            {
                ConfigureServices(services);
                services.AddSingleton(Substitute.For<INhBackgroundOperationService>());
            },
            assistantBuilder: ConfigureAssistant);
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<TimeProvider>(new AssistantTestClock(Start));
        services.AddSingleton<AlertTestSubjects>();
        services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, AlertPrincipalFactory>());
    }

    /// <summary>
    /// A kind the model may raise with four typed parameters, a kind of the rules without a template that
    /// the agent lists too, and a kind with a template that the agent does not list.
    /// </summary>
    private static void ConfigureAssistant(NhAssistantBuilder assistant)
    {
        assistant
            .AddSubjectType<AlertTestSubjectResolver>()
            .AddAlertKind(ModelKind, kind => kind
                .DisplayName("tests.alerts.document-deadline")
                .Urgency(NhAssistantAlertUrgency.Warning)
                .Title((_, parameters) => $"{parameters["document"]} is due on {parameters["deadline"]}")
                .Text((_, parameters) => $"{parameters["daysLeft"]} days left ({parameters["severity"]}).")
                .Recipients(NhAssistantAlertRecipients.SubjectResponsible)
                .RaisedByModel(template => template
                    .Describe("A document of the task has its deadline within a month and its work is not ready.")
                    .Date("deadline", "The document's deadline.")
                    .WholeNumber("daysLeft", "The days left until the deadline.", 0, 30)
                    .Text("document", "The document's name, without its file extension.", 80)
                    .Choice("severity", "How soon it needs attention.", "soon", "very-soon")
                    .Key("deadline", "document")))
            .AddAlertKind(RulesKind, kind => kind
                .DisplayName("tests.alerts.task-due-today")
                .Title((_, parameters) => $"{parameters["task"]} is due today"))
            .AddAlertKind(UnlistedModelKind, kind => kind
                .DisplayName("tests.alerts.task-escalated")
                .Title((_, _) => "A task was escalated")
                .RaisedByModel(template => template.Describe("A task was escalated.")))
            .ConfigureAgent(AgentId, agent => agent.Alerts(ModelKind, RulesKind));
    }
}

/// <summary>
/// The template of an alert kind for an agent's model (FB87-9): what its code declares and how the
/// platform checks the values a model gives, without a database.
/// </summary>
public sealed class AssistantAlertModelTemplateTests
{
    [Theory]
    [InlineData("Budget plan")]
    [InlineData("Q3 review, part 2")]
    [InlineData("v1.2 release notes")]
    [InlineData("Plan e.g. for Monday")]
    [InlineData("R&D roadmap")]
    [InlineData("Rapport voor 3 dagen")]
    [InlineData("  Trimmed  ")]
    public void Plain_text_is_accepted(string text)
    {
        var (value, error) = NhAssistantAlertModelText.Check(text, 80);

        Assert.Null(error);
        Assert.Equal(text.Trim(), value);
    }

    [Theory]
    [InlineData("See https://ex.org", NhAssistantAlertParameterErrors.Link)]
    [InlineData("ftp://files", NhAssistantAlertParameterErrors.Link)]
    [InlineData("www.example", NhAssistantAlertParameterErrors.Link)]
    [InlineData("mailto:someone", NhAssistantAlertParameterErrors.Link)]
    [InlineData("javascript:alert(1)", NhAssistantAlertParameterErrors.Link)]
    [InlineData("Go to example.com", NhAssistantAlertParameterErrors.Link)]
    [InlineData("someone@example.org", NhAssistantAlertParameterErrors.Link)]
    [InlineData("report.pdf", NhAssistantAlertParameterErrors.Link)]
    [InlineData("10.0.0.1 login", NhAssistantAlertParameterErrors.Link)]
    [InlineData("example\uFF0Ecom", NhAssistantAlertParameterErrors.Link)]
    [InlineData("example\u3002com", NhAssistantAlertParameterErrors.Link)]
    [InlineData("<script>", NhAssistantAlertParameterErrors.Markup)]
    [InlineData("[click](here)", NhAssistantAlertParameterErrors.Markup)]
    [InlineData("{0}", NhAssistantAlertParameterErrors.Markup)]
    [InlineData("`code`", NhAssistantAlertParameterErrors.Markup)]
    [InlineData("Tom &amp; Jerry", NhAssistantAlertParameterErrors.Markup)]
    [InlineData("\uFF1Cb\uFF1E", NhAssistantAlertParameterErrors.Markup)]
    [InlineData("Line\nbreak", NhAssistantAlertParameterErrors.ControlCharacter)]
    [InlineData("Tab\there", NhAssistantAlertParameterErrors.ControlCharacter)]
    [InlineData("Right\u202Eto left", NhAssistantAlertParameterErrors.ControlCharacter)]
    [InlineData("Zero\u200Bwidth", NhAssistantAlertParameterErrors.ControlCharacter)]
    [InlineData("Para\u2029graph", NhAssistantAlertParameterErrors.ControlCharacter)]
    [InlineData("   ", NhAssistantAlertParameterErrors.Empty)]
    [InlineData("This text is longer than twenty", NhAssistantAlertParameterErrors.TooLong)]
    public void Text_with_a_link_markup_or_a_control_character_is_refused(string text, string reason)
    {
        var (value, error) = NhAssistantAlertModelText.Check(text, 20);

        Assert.Null(value);
        Assert.Equal(reason, error);
    }

    [Fact]
    public void A_broken_surrogate_pair_is_a_control_character()
    {
        var broken = "Broken " + (char)0xD800 + " pair";

        Assert.Equal(NhAssistantAlertParameterErrors.ControlCharacter, NhAssistantAlertModelText.Check(broken, 80).Error);
    }

    [Fact]
    public void A_template_checks_types_choices_and_bounds_and_derives_the_key_from_the_key_values_only()
    {
        var template = Build(template => template
            .Describe("A document is due soon.")
            .Date("deadline", "The deadline.")
            .WholeNumber("daysLeft", "Days left.", 0, 30)
            .Text("document", "The document.", 40)
            .Choice("severity", "How soon.", "soon", "very-soon")
            .Key("document", "deadline"));
        var subject = new NhAiSubjectRef("task", "t-1");

        var accepted = template.Check(Json(new { deadline = "2026-11-06", daysLeft = 3.0, document = "Plan", severity = "soon" }));
        var asText = template.Check(Json(new { deadline = "2026-11-06", daysLeft = "3", document = "Plan", severity = "soon" }));
        var otherDays = template.Check(Json(new { deadline = "2026-11-06", daysLeft = 9, document = "Plan", severity = "very-soon" }));
        var otherDocument = template.Check(Json(new { deadline = "2026-11-06", daysLeft = 3, document = "Budget", severity = "soon" }));

        Assert.True(accepted.Success);
        Assert.Equal("3", accepted.Values!["daysLeft"]);
        Assert.Equal(accepted.Values, asText.Values);
        // The key follows the key parameters, in the order of the declaration, and nothing else.
        Assert.Equal(["deadline", "document"], template.KeyParameters);
        var key = template.KeyOf("document-deadline", subject, accepted.Values!);
        Assert.StartsWith("model:", key, StringComparison.Ordinal);
        Assert.True(key.Length <= NhAssistantAlertLimits.MaxKeyLength);
        Assert.Equal(key, template.KeyOf("document-deadline", subject, otherDays.Values!));
        Assert.NotEqual(key, template.KeyOf("document-deadline", subject, otherDocument.Values!));
        Assert.NotEqual(key, template.KeyOf("document-deadline", new NhAiSubjectRef("task", "t-2"), accepted.Values!));

        Assert.Equal(("daysLeft", NhAssistantAlertParameterErrors.OutOfRange), Refusal(template.Check(Json(new { deadline = "2026-11-06", daysLeft = 3.5, document = "Plan", severity = "soon" }))));
        Assert.Equal(("daysLeft", NhAssistantAlertParameterErrors.OutOfRange), Refusal(template.Check(Json(new { deadline = "2026-11-06", daysLeft = -1, document = "Plan", severity = "soon" }))));
        Assert.Equal(("daysLeft", NhAssistantAlertParameterErrors.WrongType), Refusal(template.Check(Json(new { deadline = "2026-11-06", daysLeft = true, document = "Plan", severity = "soon" }))));
        Assert.Equal(("severity", NhAssistantAlertParameterErrors.NotAChoice), Refusal(template.Check(Json(new { deadline = "2026-11-06", daysLeft = 3, document = "Plan", severity = "Soon" }))));
        Assert.Equal(("deadline", NhAssistantAlertParameterErrors.OutOfRange), Refusal(template.Check(Json(new { deadline = "2026-02-30", daysLeft = 3, document = "Plan", severity = "soon" }))));
        Assert.Equal(("document", NhAssistantAlertParameterErrors.WrongType), Refusal(template.Check(Json(new { deadline = "2026-11-06", daysLeft = 3, document = 7, severity = "soon" }))));
        Assert.Equal(("parameters", NhAssistantAlertParameterErrors.WrongType), Refusal(template.Check(Json(new[] { "Plan" }))));
        Assert.Equal(("deadline", NhAssistantAlertParameterErrors.Missing), Refusal(template.Check(null)));
    }

    [Fact]
    public void A_kind_s_code_must_describe_its_template_and_name_only_declared_parameters_in_the_key()
    {
        Assert.Throws<InvalidOperationException>(() => Build(template => template.Text("document", "The document.", 40)));
        Assert.Throws<InvalidOperationException>(() => Build(template => template.Describe("Due soon.").Text("document", "The document.", 40).Key("deadline")));
        Assert.Throws<ArgumentOutOfRangeException>(() => Build(template => template.Describe("Due soon.").Text("document", "The document.", NhAssistantAlertLimits.MaxModelTextLength + 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Build(template => template.Describe("Due soon.").WholeNumber("days", "Days.", 5, 1)));
        Assert.Throws<ArgumentException>(() => Build(template => template.Describe("Due soon.").Choice("severity", "How soon.")));
        Assert.Throws<ArgumentException>(() => Build(template => template.Describe("Due soon.").Choice("severity", "How soon.", "soon", "soon")));
        Assert.Throws<ArgumentException>(() => Build(template => template.Describe("Due soon.").Date("deadline", "Deadline.").Date("deadline", "Again.")));
        Assert.Throws<ArgumentException>(() => Build(template => template.Describe("Line\nbreak")));

        // A kind without the call is never raised by a model; a changed template has another hash.
        var plain = new NhAssistantAlertKindBuilder("plain").DisplayName("tests.plain").Title((_, _) => "Plain").Build();
        Assert.Null(plain.ModelTemplate);
        var first = Build(template => template.Describe("Due soon.").Text("document", "The document.", 40));
        var wider = Build(template => template.Describe("Due soon.").Text("document", "The document.", 60));
        Assert.NotEqual(first.ContractHash, wider.ContractHash);
    }

    private static NhAssistantAlertModelTemplate Build(Action<NhAssistantAlertModelTemplateBuilder> configure)
    {
        return new NhAssistantAlertKindBuilder("document-deadline")
            .DisplayName("tests.alerts.document-deadline")
            .Title((_, _) => "Due")
            .RaisedByModel(configure)
            .Build()
            .ModelTemplate!;
    }

    private static JsonElement Json(object value)
    {
        return JsonSerializer.SerializeToElement(value);
    }

    private static (string?, string?) Refusal(NhAssistantAlertModelValues values)
    {
        Assert.False(values.Success);
        return (values.Parameter, values.Error);
    }
}
