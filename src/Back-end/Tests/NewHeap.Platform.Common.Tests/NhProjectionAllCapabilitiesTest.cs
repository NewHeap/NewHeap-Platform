using AutoMapper;
using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Services;
using NSubstitute;
using Xunit;

namespace NewHeap.Platform.Common.Tests;

public class NhProjectionAllCapabilitiesTest
{
    [Fact]
    public async Task AnonymousProjection_SupportsSearchFilterAndOrderFluently()
    {
        ICollectionProcessingService service =
            new CollectionProcessingService(Substitute.For<IMapper>());

        await using var dbContext = new ProjectionDbContext(
            new DbContextOptionsBuilder<ProjectionDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);

        dbContext.Orders.AddRange(
            new Order { Number = "one", Lines = [new OrderLine()] },
            new Order { Number = "three", Lines = [new OrderLine(), new OrderLine(), new OrderLine()] },
            new Order { Number = "two", Lines = [new OrderLine(), new OrderLine()] });

        await dbContext.SaveChangesAsync();

        var orders = dbContext.Orders.AsQueryable();

        var projection = NhProjection
            .For<Order>()
            .Select(order => new
            {
                order.Number,
                LineCount = order.Lines.Count
            })
            .IsSearchable(result => result.Number)
            .IsFilterable(result => result.LineCount)
            .IsOrderable(result => result.LineCount);

        var searchResult = await service.GetProjectedCollectionResultModelAsync(
            new CollectionRequestModel { Search = "tw" },
            orders,
            projection,
            asNoTracking: false);

        var filterResult = await service.GetProjectedCollectionResultModelAsync(
            new CollectionRequestModel
            {
                Filter =
                [
                    new FilterCollectionRequestModel
                    {
                        Key = "LineCount",
                        Operator = ">=",
                        Value = 2
                    }
                ]
            },
            orders,
            projection,
            asNoTracking: false);

        var orderResult = await service.GetProjectedCollectionResultModelAsync(
            new CollectionRequestModel
            {
                OrderBy =
                [
                    new OrderByCollectionRequestModel
                    {
                        Key = "LineCount",
                        Direction = "DESC"
                    }
                ]
            },
            orders,
            projection,
            asNoTracking: false);

        Assert.Equal("two", searchResult.Items.Single().Number);
        Assert.Equal(["three", "two"], filterResult.Items.Select(item => item.Number));
        Assert.Equal(["three", "two", "one"], orderResult.Items.Select(item => item.Number));
    }

    private sealed class Order
    {
        public Guid Id { get; set; }
        public string? Number { get; set; }
        public List<OrderLine> Lines { get; set; } = [];
    }

    private sealed class OrderLine
    {
        public Guid Id { get; set; }
        public Guid OrderId { get; set; }
    }

    private sealed class ProjectionDbContext(
        DbContextOptions<ProjectionDbContext> options) : DbContext(options)
    {
        public DbSet<Order> Orders => Set<Order>();
    }
}
