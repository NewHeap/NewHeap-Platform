using AutoMapper;
using NewHeap.Platform.Common.Attributes;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Services;
using NSubstitute;
using Xunit;

namespace NewHeap.Platform.Common.Tests;

public class ProjectedCollectionProcessingServiceTest
{
    [Fact]
    public async Task GetProjectedCollectionResultModelAsync_SupportsShortServiceCall()
    {
        ICollectionProcessingService service =
            new CollectionProcessingService(Substitute.For<IMapper>());

        var requestModel = new CollectionRequestModel
        {
            OrderBy =
            [
                new OrderByCollectionRequestModel
                {
                    Key = nameof(OrderViewModel.CalculatedValue)
                }
            ]
        };

        var projection = NhProjection
            .For<Entity, OrderViewModel>()
            .Map(
                destination => destination.CalculatedValue,
                source => source.Value * 2)
            .Build();

        var result = await service.GetProjectedCollectionResultModelAsync(
            requestModel,
            new[] { new Entity { Value = 21 } }.AsQueryable(),
            projection);

        Assert.Equal(42, result.Items.Single().CalculatedValue);
    }

    private sealed class Entity
    {
        public int Value { get; set; }
    }

    private sealed class OrderViewModel
    {
        [Orderable]
        public int CalculatedValue { get; set; }
    }
}
