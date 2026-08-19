using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SampleProjectManagement.Api.Services;

namespace SampleProjectManagement.Api.Controllers;

[ApiController]
[Route("")]
public class HomeController : ControllerBase
{
    [HttpGet]
    [AllowAnonymous]
    [EndpointSummary("Get sample API information")]
    [EndpointDescription("Returns the sample application name and the relative URL of its Scalar API reference.")]
    [ProducesResponseType<SampleApplicationInfoModel>(StatusCodes.Status200OK)]
    public ActionResult<SampleApplicationInfoModel> Get()
    {
        return Ok(new SampleApplicationInfoModel
        {
            Application = "SampleProjectManagement",
            Scalar = "/scalar"
        });
    }
}
