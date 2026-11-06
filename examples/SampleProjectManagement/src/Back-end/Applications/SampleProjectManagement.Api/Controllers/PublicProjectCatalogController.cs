using NewHeap.Platform.Mapping;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using NewHeap.Platform.AspNet.Common.Controllers;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Models;
using SampleProjectManagement.Core.Models.View;
using SampleProjectManagement.Core.Services;

namespace SampleProjectManagement.Api.Controllers;

[ApiController]
[Route("public/projects")]
public sealed class PublicProjectCatalogController : PublicNhBaseController
{
    private readonly ProjectCollectionSampleService _collectionService;

    public PublicProjectCatalogController(
        IMapper mapper,
        ILogger<PublicProjectCatalogController> logger,
        IConfiguration configuration,
        IStringLocalizer<PublicProjectCatalogController> localizer,
        IHttpCollectionProcessingService collectionProcessingService,
        ProjectCollectionSampleService collectionService)
        : base(mapper, logger, configuration, localizer, collectionProcessingService)
    {
        _collectionService = collectionService;
    }

    [HttpGet]
    [AllowAnonymous]
    [EndpointSummary("Get the public project catalog")]
    [EndpointDescription("Returns a paged lightweight catalog containing only active or completed projects, ordered by name. `search` matches the project key and name case-insensitively; filter and ordering input is not offered to anonymous callers.")]
    [ProducesResponseType<SimpleCollectionResultModel<ProjectShortViewModel>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(
        [FromQuery] CollectionRequestModel requestModel,
        CancellationToken cancellationToken = default)
    {
        // The service owns the catalog's query policy, search and paging; the controller maps HTTP.
        var result = await _collectionService.GetPublicCatalogAsync(requestModel, cancellationToken);

        return Ok(result);
    }
}
