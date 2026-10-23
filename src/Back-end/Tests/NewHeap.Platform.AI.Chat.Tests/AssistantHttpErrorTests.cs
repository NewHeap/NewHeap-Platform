using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// The one mapping of assistant failure codes to HTTP statuses that every assistant endpoint uses,
/// and the error body that carries the code, its localization key and the field errors of a result.
/// </summary>
public sealed class AssistantHttpErrorTests
{
    [Fact]
    public void Every_assistant_code_has_exactly_one_status()
    {
        var codes = PublicCodes(typeof(NhAssistantErrorCodes))
            .Concat(PublicCodes(typeof(NhAssistantAdminErrorCodes)))
            .Append(NhAssistantTriggerReasons.RateLimited)
            .Append(NhAssistantRunStore.OrphanedRunCode)
            .ToArray();

        Assert.NotEmpty(codes);
        Assert.All(codes, code => Assert.True(NhAssistantHttpErrors.IsClassified(code), $"{code} has no HTTP status."));
    }

    [Theory]
    [InlineData(NhAssistantErrorCodes.AgentDoesNotWorkOn, StatusCodes.Status409Conflict)]
    [InlineData(NhAssistantErrorCodes.SubjectSearchUnavailable, StatusCodes.Status404NotFound)]
    [InlineData(NhAssistantErrorCodes.SubjectNotFound, StatusCodes.Status404NotFound)]
    [InlineData(NhAssistantErrorCodes.SubjectInvalid, StatusCodes.Status400BadRequest)]
    [InlineData(NhAssistantErrorCodes.RunFilterInvalid, StatusCodes.Status400BadRequest)]
    [InlineData(NhAssistantErrorCodes.ArtifactNotFound, StatusCodes.Status404NotFound)]
    [InlineData(NhAssistantErrorCodes.ContextUnavailable, StatusCodes.Status403Forbidden)]
    [InlineData(NhAssistantErrorCodes.NoAccountableOwner, StatusCodes.Status409Conflict)]
    [InlineData(NhAssistantErrorCodes.AgentStopped, StatusCodes.Status409Conflict)]
    [InlineData(NhAssistantErrorCodes.WebhookPayloadTooLarge, StatusCodes.Status413PayloadTooLarge)]
    [InlineData(NhAssistantErrorCodes.FileUploadInvalid, StatusCodes.Status400BadRequest)]
    [InlineData(NhAssistantErrorCodes.FileTypeNotAccepted, StatusCodes.Status415UnsupportedMediaType)]
    [InlineData(NhAssistantErrorCodes.FileUploadRefused, StatusCodes.Status422UnprocessableEntity)]
    [InlineData(NhAssistantErrorCodes.FileScanRefused, StatusCodes.Status422UnprocessableEntity)]
    [InlineData(NhAssistantErrorCodes.FileUploadFailed, StatusCodes.Status503ServiceUnavailable)]
    [InlineData(NhAssistantTriggerReasons.RateLimited, StatusCodes.Status409Conflict)]
    [InlineData(NhAssistantRunStore.OrphanedRunCode, StatusCodes.Status503ServiceUnavailable)]
    [InlineData(NhAssistantAdminErrorCodes.McpConnectionNotSupported, StatusCodes.Status409Conflict)]
    [InlineData(NhAssistantAdminErrorCodes.McpSecretUnreadable, StatusCodes.Status409Conflict)]
    [InlineData(NhAssistantAdminErrorCodes.McpSecretUnavailable, StatusCodes.Status503ServiceUnavailable)]
    [InlineData(NhAssistantAdminErrorCodes.McpHeadlessNotAllowed, StatusCodes.Status409Conflict)]
    [InlineData(NhAssistantAdminErrorCodes.McpPkceUnsupported, StatusCodes.Status400BadRequest)]
    [InlineData(NhAssistantAdminErrorCodes.McpClientRegistrationRequired, StatusCodes.Status400BadRequest)]
    [InlineData(NhAssistantAdminErrorCodes.McpAuthorizationExpired, StatusCodes.Status400BadRequest)]
    [InlineData(NhAssistantAdminErrorCodes.McpCredentialMismatch, StatusCodes.Status403Forbidden)]
    [InlineData(NhAssistantAdminErrorCodes.McpTooManyAuthorizations, StatusCodes.Status429TooManyRequests)]
    [InlineData(NhAssistantAdminErrorCodes.McpTokenRequestFailed, StatusCodes.Status502BadGateway)]
    [InlineData(NhAssistantErrorCodes.FileReaderFailed, StatusCodes.Status502BadGateway)]
    [InlineData(NhAssistantErrorCodes.ConsultationInCode, StatusCodes.Status409Conflict)]
    [InlineData(NhAssistantErrorCodes.ConsultationsNotAllowed, StatusCodes.Status409Conflict)]
    [InlineData(NhAssistantErrorCodes.ConsultationNotFound, StatusCodes.Status404NotFound)]
    [InlineData(NhAssistantErrorCodes.ConsultToolNotSelected, StatusCodes.Status400BadRequest)]
    [InlineData(NhAssistantAdminErrorCodes.MetadataEntryNotFound, StatusCodes.Status404NotFound)]
    [InlineData(NhAssistantAdminErrorCodes.MetadataReadOnly, StatusCodes.Status409Conflict)]
    [InlineData("application-own-code", StatusCodes.Status400BadRequest)]
    public void A_code_answers_its_one_status(string code, int status)
    {
        Assert.Equal(status, NhAssistantHttpErrors.StatusOf(code));
    }

    [Fact]
    public void The_trigger_rate_limit_is_an_assistant_code()
    {
        Assert.Equal("assistant-trigger-rate-limited", NhAssistantTriggerReasons.RateLimited);
    }

    [Fact]
    public async Task A_failure_carries_its_code_its_key_and_its_field_errors_whatever_its_code()
    {
        var filter = NhAssistantRunHistory.ReadQuery(null, "Not A Type", "x", "bogus", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-1), 0, 99);

        var (status, body) = await ExecuteAsync(NhAssistantHttpErrors.Failure(filter));

        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Equal(NhAssistantErrorCodes.RunFilterInvalid, body.GetProperty("code").GetString());
        Assert.Equal("nh-assistant.errors." + NhAssistantErrorCodes.RunFilterInvalid, body.GetProperty("messageKey").GetString());
        var errors = body.GetProperty("errors");
        Assert.Equal(
            ["itemsPerPage", "page", "status", "subjectType", "to"],
            errors.EnumerateObject().Select(field => field.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(NhAssistantFieldErrors.Invalid, errors.GetProperty("status")[0].GetString());
    }

    [Fact]
    public async Task A_failure_without_a_code_but_with_field_errors_is_a_validation_failure()
    {
        var result = new TaskResult();
        result.AddError(NhAssistantValidationErrors.FieldPrefix + "query", NhAssistantFieldErrors.Invalid);

        var (status, body) = await ExecuteAsync(NhAssistantHttpErrors.Failure(result));

        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Equal(NhAssistantAdminErrorCodes.ValidationFailed, body.GetProperty("code").GetString());
        Assert.Equal(NhAssistantFieldErrors.Invalid, body.GetProperty("errors").GetProperty("query")[0].GetString());
    }

    [Fact]
    public void A_run_filter_without_errors_reads_its_page_and_the_period_in_utc()
    {
        var from = new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.FromHours(2));

        var query = NhAssistantRunHistory.ReadQuery("agent", "project", "p-1", NhAssistantRunStatuses.Completed, from, from.AddDays(1), null, null);

        Assert.True(query.Success);
        Assert.Equal((1, 20), (query.Data!.Page, query.Data.ItemsPerPage));
        Assert.Equal(TimeSpan.Zero, query.Data.Filter.From!.Value.Offset);
        Assert.Equal(["subjectType"], NhAssistantResults.FieldErrorsOf(
            NhAssistantRunHistory.ReadQuery(null, null, "p-1", null, null, null, null, null)).Keys.ToArray());
        Assert.Equal(
            NhAssistantFieldErrors.Required,
            NhAssistantResults.FieldErrorsOf(NhAssistantRunHistory.ReadQuery(null, null, "p-1", null, null, null, null, null))["subjectType"][0]);
    }

    private static IEnumerable<string> PublicCodes(Type type)
    {
        return type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!);
    }

    private static async Task<(int Status, JsonElement Body)> ExecuteAsync(IResult result)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider()
        };
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        return (context.Response.StatusCode, document.RootElement.Clone());
    }
}
