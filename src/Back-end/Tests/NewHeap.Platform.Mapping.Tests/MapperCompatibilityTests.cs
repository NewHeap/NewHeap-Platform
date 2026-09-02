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

    [Fact]
    public void IgnoreExcludesAMemberWithoutReadingSourceOrDestinationGetters()
    {
        var configuration = new MapperConfiguration(expression =>
            expression.CreateMap<DangerousSource, DangerousDestination>()
                .ForMember(destination => destination.Dangerous, member => member.Ignore()));
        configuration.AssertConfigurationIsValid();
        var destination = new DangerousDestination { Safe = "before" };

        var result = configuration.CreateMapper().Map(
            new DangerousSource { Safe = "after" },
            destination);

        Assert.Same(destination, result);
        Assert.Equal("after", result.Safe);
        Assert.Equal(0, destination.DangerousGetterCalls);
    }

    [Fact]
    public void ConditionFalseStillReadsMembersBeforeEvaluatingTheCondition()
    {
        var mapper = CreateMapper(configuration =>
            configuration.CreateMap<CountingSource, CountingDestination>()
                .ForMember(
                    destination => destination.Value,
                    member => member.Condition((_, _, _, _) => false)));
        var source = new CountingSource();
        var destination = new CountingDestination();

        mapper.Map(source, destination);

        Assert.Equal(1, source.GetterCalls);
        Assert.Equal(1, destination.GetterCalls);
        Assert.Equal(0, destination.SetterCalls);
    }

    [Fact]
    public void DependencyInjectionResolvesMemberResolversConvertersAndMappingActions()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new MappingPrefix("resolved"));
        services.AddTransient<DisplayNameResolver>();
        services.AddTransient<StrongValueConverter>();
        services.AddTransient<EnrichmentAction>();
        services.AddAutoMapper(configuration =>
        {
            configuration.CreateMap<ResolverSource, ResolverDestination>()
                .ForMember(
                    destination => destination.DisplayName,
                    member => member.MapFrom<DisplayNameResolver>())
                .ForMember(
                    destination => destination.EnrichedBy,
                    member => member.Ignore())
                .AfterMap<EnrichmentAction>();
            configuration.CreateMap<string, StrongValue>()
                .ConvertUsing<StrongValueConverter>();
        });

        using var provider = services.BuildServiceProvider();
        var configuration = provider.GetRequiredService<IConfigurationProvider>();
        configuration.AssertConfigurationIsValid();
        var mapper = provider.GetRequiredService<IMapper>();

        var destination = mapper.Map<ResolverDestination>(new ResolverSource { Name = "project" });
        var strongValue = mapper.Map<StrongValue>("identifier");

        Assert.Equal("resolved:project", destination.DisplayName);
        Assert.Equal("resolved", destination.EnrichedBy);
        Assert.Equal("resolved:identifier", strongValue.Value);
    }

    [Fact]
    public void ConstructUsingCreatesDestinationsWithoutAParameterlessConstructor()
    {
        var configuration = new MapperConfiguration(expression =>
            expression.CreateMap<ConstructionSource, ConstructedDestination>()
                .ConstructUsing(source => new ConstructedDestination(source.Id))
                .ForMember(destination => destination.WasMapped, member => member.Ignore())
                .AfterMap((source, destination) => destination.WasMapped = source.Name.Length > 0));
        configuration.AssertConfigurationIsValid();

        var result = configuration.CreateMapper().Map<ConstructedDestination>(
            new ConstructionSource { Id = 42, Name = "constructed" });

        Assert.Equal(42, result.Id);
        Assert.Equal("constructed", result.Name);
        Assert.True(result.WasMapped);
    }

    [Fact]
    public void ConvertUsingReplacesMemberMappingForValueObjects()
    {
        var configuration = new MapperConfiguration(expression =>
            expression.CreateMap<string, StrongValue>()
                .ConvertUsing(value => new StrongValue(value.ToUpperInvariant())));
        configuration.AssertConfigurationIsValid();

        var result = configuration.CreateMapper().Map<StrongValue>("value-object");

        Assert.Equal("VALUE-OBJECT", result.Value);
    }

    [Fact]
    public void ConfigurationValidationReportsUnmappedAndIncompatibleMembers()
    {
        var configuration = new MapperConfiguration(expression =>
            expression.CreateMap<InvalidConfigurationSource, InvalidConfigurationDestination>());

        var exception = Assert.Throws<MappingConfigurationException>(
            configuration.AssertConfigurationIsValid);

        Assert.Contains(nameof(InvalidConfigurationDestination.Unmapped), exception.Message);
        Assert.Contains(nameof(InvalidConfigurationDestination.Incompatible), exception.Message);
        Assert.Contains("cannot map", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConfigurationValidationAcceptsExplicitlyIgnoredMembers()
    {
        var configuration = new MapperConfiguration(expression =>
            expression.CreateMap<InvalidConfigurationSource, InvalidConfigurationDestination>()
                .ForMember(destination => destination.Unmapped, member => member.Ignore())
                .ForMember(destination => destination.Incompatible, member => member.Ignore()));

        configuration.AssertConfigurationIsValid();
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

    private sealed class DangerousSource
    {
        public string Safe { get; set; } = string.Empty;

        public string Dangerous =>
            throw new InvalidOperationException("The ignored source getter must not be evaluated.");
    }

    private sealed class DangerousDestination
    {
        private int _dangerousGetterCalls;

        public string Safe { get; set; } = string.Empty;

        public string Dangerous
        {
            get
            {
                _dangerousGetterCalls++;
                throw new InvalidOperationException("The ignored destination getter must not be evaluated.");
            }
            set => throw new InvalidOperationException("The ignored destination setter must not be evaluated.");
        }

        public int DangerousGetterCalls => _dangerousGetterCalls;
    }

    private sealed class CountingSource
    {
        private int _getterCalls;

        public string Value
        {
            get
            {
                _getterCalls++;
                return "source";
            }
        }

        public int GetterCalls => _getterCalls;
    }

    private sealed class CountingDestination
    {
        private int _getterCalls;
        private int _setterCalls;

        public string Value
        {
            get
            {
                _getterCalls++;
                return "destination";
            }
            set => _setterCalls++;
        }

        public int GetterCalls => _getterCalls;
        public int SetterCalls => _setterCalls;
    }

    private sealed record MappingPrefix(string Value);

    private sealed class ResolverSource
    {
        public string Name { get; set; } = string.Empty;
    }

    private sealed class ResolverDestination
    {
        public string DisplayName { get; set; } = string.Empty;
        public string EnrichedBy { get; set; } = string.Empty;
    }

    private sealed class DisplayNameResolver(MappingPrefix prefix) :
        IValueResolver<ResolverSource, ResolverDestination, string>
    {
        public string Resolve(
            ResolverSource source,
            ResolverDestination destination,
            string destinationMember,
            ResolutionContext context)
            => $"{prefix.Value}:{source.Name}";
    }

    private sealed class StrongValueConverter(MappingPrefix prefix) :
        ITypeConverter<string, StrongValue>
    {
        public StrongValue Convert(
            string source,
            StrongValue destination,
            ResolutionContext context)
            => new($"{prefix.Value}:{source}");
    }

    private sealed class EnrichmentAction(MappingPrefix prefix) :
        IMappingAction<ResolverSource, ResolverDestination>
    {
        public void Process(
            ResolverSource source,
            ResolverDestination destination,
            ResolutionContext context)
        {
            destination.EnrichedBy = prefix.Value;
        }
    }

    private sealed class StrongValue
    {
        public StrongValue(string value)
        {
            Value = value;
        }

        public string Value { get; }
    }

    private sealed class ConstructionSource
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    private sealed class ConstructedDestination
    {
        public ConstructedDestination(int id)
        {
            Id = id;
        }

        public int Id { get; }
        public string Name { get; set; } = string.Empty;
        public bool WasMapped { get; set; }
    }

    private sealed class InvalidConfigurationSource
    {
        public string Compatible { get; set; } = string.Empty;
        public string Incompatible { get; set; } = string.Empty;
    }

    private sealed class InvalidConfigurationDestination
    {
        public string Compatible { get; set; } = string.Empty;
        public Guid Incompatible { get; set; }
        public string Unmapped { get; set; } = string.Empty;
    }
}
