using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models.Mutate;
using NewHeap.Platform.AspNet.Common.Models.View;
using NewHeap.Platform.AspNet.Common.Utilities;
using NewHeap.Platform.Mapping;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

public sealed class MappingProfileCompatibilityTests
{
    [Fact]
    public void BuiltInProfileMapsNestedDivisionNavigation()
    {
        var mapper = CreateMapper();
        var divisionId = Guid.NewGuid();
        var user = new NhUser
        {
            Id = Guid.NewGuid(),
            Email = "mapping@example.com",
            ActiveDivisionId = divisionId,
            ActiveDivision = new NhDivision
            {
                Id = divisionId,
                Name = "Mapped division"
            }
        };

        var result = mapper.Map<NhUserViewModel<NhDivisionViewModel>>(user);

        Assert.Equal(user.Id, result.Id);
        Assert.Equal(user.Email, result.Email);
        Assert.Equal(divisionId, result.ActiveDivisionId);
        Assert.NotNull(result.ActiveDivision);
        Assert.Equal(divisionId, result.ActiveDivision.Id);
        Assert.Equal("Mapped division", result.ActiveDivision.Name);
    }

    [Fact]
    public void MutateMappingKeepsEntityNavigationCollection()
    {
        var mapper = CreateMapper();
        var divisionUsers = new List<NhDivisionUser>
        {
            new() { Id = Guid.NewGuid() }
        };
        var division = new NhDivision
        {
            Name = "Before",
            Description = "Before",
            DivisionUsers = divisionUsers
        };

        var result = mapper.Map(
            new NhDivisionMutateModel
            {
                Name = "After",
                Description = "After"
            },
            division);

        Assert.Same(division, result);
        Assert.Equal("After", result.Name);
        Assert.Equal("After", result.Description);
        Assert.Same(divisionUsers, result.DivisionUsers);
        Assert.Single(result.DivisionUsers);
    }

    private static IMapper CreateMapper()
        => new Mapper(new MapperConfiguration(configuration =>
            configuration.AddProfile<AutomapperProfileConfiguration>()));
}
