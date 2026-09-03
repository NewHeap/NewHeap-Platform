using Microsoft.Extensions.Options;
using NewHeap.Platform.Common.Models.Options;
using NewHeap.Platform.Common.Services;
using NewHeap.Platform.Mapping;
using NSubstitute;
using Xunit;

namespace NewHeap.Platform.Common.Tests;

public sealed class CollectionProcessingSettingsTests
{
    [Fact]
    public void PagingDefaultsRetainTheirCompatibleValues()
    {
        var service = new CollectionProcessingService(Substitute.For<IMapper>());

        Assert.Equal(20, service.GetDefaultItemsPerPage());
        Assert.Equal(1000, service.GetDefaultMaxItemsPerPage());
    }

    [Fact]
    public void PagingDefaultsComeFromPlatformCommonSettings()
    {
        var service = CreateService(new NewHeapCommonSettings
        {
            CollectionProcessingDefaultItemsPerPage = 25,
            CollectionProcessingDefaultMaxItemsPerPage = 250
        });

        Assert.Equal(25, service.GetDefaultItemsPerPage());
        Assert.Equal(250, service.GetDefaultMaxItemsPerPage());
    }

    [Theory]
    [InlineData(0, 1000)]
    [InlineData(20, 0)]
    [InlineData(1001, 1000)]
    public void InvalidPagingDefaultsAreRejected(
        int defaultItemsPerPage,
        int defaultMaxItemsPerPage)
    {
        var settings = new NewHeapCommonSettings
        {
            CollectionProcessingDefaultItemsPerPage = defaultItemsPerPage,
            CollectionProcessingDefaultMaxItemsPerPage = defaultMaxItemsPerPage
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => CreateService(settings));
    }

    private static CollectionProcessingService CreateService(NewHeapCommonSettings settings)
    {
        return new CollectionProcessingService(
            Substitute.For<IMapper>(),
            Options.Create(settings));
    }
}
