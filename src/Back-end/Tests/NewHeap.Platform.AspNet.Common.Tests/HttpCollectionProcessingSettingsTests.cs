using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Models.Options;
using NewHeap.Platform.Mapping;
using NSubstitute;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

public sealed class HttpCollectionProcessingSettingsTests
{
    [Fact]
    public void InvalidRequestPageSizeFallsBackToConfiguredDefault()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.QueryString = new QueryString("?itemsPerPage=51");
        var service = new HttpCollectionProcessingService(
            Substitute.For<IMapper>(),
            new HttpContextAccessor { HttpContext = httpContext },
            Substitute.For<ILogger<HttpCollectionProcessingService>>(),
            Options.Create(new NewHeapCommonSettings
            {
                CollectionProcessingDefaultItemsPerPage = 7,
                CollectionProcessingDefaultMaxItemsPerPage = 50
            }));

        var request = service.GetCollectionRequestModel();

        Assert.Equal(1, request.Page);
        Assert.Equal(7, request.ItemsPerPage);
    }
}
