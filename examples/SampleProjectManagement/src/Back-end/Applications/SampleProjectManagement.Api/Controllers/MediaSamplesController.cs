using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using SampleProjectManagement.Api.Services;

namespace SampleProjectManagement.Api.Controllers;

[ApiController]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
[Route("media-samples")]
public sealed class MediaSamplesController : ControllerBase
{
    private readonly ProjectMediaSampleService _mediaSamples;

    public MediaSamplesController(ProjectMediaSampleService mediaSamples)
    {
        _mediaSamples = mediaSamples;
    }

    // The authorization decisions name the divisions and paths of every division, so only people
    // with the application-wide project permission, who may read all of them, see the diagnostics.
    [HttpGet("diagnostics")]
    [Authorize(Policy = "app.project.view")]
    [EndpointSummary("Get media diagnostics")]
    [EndpointDescription("Returns the media event, thumbnail and authorization diagnostics collected by the sample media modules. Requires the application permission app.project.view, because the authorization decisions cover every division.")]
    [ProducesResponseType<ProjectMediaDiagnostics>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<ProjectMediaDiagnostics> GetDiagnostics() =>
        Ok(_mediaSamples.GetDiagnostics());

    [HttpGet("download")]
    [EndpointSummary("Download a sample media file")]
    [EndpointDescription("Downloads a file from the configured media storage for the signed-in user. The media authorization module requires the path to lie in the active division and the user to hold the project view permission there; otherwise the response is 401. The stored file name chooses the content type from the media library's fixed list (application/octet-stream for any other name); raster images are served inline, every other file as an attachment, always with X-Content-Type-Options: nosniff.")]
    [Produces("application/octet-stream")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Download([FromQuery] string? path, [FromQuery] string fileName)
    {
        try
        {
            var download = await _mediaSamples.DownloadAsync(path, fileName);
            if (download is null)
            {
                return NotFound();
            }

            Response.Headers.XContentTypeOptions = "nosniff";
            if (!download.Inline)
            {
                return File(download.Stream, download.ContentType, download.FileName);
            }

            var disposition = new ContentDispositionHeaderValue("inline");
            disposition.SetHttpFileName(download.FileName);
            Response.Headers.ContentDisposition = disposition.ToString();
            return File(download.Stream, download.ContentType);
        }
        catch (UnauthorizedAccessException)
        {
            return Unauthorized();
        }
    }
}
