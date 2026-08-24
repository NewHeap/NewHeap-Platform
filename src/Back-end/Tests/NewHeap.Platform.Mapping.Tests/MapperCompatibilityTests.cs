using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.Mapping;
using Xunit;

namespace NewHeap.Platform.Mapping.Tests;

public sealed class MapperCompatibilityTests
{
    [Fact]
    public void ExistingNestedObjectsAndCollectionsAreReused()
    {
        var mapper = CreateMapper(configuration =>
        {
            configuration.CreateMap<ChildSource, ChildDestination>();
            configuration.CreateMap<ParentSource, ParentDestination>();
        });
        var child = new ChildDestination { Name = "old-child" };
        var oldListChild = new ChildDestination { Name = "old-list-child" };
        var children = new List<ChildDestination> { oldListChild };
        var destination = new ParentDestination
        {
            Name = "old-parent",
            Child = child,
            Children = children
        };

        var result = mapper.Map(
            new ParentSource
            {
                Name = "new-parent",
                Child = new ChildSource { Name = "new-child" },
                Children = [new ChildSource { Name = "new-list-child" }]
            },
            destination);

        Assert.Same(destination, result);
        Assert.Same(child, result.Child);
        Assert.Same(children, result.Children);
        Assert.Equal("new-child", result.Child!.Name);
        Assert.Equal(["new-list-child"], result.Children!.Select(item => item.Name));
        Assert.NotSame(oldListChild, result.Children!.Single());
    }

    [Fact]
    public void NullNestedMembersClearReferencesAndCollectionsLikeAutoMapper14()
    {
        var mapper = CreateMapper(configuration =>
        {
            configuration.CreateMap<ChildSource, ChildDestination>();
            configuration.CreateMap<ParentSource, ParentDestination>();
        });
        var children = new List<ChildDestination> { new() { Name = "remove" } };
        var destination = new ParentDestination
        {
            Child = new ChildDestination { Name = "remove" },
            Children = children
        };

        var result = mapper.Map(
            new ParentSource { Name = "nulls", Child = null, Children = null },
            destination);

        Assert.Null(result.Child);
        Assert.Same(children, result.Children);
        Assert.Empty(result.Children!);
        Assert.Null(mapper.Map<ParentDestination>(null));

        var destinationWithoutCollection = new ParentDestination { Children = null };
        mapper.Map(new ParentSource { Children = null }, destinationWithoutCollection);
        Assert.NotNull(destinationWithoutCollection.Children);
        Assert.Empty(destinationWithoutCollection.Children);
    }

    [Fact]
    public void MemberConditionSkipsEqualValuesAndStillClearsNullChanges()
    {
        var mapper = CreateMapper(configuration =>
            configuration.CreateMap<UpdateSource, UpdateDestination>()
                .ForAllMembers(member => member.Condition((_, _, sourceMember, destinationMember) =>
                    sourceMember is null
                        ? destinationMember is not null
                        : !sourceMember.Equals(destinationMember))));
        var destination = new UpdateDestination
        {
            Name = "same",
            Optional = "clear-me",
            Number = 4
        };
        destination.ResetCounts();

        mapper.Map(
            new UpdateSource { Name = "same", Optional = null, Number = 5 },
            destination);

        Assert.Equal(0, destination.NameSetCount);
        Assert.Equal(1, destination.OptionalSetCount);
        Assert.Equal(1, destination.NumberSetCount);
        Assert.Null(destination.Optional);
        Assert.Equal(5, destination.Number);
    }

    [Fact]
    public void MapFromMapsTheWholeSourceAndItsNestedCollection()
    {
        var mapper = CreateMapper(configuration =>
        {
            configuration.CreateMap<ChildSource, ChildDestination>();
            configuration.CreateMap<ParentSource, ParentDestination>();
            configuration.CreateMap<ParentSource, CompositeDestination>()
                .ForMember(destination => destination.Parent, member => member.MapFrom(source => source))
                .ForMember(destination => destination.Children, member => member.MapFrom(source => source.Children));
        });

        var result = mapper.Map<CompositeDestination>(new ParentSource
        {
            Name = "composite",
            Child = new ChildSource { Name = "nested" },
            Children = [new ChildSource { Name = "first" }, new ChildSource { Name = "second" }]
        });

        Assert.Equal("composite", result.Parent.Name);
        Assert.Equal("nested", result.Parent.Child!.Name);
        Assert.Equal(["first", "second"], result.Children.Select(item => item.Name));
    }

    [Fact]
    public void MaxDepthStopsARecursiveMemberAtTheSameBoundaryAsAutoMapper14()
    {
        var mapper = CreateMapper(configuration =>
            configuration.CreateMap<CircularSource, CircularDestination>().MaxDepth(2));
        var source = new CircularSource
        {
            Name = "one",
            Child = new CircularSource
            {
                Name = "two",
                Child = new CircularSource { Name = "three" }
            }
        };

        var result = mapper.Map<CircularDestination>(source);

        Assert.Equal("one", result.Name);
        Assert.Equal("two", result.Child!.Name);
        Assert.Null(result.Child.Child);
    }

    [Fact]
    public void EveryMapHasA64LevelDefaultDepthGuard()
    {
        var mapper = CreateMapper(configuration =>
            configuration.CreateMap<CircularSource, CircularDestination>());
        var source = new CircularSource { Name = "1" };
        var current = source;
        for (var depth = 2; depth <= 70; depth++)
        {
            current.Child = new CircularSource { Name = depth.ToString() };
            current = current.Child;
        }

        var result = mapper.Map<CircularDestination>(source);
        var mappedDepth = 0;
        for (var currentResult = result; currentResult is not null; currentResult = currentResult.Child)
        {
            mappedDepth++;
        }

        Assert.Equal(64, mappedDepth);
    }

    [Fact]
    public void AssignableNestedMembersKeepTheirSourceReference()
    {
        var mapper = CreateMapper(configuration =>
            configuration.CreateMap<SameTypeParentSource, SameTypeParentDestination>());
        var child = new SameTypeChild { Name = "same-instance" };

        var result = mapper.Map<SameTypeParentDestination>(new SameTypeParentSource { Child = child });

        Assert.Same(child, result.Child);
    }

    [Fact]
    public void MissingNestedMapThrowsAMappingException()
    {
        var mapper = CreateMapper(configuration =>
            configuration.CreateMap<MissingParentSource, MissingParentDestination>());

        var exception = Assert.Throws<MappingException>(() =>
            mapper.Map<MissingParentDestination>(new MissingParentSource
            {
                Child = new MissingChildSource { Name = "missing" }
            }));

        Assert.Contains(typeof(MissingChildSource).FullName!, exception.Message);
        Assert.Contains(typeof(MissingChildDestination).FullName!, exception.Message);
    }

    [Fact]
    public void ProfilesComposeAndNullableValuesMapToNonNullableMembers()
    {
        var mapper = CreateMapper(configuration =>
        {
            configuration.AddProfile<FirstProfile>();
            configuration.AddProfile<SecondProfile>();
        });

        var intermediate = mapper.Map<NullableDestination>(new NullableSource { Id = 42 });
        var result = mapper.Map<FinalDestination>(intermediate);

        Assert.Equal(42, intermediate.Id);
        Assert.Equal(42, result.Id);
    }

    [Fact]
    public void AssemblyScanningFindsMultipleProfilesOnlyOnce()
    {
        var mapper = CreateMapper(configuration =>
        {
            configuration.AddMaps(typeof(MapperCompatibilityTests).Assembly);
            configuration.AddMaps(typeof(MapperCompatibilityTests));
        });

        var result = mapper.Map<ScannedDestination>(new ScannedSource { Value = "scanned" });

        Assert.Equal("scanned", result.Value);
    }

    [Fact]
    public void DependencyInjectionUsesOneConfigurationAndTransientMappers()
    {
        var services = new ServiceCollection();
        services.AddAutoMapper(configuration =>
            configuration.CreateMap<ChildSource, ChildDestination>());

        using var provider = services.BuildServiceProvider();
        var firstMapper = provider.GetRequiredService<IMapper>();
        var secondMapper = provider.GetRequiredService<IMapper>();

        Assert.NotSame(firstMapper, secondMapper);
        Assert.Same(firstMapper.ConfigurationProvider, secondMapper.ConfigurationProvider);
        Assert.Equal(
            "mapped",
            firstMapper.Map<ChildDestination>(new ChildSource { Name = "mapped" }).Name);
    }

    [Fact]
    public void MapperCanBeUsedConcurrentlyWithIndependentDepthContexts()
    {
        var mapper = CreateMapper(configuration =>
            configuration.CreateMap<ChildSource, ChildDestination>());

        var results = Enumerable.Range(0, 100)
            .AsParallel()
            .Select(index => mapper.Map<ChildDestination>(new ChildSource { Name = index.ToString() }))
            .ToArray();

        Assert.Equal(100, results.Length);
        Assert.Equal(100, results.Select(result => result.Name).Distinct().Count());
    }

    private static IMapper CreateMapper(Action<IMapperConfigurationExpression> configure)
        => new Mapper(new MapperConfiguration(configure));

    private sealed class FirstProfile : Profile
    {
        public FirstProfile()
        {
            CreateMap<NullableSource, NullableDestination>();
        }
    }

    private sealed class SecondProfile : Profile
    {
        public SecondProfile()
        {
            CreateMap<NullableDestination, FinalDestination>();
        }
    }

    private sealed class ScannedProfile : Profile
    {
        public ScannedProfile()
        {
            CreateMap<ScannedSource, ScannedDestination>();
        }
    }

    private sealed class ChildSource
    {
        public string Name { get; set; } = string.Empty;
    }

    private sealed class ChildDestination
    {
        public string Name { get; set; } = string.Empty;
    }

    private sealed class ParentSource
    {
        public string Name { get; set; } = string.Empty;
        public ChildSource? Child { get; set; }
        public List<ChildSource>? Children { get; set; }
    }

    private sealed class ParentDestination
    {
        public string Name { get; set; } = string.Empty;
        public ChildDestination? Child { get; set; }
        public List<ChildDestination>? Children { get; set; } = [];
    }

    private sealed class CompositeDestination
    {
        public ParentDestination Parent { get; set; } = new();
        public List<ChildDestination> Children { get; set; } = [];
    }

    private sealed class UpdateSource
    {
        public string? Name { get; set; }
        public string? Optional { get; set; }
        public int Number { get; set; }
    }

    private sealed class UpdateDestination
    {
        private string? _name;
        private string? _optional;
        private int _number;

        public string? Name
        {
            get => _name;
            set
            {
                NameSetCount++;
                _name = value;
            }
        }

        public string? Optional
        {
            get => _optional;
            set
            {
                OptionalSetCount++;
                _optional = value;
            }
        }

        public int Number
        {
            get => _number;
            set
            {
                NumberSetCount++;
                _number = value;
            }
        }

        public int NameSetCount { get; private set; }
        public int OptionalSetCount { get; private set; }
        public int NumberSetCount { get; private set; }

        public void ResetCounts()
        {
            NameSetCount = 0;
            OptionalSetCount = 0;
            NumberSetCount = 0;
        }
    }

    private sealed class CircularSource
    {
        public string Name { get; set; } = string.Empty;
        public CircularSource? Child { get; set; }
    }

    private sealed class CircularDestination
    {
        public string Name { get; set; } = string.Empty;
        public CircularDestination? Child { get; set; }
    }

    private sealed class SameTypeParentSource
    {
        public SameTypeChild? Child { get; set; }
    }

    private sealed class SameTypeParentDestination
    {
        public SameTypeChild? Child { get; set; }
    }

    private sealed class SameTypeChild
    {
        public string Name { get; set; } = string.Empty;
    }

    private sealed class MissingParentSource
    {
        public MissingChildSource? Child { get; set; }
    }

    private sealed class MissingParentDestination
    {
        public MissingChildDestination? Child { get; set; }
    }

    private sealed class MissingChildSource
    {
        public string Name { get; set; } = string.Empty;
    }

    private sealed class MissingChildDestination
    {
        public string Name { get; set; } = string.Empty;
    }

    private sealed class NullableSource
    {
        public int? Id { get; set; }
    }

    private sealed class NullableDestination
    {
        public int Id { get; set; }
    }

    private sealed class FinalDestination
    {
        public int Id { get; set; }
    }

    private sealed class ScannedSource
    {
        public string Value { get; set; } = string.Empty;
    }

    private sealed class ScannedDestination
    {
        public string Value { get; set; } = string.Empty;
    }
}
