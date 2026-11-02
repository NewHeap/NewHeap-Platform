using System.ComponentModel;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.Common.Attributes;
using NewHeap.Platform.Common.Exceptions;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Services;
using NewHeap.Platform.Mapping;
using NewHeap.Platform.Tests.Relational;
using NSubstitute;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

/// <summary>
/// A <see cref="DateTime"/> or <see cref="DateTimeOffset"/> member filters by the instant in UTC on both
/// relational providers, whatever zone the host runs in: <c>Z</c>, a positive and a negative offset, a
/// value without a zone and a date alone (both read as UTC), a value as a JSON string and as the
/// <see cref="DateTime"/> of each kind a JSON body binds to, an <c>IN</c> list and a nullable member. A
/// value that is not a date and time, also inside a collection's items, or that another member type
/// cannot hold, fails as an invalid filter, which the base controllers return as <c>400</c>.
/// </summary>
public sealed class CollectionDateTimeFilterProviderTests
{
    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task Date_time_members_filter_by_the_instant_in_utc(RelationalTestProvider provider)
    {
        await using var context = await CreateContextAsync(provider);
        var service = new CollectionProcessingService(Substitute.For<IMapper>());

        foreach (var key in new[] { "at", "atOffset" })
        {
            (await NamesAsync(service, context, Filter(key, ">=", "2026-11-01T09:00:00Z"))).Should().Equal("on-time", "late");
            (await NamesAsync(service, context, Filter(key, ">=", "2026-11-01T10:00:00+01:00"))).Should().Equal("on-time", "late");
            (await NamesAsync(service, context, Filter(key, ">=", "2026-11-01T04:00:00-05:00"))).Should().Equal("on-time", "late");
            (await NamesAsync(service, context, Filter(key, "==", Json("2026-11-01T09:00:00")))).Should().Equal("on-time");
            // A date alone is midnight UTC, so the evening before stays out also on a host east of UTC.
            (await NamesAsync(service, context, Filter(key, ">=", "2026-11-01"))).Should().Equal("early", "on-time", "late");
            (await NamesAsync(service, context, Filter(key, "<", Json("2026-11-01")))).Should().Equal("evening-before");
            (await NamesAsync(service, context, Filter(key, "IN", "2026-11-01T09:00:00Z, 2026-11-01T11:00:00+01:00"))).Should().Equal("on-time", "late");
            (await NamesAsync(service, context, Filter(key, "NOT IN", "2026-11-01T08:00:00Z,2026-10-31T23:30:00"))).Should().Equal("on-time", "late");

            var onTime = new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc);
            (await NamesAsync(service, context, Filter(key, "==", onTime))).Should().Equal("on-time");
            (await NamesAsync(service, context, Filter(key, "==", onTime.ToLocalTime()))).Should().Equal("on-time");
            (await NamesAsync(service, context, Filter(key, "==", DateTime.SpecifyKind(onTime, DateTimeKind.Unspecified)))).Should().Equal("on-time");
            (await NamesAsync(service, context, Filter(key, "==", new DateTimeOffset(onTime).ToOffset(TimeSpan.FromHours(2))))).Should().Equal("on-time");
        }

        (await NamesAsync(service, context, Filter("due", ">", "2026-11-30T23:00:00-01:00"))).Should().Equal("late");
        (await NamesAsync(service, context, Filter("due", "==", JsonSerializer.SerializeToElement<string?>(null)))).Should().Equal("evening-before", "early", "on-time");
        (await NamesAsync(service, context, Filter("slots{any}.startsAt", "<", "2026-11-01T10:00:00+01:00"))).Should().Equal("evening-before", "early");
    }

    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task A_value_the_member_cannot_hold_is_an_invalid_filter(RelationalTestProvider provider)
    {
        await using var context = await CreateContextAsync(provider);
        var service = new CollectionProcessingService(Substitute.For<IMapper>());

        foreach (var (key, value) in new (string, object)[]
                 {
                     ("at", "not a date"),
                     ("atOffset", Json("2026-13-01T09:00:00Z")),
                     ("due", "tomorrow"),
                     ("slots{any}.startsAt", "soon")
                 })
        {
            var invalid = async () => await NamesAsync(service, context, Filter(key, ">=", value));

            (await invalid.Should().ThrowAsync<InvalidFilterCollectionResultException>())
                .Which.Message.Should().EndWith("expected a date and time such as 2026-11-01T09:00:00Z");
        }

        var notANumber = async () => await NamesAsync(service, context, Filter("rank", "==", "first"));
        (await notANumber.Should().ThrowAsync<InvalidFilterCollectionResultException>())
            .Which.Message.Should().Be("Invalid value for filter field 'rank'");
    }

    private static async Task<ItemDbContext> CreateContextAsync(RelationalTestProvider provider)
    {
        var database = await ProviderTestDatabase.CreateAsync(provider);
        var optionsBuilder = new DbContextOptionsBuilder<ItemDbContext>();
        database.UseNewHeapProvider(optionsBuilder);
        var context = new ItemDbContext(optionsBuilder.Options);
        await context.Database.EnsureCreatedAsync();
        context.Items.AddRange(
            Item("evening-before", new DateTime(2026, 10, 31, 23, 30, 0, DateTimeKind.Utc), rank: 1),
            Item("early", new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc), rank: 2),
            Item("on-time", new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc), rank: 3),
            Item("late", new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc), rank: 4, due: new DateTimeOffset(2026, 12, 1, 0, 30, 0, TimeSpan.Zero)));
        await context.SaveChangesAsync();
        return context;
    }

    private static Entry Item(string name, DateTime at, int rank, DateTimeOffset? due = null)
    {
        return new Entry
        {
            Name = name,
            Rank = rank,
            At = at,
            AtOffset = new DateTimeOffset(at),
            Due = due,
            Slots = [new Slot { StartsAt = new DateTimeOffset(at) }]
        };
    }

    private static async Task<string[]> NamesAsync(CollectionProcessingService service, ItemDbContext context, FilterCollectionRequestModel filter)
    {
        var result = await service.GetCollectionResultModelAsync<Entry, Entry>(
            new CollectionRequestModel { Page = 1, ItemsPerPage = 20, Filter = [filter] },
            context.Items,
            resultQueryableFunc: null,
            asNoTracking: true,
            cancellationToken: default,
            (entry => (object)entry.Rank, ListSortDirection.Ascending));
        return result.Items.Select(entry => entry.Name).ToArray();
    }

    private static FilterCollectionRequestModel Filter(string key, string @operator, object value)
    {
        return new FilterCollectionRequestModel { Key = key, Operator = @operator, Value = value };
    }

    private static JsonElement Json(string value)
    {
        return JsonSerializer.SerializeToElement(value);
    }

    private sealed class ItemDbContext(DbContextOptions<ItemDbContext> options) : DbContext(options)
    {
        public DbSet<Entry> Items => Set<Entry>();
    }

    private sealed class Entry
    {
        public int Id { get; set; }

        public required string Name { get; set; }

        [Filterable]
        public int Rank { get; set; }

        [Filterable]
        public DateTime At { get; set; }

        [Filterable]
        public DateTimeOffset AtOffset { get; set; }

        [Filterable]
        public DateTimeOffset? Due { get; set; }

        [Filterable]
        public List<Slot> Slots { get; set; } = [];
    }

    private sealed class Slot
    {
        public int Id { get; set; }

        [Filterable]
        public DateTimeOffset StartsAt { get; set; }
    }
}
