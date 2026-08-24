using NewHeap.Platform.Mapping;
using Xunit;

namespace NewHeap.Platform.Common.Tests;

public sealed class MapOnlyIfChangedTests
{
    [Fact]
    public void EqualScalarsAreSkippedWhileChangedAndNullValuesAreWritten()
    {
        var mapper = CreateMapper();
        var destination = new EntityGraph
        {
            Name = "same",
            Description = "clear",
            Count = 1
        };
        destination.ResetCounts();

        mapper.Map(
            new MutateGraph { Name = "same", Description = null, Count = 2 },
            destination);

        Assert.Equal(0, destination.NameSetCount);
        Assert.Equal(1, destination.DescriptionSetCount);
        Assert.Equal(1, destination.CountSetCount);
        Assert.Null(destination.Description);
        Assert.Equal(2, destination.Count);
    }

    [Fact]
    public void NestedObjectsAndCollectionsFollowAutoMapperReferenceEqualitySemantics()
    {
        var mapper = CreateMapper();
        var child = new ChildDestination { Value = "old" };
        var children = new List<ChildDestination> { new() { Value = "old-list" } };
        var navigation = new NavigationEntity { Id = Guid.NewGuid() };
        var destination = new EntityGraph
        {
            Name = "entity",
            Child = child,
            Children = children,
            Navigation = navigation
        };
        destination.ResetCounts();

        mapper.Map(
            new MutateGraph
            {
                Name = "entity",
                Child = new ChildSource { Value = "new" },
                Children = [new ChildSource { Value = "new-list" }]
            },
            destination);

        Assert.Same(child, destination.Child);
        Assert.Equal("new", destination.Child!.Value);
        Assert.Same(children, destination.Children);
        Assert.Equal(["new-list"], destination.Children.Select(item => item.Value));
        Assert.Equal(1, destination.ChildSetCount);
        Assert.Equal(1, destination.ChildrenSetCount);
        Assert.Same(navigation, destination.Navigation);
        Assert.Equal(0, destination.NavigationSetCount);
    }

    private static IMapper CreateMapper()
        => new Mapper(new MapperConfiguration(configuration =>
        {
            configuration.CreateMap<ChildSource, ChildDestination>().MapOnlyIfChanged();
            configuration.CreateMap<MutateGraph, EntityGraph>().MapOnlyIfChanged();
        }));

    private sealed class MutateGraph
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public int Count { get; set; }
        public ChildSource? Child { get; set; }
        public List<ChildSource>? Children { get; set; }
    }

    private sealed class EntityGraph
    {
        private string? _name;
        private string? _description;
        private int _count;
        private ChildDestination? _child;
        private List<ChildDestination>? _children = [];
        private NavigationEntity? _navigation;

        public string? Name
        {
            get => _name;
            set
            {
                NameSetCount++;
                _name = value;
            }
        }

        public string? Description
        {
            get => _description;
            set
            {
                DescriptionSetCount++;
                _description = value;
            }
        }

        public int Count
        {
            get => _count;
            set
            {
                CountSetCount++;
                _count = value;
            }
        }

        public ChildDestination? Child
        {
            get => _child;
            set
            {
                ChildSetCount++;
                _child = value;
            }
        }

        public List<ChildDestination>? Children
        {
            get => _children;
            set
            {
                ChildrenSetCount++;
                _children = value;
            }
        }

        public NavigationEntity? Navigation
        {
            get => _navigation;
            set
            {
                NavigationSetCount++;
                _navigation = value;
            }
        }

        public int NameSetCount { get; private set; }
        public int DescriptionSetCount { get; private set; }
        public int CountSetCount { get; private set; }
        public int ChildSetCount { get; private set; }
        public int ChildrenSetCount { get; private set; }
        public int NavigationSetCount { get; private set; }

        public void ResetCounts()
        {
            NameSetCount = 0;
            DescriptionSetCount = 0;
            CountSetCount = 0;
            ChildSetCount = 0;
            ChildrenSetCount = 0;
            NavigationSetCount = 0;
        }
    }

    private sealed class ChildSource
    {
        public string Value { get; set; } = string.Empty;
    }

    private sealed class ChildDestination
    {
        public string Value { get; set; } = string.Empty;
    }

    private sealed class NavigationEntity
    {
        public Guid Id { get; set; }
    }
}
