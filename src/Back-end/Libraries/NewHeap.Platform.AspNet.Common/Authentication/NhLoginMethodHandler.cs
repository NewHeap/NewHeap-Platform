using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using HttpMethod = NewHeap.Platform.AspNet.Common.Builders.HttpMethod;

namespace NewHeap.Platform.AspNet.Common.Authentication;

public class NhLoginMethodHandler : BaseNhAuthenticationEndpoint
{
    public NhLoginMethodHandler(
        IHttpContextAccessor httpContextAccessor,
        IServiceProvider serviceProvider,
        AuthenticationConfiguration configuration
    ) : base(httpContextAccessor, "authentication/method", serviceProvider, configuration)
    {
        Method = HttpMethod.Post;
        Handler = ExecuteAsync;
    }

    [ApiExplorerSettings(GroupName = "Authentication")]
    [Tags("Authentication")]
    [EndpointName("Get the correct authentication flow for an account")]
    [Produces<Results<Ok<string>, BadRequest>>]
    private async Task<IResult> ExecuteAsync([FromServices] AuthenticationMethodPickerService picker,
        [FromBody] Request request)
    {
        var result = await picker.GetAuthMethod(request.Username ?? "");
        if (result.Success)
        {
            return Ok(result.Data!);
        }

        return BadRequest(result);
    }

    private class Request
    {
        public string? Username { get; set; }
    }
}