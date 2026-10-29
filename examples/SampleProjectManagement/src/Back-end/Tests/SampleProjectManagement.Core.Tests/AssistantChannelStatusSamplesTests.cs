using System.Reflection;
using Microsoft.AspNetCore.Http;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Chat.AspNet;
using SampleProjectManagement.Api.Services;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// SPM-381: the messaging app answers a refusal with the HTTP status the portal's endpoints give its
/// code, through the platform's <see cref="NhAssistantHttpStatuses"/>, after the channel's own codes.
/// <c>AssistantMessagingChannelSamplesTests</c> and <c>AssistantChannelFileSamplesTests</c> run the
/// statuses over HTTP.
/// </summary>
public sealed class AssistantChannelStatusSamplesTests
{
    [Theory]
    [InlineData(SampleAssistantMessagingChannel.PersonUnavailableCode, StatusCodes.Status403Forbidden)]
    [InlineData(SampleAssistantMessagingChannel.DeliveryInvalidCode, StatusCodes.Status400BadRequest)]
    // Platform codes the channel once answered 400 for, because its own table did not list them.
    [InlineData(NhAssistantErrorCodes.Disabled, StatusCodes.Status404NotFound)]
    [InlineData(NhAssistantErrorCodes.FileForbidden, StatusCodes.Status403Forbidden)]
    [InlineData(NhAssistantErrorCodes.AgentInstructionsUnavailable, StatusCodes.Status409Conflict)]
    [InlineData(NhAssistantErrorCodes.ProposalCheckFailed, StatusCodes.Status503ServiceUnavailable)]
    public void SPM_381_a_refusal_answers_the_status_of_its_code(string code, int status)
    {
        Assert.Equal(status, SampleAssistantMessagingChannel.StatusOf(code));
    }

    [Fact]
    public void SPM_381_every_platform_code_answers_the_status_the_portal_gives_it()
    {
        var codes = typeof(NhAssistantErrorCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToArray();

        Assert.NotEmpty(codes);
        Assert.All(codes, code => Assert.Equal(NhAssistantHttpStatuses.Of(code), SampleAssistantMessagingChannel.StatusOf(code)));
    }
}
