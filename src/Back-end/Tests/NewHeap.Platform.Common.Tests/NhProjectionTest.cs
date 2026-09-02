using NewHeap.Platform.Mapping;
using NewHeap.Platform.Common.Attributes;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Services;
using NSubstitute;
using Xunit;

namespace NewHeap.Platform.Common.Tests;

public class NhProjectionTest
{
    [Fact]
    public void Build_MapsMatchingPropertiesAndExplicitMembers()
    {
        var projection = NhProjection
            .For<Order, Order2ViewModel>()
            .Map(
                destination => destination.LineCount,
                source => source.Lines.Count)
            .Build()
            .Compile();

        var id = Guid.NewGuid();
        var result = projection(new Order
        {
            Id = id,
            Number = "NH-42",
            Lines = [new OrderLine(), new OrderLine()]
        });

        Assert.Equal(id, result.Id);
        Assert.Equal("NH-42", result.Number);
        Assert.Equal(2, result.LineCount);
    }

    [Fact]
    public void Build_ExplicitMemberOverridesConventionMapping()
    {
        var projection = NhProjection
            .For<Order, Order2ViewModel>()
            .Map(
                destination => destination.Number,
                source => source.Number + "-projected")
            .Build()
            .Compile();

        var result = projection(new Order { Number = "NH-42" });

        Assert.Equal("NH-42-projected", result.Number);
    }

    [Fact]
    public void Build_IgnoreExcludesConventionMapping()
    {
        var projection = NhProjection
            .For<Order, Order2ViewModel>()
            .Ignore(destination => destination.Number)
            .Build()
            .Compile();

        var result = projection(new Order { Number = "NH-42" });

        Assert.Null(result.Number);
    }

    [Fact]
    public async Task ProjectedCollectionResult_CanOrderByExplicitProjectedMember()
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
                    Key = nameof(Order2ViewModel.LineCount),
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
            .For<Order, Order2ViewModel>()
            .Map(
                destination => destination.LineCount,
                source => source.Lines.Count)
            .Build();

        var result = await service.GetProjectedCollectionResultModelAsync(
            requestModel,
            orders,
            projection,
            resultQueryableFunc: null,
            asNoTracking: false,
            cancellationToken: default);

        Assert.Equal(["three", "two", "one"], result.Items.Select(item => item.Number));
        Assert.Equal([3, 2, 1], result.Items.Select(item => item.LineCount));
    }

    private sealed class Order
    {
        public Guid Id { get; set; }
        public string? Number { get; set; }
        public List<OrderLine> Lines { get; set; } = [];
    }

    private sealed class OrderLine
    {
    }

    private class OrderViewModel
    {
        public Guid Id { get; set; }
        public string? Number { get; set; }
    }

    private sealed class Order2ViewModel : OrderViewModel
    {
        [Orderable]
        public int LineCount { get; set; }
    }
}
