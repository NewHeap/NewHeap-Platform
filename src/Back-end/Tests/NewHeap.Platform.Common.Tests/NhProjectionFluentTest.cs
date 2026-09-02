using NewHeap.Platform.Mapping;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Services;
using NSubstitute;
using Xunit;

namespace NewHeap.Platform.Common.Tests;

public class NhProjectionFluentTest
{
    [Fact]
    public async Task AnonymousSelect_CanDeclareOrderableMemberFluently()
    {
        ICollectionProcessingService service =
            new CollectionProcessingService(Substitute.For<IMapper>());

        var requestModel = new CollectionRequestModel
        {
            Page = 1,
            ItemsPerPage = 20,
            OrderBy =
            [
                new OrderByCollectionRequestModel
                {
                    Key = "LineCount",
                    Direction = "DESC"
                }
            ]
        };

        var orders = new[]
        {
            new Order { Number = "one", Lines = [new OrderLine()] },
            new Order { Number = "three", Lines = [new OrderLine(), new OrderLine(), new OrderLine()] },
            new Order { Number = "two", Lines = [new OrderLine(), new OrderLine()] }
        }.AsQueryable();

        var projection = NhProjection
            .For<Order>()
            .Select(order => new
            {
                order.Number,
                LineCount = order.Lines.Count
            })
            .IsOrderable(viewModel => viewModel.LineCount);

        var selected = orders.Select(projection).ToArray();
        var result = await service.GetProjectedCollectionResultModelAsync(
            requestModel,
            orders,
            projection,
            asNoTracking: false);

        Assert.Equal([1, 3, 2], selected.Select(item => item.LineCount));
        Assert.Equal(["three", "two", "one"], result.Items.Select(item => item.Number));
        Assert.Equal([3, 2, 1], result.Items.Select(item => item.LineCount));
    }

    [Fact]
    public async Task DtoProjection_CanDeclareOrderableMemberFluently()
    {
        ICollectionProcessingService service =
            new CollectionProcessingService(Substitute.For<IMapper>());

        var requestModel = new CollectionRequestModel
        {
            Page = 1,
            ItemsPerPage = 20,
            OrderBy =
            [
                new OrderByCollectionRequestModel
                {
                    Key = nameof(OrderProjection.LineCount),
                    Direction = "DESC"
                }
            ]
        };

        var projection = NhProjection
            .For<Order, OrderProjection>()
            .Map(
                destination => destination.LineCount,
                source => source.Lines.Count)
            .IsOrderable(viewModel => viewModel.LineCount)
            .Build();

        var result = await service.GetProjectedCollectionResultModelAsync(
            requestModel,
            new[]
            {
                new Order { Number = "one", Lines = [new OrderLine()] },
                new Order { Number = "two", Lines = [new OrderLine(), new OrderLine()] }
            }.AsQueryable(),
            projection,
            asNoTracking: false);

        Assert.Equal(["two", "one"], result.Items.Select(item => item.Number));
    }

    private sealed class Order
    {
        public string? Number { get; set; }
        public List<OrderLine> Lines { get; set; } = [];
    }

    private sealed class OrderLine
    {
    }

    private sealed class OrderProjection
    {
        public string? Number { get; set; }
        public int LineCount { get; set; }
    }
}
