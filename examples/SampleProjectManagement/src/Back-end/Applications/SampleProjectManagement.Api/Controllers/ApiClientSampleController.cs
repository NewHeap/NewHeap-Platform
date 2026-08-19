using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NewHeap.Platform.AspNet.Common.Models.ResponseTypes;
using SampleProjectManagement.Api.Services;

namespace SampleProjectManagement.Api.Controllers;

[ApiController]
[AllowAnonymous]
[Route("samples/api-client")]
public class ApiClientSampleController : ControllerBase
{
    private readonly ISampleProjectManagementApiService _apiService;

    public ApiClientSampleController(ISampleProjectManagementApiService apiService)
    {
        _apiService = apiService;
    }

    [HttpGet]
    [EndpointSummary("Call the sample API through the typed API client")]
    [EndpointDescription("Uses AddNhApiClient to call the sample root endpoint and returns the typed application information contract.")]
    [ProducesResponseType<SampleApplicationInfoModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ModelStateResponseType>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<SampleApplicationInfoModel>> Get(
        CancellationToken cancellationToken = default)
    {
        var result = await _apiService.GetApplicationInfoAsync(cancellationToken);
        if (!result.Success)
        {
            return BadRequest(result.GetResultItems().ToDictionary(
                item => item.Name,
                item => item.ErrorMessages.Select(error => error.ToString()).ToArray()));
        }

        return Ok(result.Data);
    }
}
