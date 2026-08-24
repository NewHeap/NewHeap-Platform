using NewHeap.Platform.Mapping;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using NewHeap.Platform.AspNet.Common.Controllers;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Models;
using SampleProjectManagement.Core.Models.View;
using SampleProjectManagement.Core.Services;
using SampleProjectManagement.DAL.Entities;
using System.ComponentModel;

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
    [EndpointDescription("Returns a filtered and paged lightweight catalog containing only active or completed projects.")]
    [ProducesResponseType<CollectionResultModel<ProjectShortViewModel>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(
        [FromQuery] CollectionRequestModel requestModel,
        CancellationToken cancellationToken = default)
    {
        var result = await GetCollectionResultModel<Project, ProjectShortViewModel>(
            requestModel,
            _collectionService.GetPublicCatalogQuery(),
            cancellationToken,
            (project => project.Name, ListSortDirection.Ascending));

        return Ok(result);
    }
}
