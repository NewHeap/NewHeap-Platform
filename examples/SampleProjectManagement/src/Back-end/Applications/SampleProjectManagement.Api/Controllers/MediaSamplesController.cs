using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SampleProjectManagement.Api.Services;

namespace SampleProjectManagement.Api.Controllers;

[ApiController]
[AllowAnonymous]
[Route("media-samples")]
public sealed class MediaSamplesController : ControllerBase
{
    private readonly ProjectMediaSampleService _mediaSamples;

    public MediaSamplesController(ProjectMediaSampleService mediaSamples)
    {
        _mediaSamples = mediaSamples;
    }

    [HttpGet("diagnostics")]
    [EndpointSummary("Get media diagnostics")]
    [EndpointDescription("Returns the media event, thumbnail and authorization diagnostics collected by the sample media modules.")]
    [ProducesResponseType<ProjectMediaDiagnostics>(StatusCodes.Status200OK)]
    public ActionResult<ProjectMediaDiagnostics> GetDiagnostics() =>
        Ok(_mediaSamples.GetDiagnostics());

    [HttpGet("download")]
    [EndpointSummary("Download a sample media file")]
    [EndpointDescription("Downloads a file from the configured media storage after applying path and authorization checks.")]
    [Produces("application/octet-stream")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Download([FromQuery] string? path, [FromQuery] string fileName)
    {
        try
        {
            var download = await _mediaSamples.DownloadAsync(path, fileName);
            return download is null
                ? NotFound()
                : File(download.Stream, download.ContentType, download.FileName);
        }
        catch (UnauthorizedAccessException)
        {
            return Unauthorized();
        }
    }
}
