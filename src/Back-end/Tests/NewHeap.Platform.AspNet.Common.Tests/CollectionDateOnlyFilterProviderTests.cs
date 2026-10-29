using System.ComponentModel;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.Common.Attributes;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Services;
using NewHeap.Platform.Mapping;
using NewHeap.Platform.Tests.Relational;
using NSubstitute;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

/// <summary>
/// A <see cref="DateOnly"/> or <see cref="TimeOnly"/> member filters as the date or time it holds, on both
/// relational providers: equality and range operators, a nullable member, and a value that arrives as a
/// string or as the JSON string a request body binds to. The collection contract describes such a member
/// as <see cref="NhCollectionFieldType.Date"/> or <see cref="NhCollectionFieldType.Time"/>.
/// </summary>
public sealed class CollectionDateOnlyFilterProviderTests
{
    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task Date_and_time_members_filter_as_dates_and_times(RelationalTestProvider provider)
    {
        var database = await ProviderTestDatabase.CreateAsync(provider);
        var optionsBuilder = new DbContextOptionsBuilder<VisitDbContext>();
        database.UseNewHeapProvider(optionsBuilder);
        await using var context = new VisitDbContext(optionsBuilder.Options);
        await context.Database.EnsureCreatedAsync();
        context.Visits.AddRange(
            new Visit { Name = "early", Day = new DateOnly(2026, 10, 31), StartsAt = new TimeOnly(9, 30) },
            new Visit { Name = "on-the-day", Day = new DateOnly(2026, 11, 1), StartsAt = new TimeOnly(14, 0), FollowUp = new DateOnly(2026, 12, 1) },
            new Visit { Name = "later", Day = new DateOnly(2026, 11, 15), StartsAt = new TimeOnly(11, 45, 30) });
        await context.SaveChangesAsync();
        var service = new CollectionProcessingService(Substitute.For<IMapper>());

        (await NamesAsync(service, context, Filter("day", "==", "2026-11-01"))).Should().Equal("on-the-day");
        (await NamesAsync(service, context, Filter("day", ">=", "2026-11-01"))).Should().Equal("on-the-day", "later");
        (await NamesAsync(service, context, Filter("day", "<", Json("2026-11-01")))).Should().Equal("early");
        (await NamesAsync(service, context, Filter("startsAt", "<", "12:00"))).Should().Equal("early", "later");
        (await NamesAsync(service, context, Filter("startsAt", "==", Json("11:45:30")))).Should().Equal("later");
        (await NamesAsync(service, context, Filter("followUp", "==", "2026-12-01"))).Should().Equal("on-the-day");

        var contract = NhCollectionContractMetadata.Describe(typeof(Visit));
        contract.FilterFields.Single(field => field.Name == "day").FieldType.Should().Be(NhCollectionFieldType.Date);
        contract.FilterFields.Single(field => field.Name == "followUp").FieldType.Should().Be(NhCollectionFieldType.Date);
        contract.FilterFields.Single(field => field.Name == "startsAt").FieldType.Should().Be(NhCollectionFieldType.Time);
    }

    private static async Task<string[]> NamesAsync(CollectionProcessingService service, VisitDbContext context, FilterCollectionRequestModel filter)
    {
        var result = await service.GetCollectionResultModelAsync<Visit, Visit>(
            new CollectionRequestModel { Page = 1, ItemsPerPage = 20, Filter = [filter] },
            context.Visits,
            resultQueryableFunc: null,
            asNoTracking: true,
            cancellationToken: default,
            (visit => (object)visit.Id, ListSortDirection.Ascending));
        return result.Items.Select(visit => visit.Name).ToArray();
    }

    private static FilterCollectionRequestModel Filter(string key, string @operator, object value)
    {
        return new FilterCollectionRequestModel { Key = key, Operator = @operator, Value = value };
    }

    private static JsonElement Json(string value)
    {
        return JsonSerializer.SerializeToElement(value);
    }

    private sealed class VisitDbContext(DbContextOptions<VisitDbContext> options) : DbContext(options)
    {
        public DbSet<Visit> Visits => Set<Visit>();
    }

    private sealed class Visit
    {
        public int Id { get; set; }

        public required string Name { get; set; }

        [Filterable]
        public DateOnly Day { get; set; }

        [Filterable]
        public TimeOnly StartsAt { get; set; }

        [Filterable]
        public DateOnly? FollowUp { get; set; }
    }
}
