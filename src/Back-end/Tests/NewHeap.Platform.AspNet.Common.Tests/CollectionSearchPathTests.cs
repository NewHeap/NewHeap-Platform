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
/// <see cref="NhCollectionContract.SearchPaths"/> names exactly the members a search reads: a value
/// in a listed path is found and a value anywhere else is not, with and without collection
/// processing options. The API bridge gateway relies on these paths to refuse searches over
/// redacted fields.
/// </summary>
public sealed class CollectionSearchPathTests
{
    private static readonly string[] Columns =
    [
        "name",
        "email",
        "notes",
        "owner.displayName",
        "owner.email",
        "owner.secret",
        "owner.address.street",
        "owner.address.region.name",
        "tags.label"
    ];

    [Fact]
    public void Search_paths_follow_searchable_members_up_to_two_nested_levels()
    {
        var contract = NhCollectionContractMetadata.Describe(typeof(ContactViewModel));

        Assert.Equal(
            ["name", "email", "owner.displayName", "owner.email", "owner.address.street"],
            contract.SearchPaths);
    }

    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task Search_matches_a_value_only_in_a_published_search_path(RelationalTestProvider provider)
    {
        var database = await ProviderTestDatabase.CreateAsync(provider);
        var options = new DbContextOptionsBuilder<ContactDbContext>();
        database.UseNewHeapProvider(options);

        await using var context = new ContactDbContext(options.Options);
        await context.Database.EnsureCreatedAsync();
        context.Contacts.AddRange(Columns.Select(Seed));
        await context.SaveChangesAsync();

        var searchPaths = NhCollectionContractMetadata.Describe(typeof(ContactViewModel)).SearchPaths;
        var processor = new CollectionProcessingService(Substitute.For<IMapper>());
        foreach (var column in Columns)
        {
            foreach (var useOptions in new[] { false, true })
            {
                IQueryable<Contact> query = context.Contacts.AsNoTracking();
                if (useOptions)
                {
                    processor.ProcessSearch(ref query, Marker(column), CollectionProcessingOptions<Contact, ContactViewModel>.Default);
                }
                else
                {
                    processor.ProcessSearch<Contact, ContactViewModel>(ref query, Marker(column));
                }

                var found = await query.Select(contact => contact.Name).ToListAsync();
                if (searchPaths.Contains(column))
                {
                    Assert.Equal([column == "name" ? Marker(column) : column], found);
                }
                else
                {
                    Assert.Empty(found);
                }
            }
        }
    }

    private static string Marker(string column)
    {
        return "marker-" + column.Replace('.', '-');
    }

    /// <summary>A contact that holds the marker of one column and plain text everywhere else.</summary>
    private static Contact Seed(string column)
    {
        string Value(string candidate)
        {
            return candidate == column ? Marker(column) : "plain";
        }

        return new Contact
        {
            Name = column == "name" ? Marker(column) : column,
            Email = Value("email"),
            Notes = Value("notes"),
            ExternalId = Guid.NewGuid(),
            Owner = new ContactOwner
            {
                DisplayName = Value("owner.displayName"),
                Email = Value("owner.email"),
                Secret = Value("owner.secret"),
                Address = new ContactAddress
                {
                    Street = Value("owner.address.street"),
                    Region = new ContactRegion { Name = Value("owner.address.region.name") }
                }
            },
            Tags = [new ContactTag { Label = Value("tags.label") }]
        };
    }

    private sealed class ContactDbContext(DbContextOptions<ContactDbContext> options) : DbContext(options)
    {
        public DbSet<Contact> Contacts => Set<Contact>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Contact>().HasOne(contact => contact.Owner).WithMany();
            modelBuilder.Entity<Contact>().HasMany(contact => contact.Tags).WithOne();
            modelBuilder.Entity<ContactOwner>().HasOne(owner => owner.Address).WithMany();
            modelBuilder.Entity<ContactAddress>().HasOne(address => address.Region).WithMany();
        }
    }

    private sealed class Contact
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string Email { get; set; } = "";
        public string Notes { get; set; } = "";
        public Guid ExternalId { get; set; }
        public ContactOwner? Owner { get; set; }
        public ICollection<ContactTag> Tags { get; set; } = [];
    }

    private sealed class ContactOwner
    {
        public int Id { get; set; }
        public string DisplayName { get; set; } = "";
        public string Email { get; set; } = "";
        public string Secret { get; set; } = "";
        public ContactAddress? Address { get; set; }
    }

    private sealed class ContactAddress
    {
        public int Id { get; set; }
        public string Street { get; set; } = "";
        public ContactRegion? Region { get; set; }
    }

    private sealed class ContactRegion
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    private sealed class ContactTag
    {
        public int Id { get; set; }
        public string Label { get; set; } = "";
    }

    private sealed class ContactViewModel
    {
        public int Id { get; set; }
        [Searchable] public string Name { get; set; } = "";
        [Searchable] public string Email { get; set; } = "";
        public string Notes { get; set; } = "";
        [Searchable] public Guid ExternalId { get; set; }
        [Searchable] public ContactOwnerViewModel? Owner { get; set; }
        [Searchable] public List<ContactTagViewModel> Tags { get; set; } = [];
    }

    private sealed class ContactOwnerViewModel
    {
        [Searchable] public string DisplayName { get; set; } = "";
        [Searchable] public string Email { get; set; } = "";
        public string Secret { get; set; } = "";
        [Searchable] public ContactAddressViewModel? Address { get; set; }
    }

    private sealed class ContactAddressViewModel
    {
        [Searchable] public string Street { get; set; } = "";
        [Searchable] public ContactRegionViewModel? Region { get; set; }
    }

    private sealed class ContactRegionViewModel
    {
        [Searchable] public string Name { get; set; } = "";
    }

    private sealed class ContactTagViewModel
    {
        [Searchable] public string Label { get; set; } = "";
    }
}
