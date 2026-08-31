extern alias AutoMapper14;

using NewHeap.Platform.Mapping;
using Newtonsoft.Json.Linq;
using AutoMapper14Configuration = AutoMapper14::AutoMapper.MapperConfiguration;
using AutoMapper14DuplicateTypeMapConfigurationException =
    AutoMapper14::AutoMapper.DuplicateTypeMapConfigurationException;
using Xunit;

namespace NewHeap.Platform.Mapping.Tests;

public sealed class AutoMapper14ParityTests
{
    [Fact]
    public void DuplicateMapsUseTheLastRegisteredProfileLikeAutoMapper14()
    {
        var newHeapConfiguration = new MapperConfiguration(configuration =>
        {
            configuration.AddProfile<NewHeapFirstDuplicateProfile>();
            configuration.AddProfile<NewHeapLastDuplicateProfile>();
        });
        var autoMapperConfiguration = new AutoMapper14Configuration(configuration =>
        {
            configuration.AddProfile<AutoMapperFirstDuplicateProfile>();
            configuration.AddProfile<AutoMapperLastDuplicateProfile>();
        });
        var source = new DuplicateSource
        {
            FirstValue = "first",
            LastValue = "last"
        };

        var expected = autoMapperConfiguration.CreateMapper().Map<DuplicateDestination>(source);
        var actual = newHeapConfiguration.CreateMapper().Map<DuplicateDestination>(source);

        Assert.Equal(expected.Value, actual.Value);
        Assert.Equal("last", actual.Value);
    }

    [Fact]
    public void DuplicateMapsAreReportedDuringValidationLikeAutoMapper14()
    {
        var newHeapConfiguration = new MapperConfiguration(configuration =>
        {
            configuration.AddProfile<NewHeapFirstDuplicateProfile>();
            configuration.AddProfile<NewHeapLastDuplicateProfile>();
        });
        var autoMapperConfiguration = new AutoMapper14Configuration(configuration =>
        {
            configuration.AddProfile<AutoMapperFirstDuplicateProfile>();
            configuration.AddProfile<AutoMapperLastDuplicateProfile>();
        });

        Assert.Throws<AutoMapper14DuplicateTypeMapConfigurationException>(
            autoMapperConfiguration.AssertConfigurationIsValid);
        var exception = Assert.Throws<MappingConfigurationException>(
            newHeapConfiguration.AssertConfigurationIsValid);

        Assert.Contains("duplicate maps", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(nameof(DuplicateSource), exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(DuplicateDestination), exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(NewHeapFirstDuplicateProfile), exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(NewHeapLastDuplicateProfile), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitMapFromOverANullNavigationMatchesAutoMapper14()
    {
        var newHeap = new MapperConfiguration(configuration =>
            configuration.CreateMap<ProductSource, ProductDestination>()
                .ForMember(
                    destination => destination.CategoryIds,
                    member => member.MapFrom(source =>
                        source.ProductCategories!.Select(link => link.CategoryId))))
            .CreateMapper();
        var autoMapper = new AutoMapper14Configuration(configuration =>
            configuration.CreateMap<ProductSource, ProductDestination>()
                .ForMember(
                    destination => destination.CategoryIds,
                    member => member.MapFrom(source =>
                        source.ProductCategories!.Select(link => link.CategoryId))))
            .CreateMapper();
        var source = new ProductSource { ProductCategories = null };

        var expected = autoMapper.Map<ProductDestination>(source);
        var actual = newHeap.Map<ProductDestination>(source);

        Assert.Equal(expected.CategoryIds, actual.CategoryIds);
        Assert.Empty(actual.CategoryIds);
    }

    [Fact]
    public void ApplyingAMutateModelKeepsExistingNavigationReferencesLikeAutoMapper14()
    {
        var newHeap = new MapperConfiguration(configuration =>
        {
            configuration.CreateMap<NavigationMutate, NavigationEntity>()
                .ForAllMembers(member => member.Condition(Changed));
            configuration.CreateMap<AggregateMutate, AggregateEntity>()
                .ForAllMembers(member => member.Condition(Changed));
        }).CreateMapper();
        var autoMapper = new AutoMapper14Configuration(configuration =>
        {
            configuration.CreateMap<NavigationMutate, NavigationEntity>()
                .ForAllMembers(member => member.Condition(Changed));
            configuration.CreateMap<AggregateMutate, AggregateEntity>()
                .ForAllMembers(member => member.Condition(Changed));
        }).CreateMapper();
        var source = new AggregateMutate
        {
            Navigation = new NavigationMutate { Name = "updated" },
            Navigations =
            [
                new NavigationMutate { Name = "first" },
                new NavigationMutate { Name = "second" }
            ]
        };
        var expectedEntity = CreateExistingAggregate();
        var actualEntity = CreateExistingAggregate();
        var expectedNavigation = expectedEntity.Navigation;
        var actualNavigation = actualEntity.Navigation;
        var expectedNavigations = expectedEntity.Navigations;
        var actualNavigations = actualEntity.Navigations;

        autoMapper.Map(source, expectedEntity);
        newHeap.Map(source, actualEntity);

        Assert.Same(expectedNavigation, expectedEntity.Navigation);
        Assert.Same(actualNavigation, actualEntity.Navigation);
        Assert.Equal(expectedEntity.Navigation.Name, actualEntity.Navigation.Name);
        Assert.Equal(expectedEntity.NavigationSetterCalls, actualEntity.NavigationSetterCalls);
        Assert.Same(expectedNavigations, expectedEntity.Navigations);
        Assert.Same(actualNavigations, actualEntity.Navigations);
        Assert.Equal(
            expectedEntity.Navigations.Select(navigation => navigation.Name),
            actualEntity.Navigations.Select(navigation => navigation.Name));
        Assert.Equal(expectedEntity.NavigationsSetterCalls, actualEntity.NavigationsSetterCalls);
    }

    [Fact]
    public void ConditionReceivesTheMappedMemberLikeAutoMapper14()
    {
        object? expectedConditionMember = null;
        object? actualConditionMember = null;
        var newHeap = new MapperConfiguration(configuration =>
            configuration.CreateMap<ConversionSource, ConversionDestination>()
                .ForMember(
                    destination => destination.Value,
                    member =>
                    {
                        member.MapFrom(source => source.Value);
                        member.Condition((_, _, sourceMember, _) =>
                        {
                            actualConditionMember = sourceMember;
                            return true;
                        });
                    }))
            .CreateMapper();
        var autoMapper = new AutoMapper14Configuration(configuration =>
            configuration.CreateMap<ConversionSource, ConversionDestination>()
                .ForMember(
                    destination => destination.Value,
                    member =>
                    {
                        member.MapFrom(source => source.Value);
                        member.Condition((_, _, sourceMember, _) =>
                        {
                            expectedConditionMember = sourceMember;
                            return true;
                        });
                    }))
            .CreateMapper();

        var expected = autoMapper.Map<ConversionDestination>(new ConversionSource { Value = "42" });
        var actual = newHeap.Map<ConversionDestination>(new ConversionSource { Value = "42" });

        Assert.Equal(expected.Value, actual.Value);
        Assert.Equal(expectedConditionMember, actualConditionMember);
        Assert.IsType<int>(actualConditionMember);
    }

    [Fact]
    public void NullSourceKeepsAnExistingDestinationLikeAutoMapper14()
    {
        var newHeap = new MapperConfiguration(configuration =>
            configuration.CreateMap<NavigationMutate, NavigationEntity>())
            .CreateMapper();
        var autoMapper = new AutoMapper14Configuration(configuration =>
            configuration.CreateMap<NavigationMutate, NavigationEntity>())
            .CreateMapper();
        var expectedEntity = new NavigationEntity { Name = "existing" };
        var actualEntity = new NavigationEntity { Name = "existing" };

        var expected = autoMapper.Map<NavigationMutate, NavigationEntity>(null!, expectedEntity);
        var actual = newHeap.Map<NavigationMutate, NavigationEntity>(null!, actualEntity);

        Assert.Same(expectedEntity, expected);
        Assert.Same(actualEntity, actual);
        Assert.Equal(expected.Name, actual.Name);
    }

    [Fact]
    public void NullSourceClearsAnExistingCollectionLikeAutoMapper14()
    {
        var newHeap = new MapperConfiguration(_ => { }).CreateMapper();
        var autoMapper = new AutoMapper14Configuration(_ => { }).CreateMapper();
        var expectedCollection = new List<int> { 1 };
        var actualCollection = new List<int> { 1 };

        var expected = autoMapper.Map<List<int>?, List<int>>(null, expectedCollection);
        var actual = newHeap.Map<List<int>?, List<int>>(null, actualCollection);

        Assert.Same(expectedCollection, expected);
        Assert.Same(actualCollection, actual);
        Assert.Empty(expected);
        Assert.Empty(actual);
    }

    [Fact]
    public void ExplicitCollectionConverterTakesPrecedenceLikeAutoMapper14()
    {
        var newHeap = new MapperConfiguration(configuration =>
            configuration.CreateMap<List<string>, List<int>>()
                .ConvertUsing(source => source.Select(int.Parse).Reverse().ToList()))
            .CreateMapper();
        var autoMapper = new AutoMapper14Configuration(configuration =>
            configuration.CreateMap<List<string>, List<int>>()
                .ConvertUsing(source => source.Select(int.Parse).Reverse().ToList()))
            .CreateMapper();

        var expected = autoMapper.Map<List<int>>(new List<string> { "1", "2" });
        var actual = newHeap.Map<List<int>>(new List<string> { "1", "2" });

        Assert.Equal(expected, actual);
        Assert.Equal([2, 1], actual);
    }

    [Fact]
    public void GetterOnlyDestinationCollectionIsPopulatedLikeAutoMapper14()
    {
        var newHeap = new MapperConfiguration(configuration =>
        {
            configuration.CreateMap<NavigationMutate, NavigationEntity>();
            configuration.CreateMap<ReadOnlyAggregateMutate, ReadOnlyAggregateEntity>();
        }).CreateMapper();
        var autoMapper = new AutoMapper14Configuration(configuration =>
        {
            configuration.CreateMap<NavigationMutate, NavigationEntity>();
            configuration.CreateMap<ReadOnlyAggregateMutate, ReadOnlyAggregateEntity>();
        }).CreateMapper();
        var source = new ReadOnlyAggregateMutate
        {
            Navigations = [new NavigationMutate { Name = "mapped" }]
        };

        var expected = autoMapper.Map<ReadOnlyAggregateEntity>(source);
        var actual = newHeap.Map<ReadOnlyAggregateEntity>(source);

        Assert.Equal(
            expected.Navigations.Select(navigation => navigation.Name),
            actual.Navigations.Select(navigation => navigation.Name));
        Assert.Equal(["mapped"], actual.Navigations.Select(navigation => navigation.Name));
    }

    [Fact]
    public void ReadOnlyDictionaryMemberIsMappedLikeAutoMapper14()
    {
        var newHeap = new MapperConfiguration(configuration =>
            configuration.CreateMap<DictionarySource, DictionaryDestination>())
            .CreateMapper();
        var autoMapper = new AutoMapper14Configuration(configuration =>
            configuration.CreateMap<DictionarySource, DictionaryDestination>())
            .CreateMapper();
        var source = new DictionarySource
        {
            Values = new Dictionary<string, string>
            {
                ["first"] = "one",
                ["second"] = "two"
            }
        };

        var expected = autoMapper.Map<DictionaryDestination>(source);
        var actual = newHeap.Map<DictionaryDestination>(source);

        Assert.Equal(expected.Values, actual.Values);
        Assert.Equal("one", actual.Values!["first"]);
        Assert.Equal(expected.Values!.GetType(), actual.Values.GetType());
    }

    [Fact]
    public void DictionaryKeysAndValuesAreConvertedLikeAutoMapper14()
    {
        var newHeap = new MapperConfiguration(configuration =>
            configuration.CreateMap<DictionaryConversionSource, DictionaryConversionDestination>())
            .CreateMapper();
        var autoMapper = new AutoMapper14Configuration(configuration =>
            configuration.CreateMap<DictionaryConversionSource, DictionaryConversionDestination>())
            .CreateMapper();
        var source = new DictionaryConversionSource
        {
            Values = new Dictionary<int, string>
            {
                [1] = "42"
            }
        };

        var expected = autoMapper.Map<DictionaryConversionDestination>(source);
        var actual = newHeap.Map<DictionaryConversionDestination>(source);

        Assert.Equal(expected.Values, actual.Values);
        Assert.Equal(42, actual.Values!["1"]);
    }

    [Fact]
    public void JObjectDictionaryEnumerationMatchesAutoMapper14()
    {
        var newHeap = new MapperConfiguration(_ => { }).CreateMapper();
        var autoMapper = new AutoMapper14Configuration(_ => { }).CreateMapper();
        var source = JObject.Parse("""
            {
              "name": "mapped",
              "enabled": true
            }
            """);

        var expected = autoMapper.Map<JObject>(source);
        var actual = newHeap.Map<JObject>(source);

        Assert.True(JToken.DeepEquals(expected, actual));
        Assert.Equal("mapped", actual.Value<string>("name"));
        Assert.True(actual.Value<bool>("enabled"));
    }

    [Fact]
    public void ExistingMutableDictionaryIsUpdatedInPlaceLikeAutoMapper14()
    {
        var newHeap = new MapperConfiguration(_ => { }).CreateMapper();
        var autoMapper = new AutoMapper14Configuration(_ => { }).CreateMapper();
        var source = new Dictionary<string, string>
        {
            ["current"] = "value"
        };
        var expectedDestination = new Dictionary<string, string>
        {
            ["stale"] = "value"
        };
        var actualDestination = new Dictionary<string, string>
        {
            ["stale"] = "value"
        };

        var expected = autoMapper.Map(source, expectedDestination);
        var actual = newHeap.Map(source, actualDestination);

        Assert.Same(expectedDestination, expected);
        Assert.Same(actualDestination, actual);
        Assert.Equal(expected, actual);
        Assert.False(actual.ContainsKey("stale"));
    }

    [Fact]
    public void ExistingRuntimeDestinationSelectsTheRuntimeTypeMapLikeAutoMapper14()
    {
        var newHeap = new MapperConfiguration(configuration =>
        {
            configuration.CreateMap<BaseRuntimeSource, BaseRuntimeDestination>();
            configuration.CreateMap<DerivedRuntimeSource, DerivedRuntimeDestination>();
        }).CreateMapper();
        var autoMapper = new AutoMapper14Configuration(configuration =>
        {
            configuration.CreateMap<BaseRuntimeSource, BaseRuntimeDestination>();
            configuration.CreateMap<DerivedRuntimeSource, DerivedRuntimeDestination>();
        }).CreateMapper();
        BaseRuntimeSource source = new DerivedRuntimeSource
        {
            Name = "base",
            Detail = "derived"
        };
        BaseRuntimeDestination expectedDestination = new DerivedRuntimeDestination();
        BaseRuntimeDestination actualDestination = new DerivedRuntimeDestination();

        var expected = autoMapper.Map(source, expectedDestination, typeof(BaseRuntimeSource), typeof(BaseRuntimeDestination));
        var actual = newHeap.Map(source, actualDestination, typeof(BaseRuntimeSource), typeof(BaseRuntimeDestination));

        Assert.Same(expectedDestination, expected);
        Assert.Same(actualDestination, actual);
        Assert.Equal(
            ((DerivedRuntimeDestination)expected).Detail,
            ((DerivedRuntimeDestination)actual!).Detail);
        Assert.Equal("derived", ((DerivedRuntimeDestination)actual).Detail);
    }

    [Fact]
    public void GenericExistingDestinationUsesTheDeclaredDestinationMapLikeAutoMapper14()
    {
        var newHeap = new MapperConfiguration(configuration =>
        {
            configuration.CreateMap<BaseRuntimeSource, BaseRuntimeDestination>();
            configuration.CreateMap<DerivedRuntimeSource, DerivedRuntimeDestination>();
        }).CreateMapper();
        var autoMapper = new AutoMapper14Configuration(configuration =>
        {
            configuration.CreateMap<BaseRuntimeSource, BaseRuntimeDestination>();
            configuration.CreateMap<DerivedRuntimeSource, DerivedRuntimeDestination>();
        }).CreateMapper();
        BaseRuntimeSource source = new DerivedRuntimeSource
        {
            Name = "base",
            Detail = "derived"
        };
        BaseRuntimeDestination expectedDestination = new DerivedRuntimeDestination();
        BaseRuntimeDestination actualDestination = new DerivedRuntimeDestination();

        var expected = autoMapper.Map(source, expectedDestination);
        var actual = newHeap.Map(source, actualDestination);

        Assert.Same(expectedDestination, expected);
        Assert.Same(actualDestination, actual);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(
            ((DerivedRuntimeDestination)expected).Detail,
            ((DerivedRuntimeDestination)actual).Detail);
        Assert.Equal("derived", ((DerivedRuntimeDestination)actual).Detail);
    }

    [Fact]
    public void IncludeBaseAppliesBaseMembersAndActionsLikeAutoMapper14()
    {
        var newHeapConfiguration = new MapperConfiguration(configuration =>
        {
            configuration.CreateMap<BaseActionSource, BaseActionDestination>()
                .ForMember(
                    destination => destination.BaseValue,
                    member => member.MapFrom(source => $"base:{source.BaseValue}"))
                .ForMember(
                    destination => destination.BaseActionApplied,
                    member => member.Ignore())
                .AfterMap((_, destination) => destination.BaseActionApplied = true);
            configuration.CreateMap<BaseActionSource, DerivedActionDestination>()
                .IncludeBase<BaseActionSource, BaseActionDestination>();
        });
        var autoMapperConfiguration = new AutoMapper14Configuration(configuration =>
        {
            configuration.CreateMap<BaseActionSource, BaseActionDestination>()
                .ForMember(
                    destination => destination.BaseValue,
                    member => member.MapFrom(source => $"base:{source.BaseValue}"))
                .ForMember(
                    destination => destination.BaseActionApplied,
                    member => member.Ignore())
                .AfterMap((_, destination) => destination.BaseActionApplied = true);
            configuration.CreateMap<BaseActionSource, DerivedActionDestination>()
                .IncludeBase<BaseActionSource, BaseActionDestination>();
        });
        newHeapConfiguration.AssertConfigurationIsValid();
        autoMapperConfiguration.AssertConfigurationIsValid();
        var newHeap = newHeapConfiguration.CreateMapper();
        var autoMapper = autoMapperConfiguration.CreateMapper();
        var source = new BaseActionSource
        {
            BaseValue = "mapped",
            DerivedValue = "derived"
        };

        var expected = autoMapper.Map<DerivedActionDestination>(source);
        var actual = newHeap.Map<DerivedActionDestination>(source);

        Assert.Equal(expected.BaseValue, actual.BaseValue);
        Assert.Equal(expected.DerivedValue, actual.DerivedValue);
        Assert.Equal(expected.BaseActionApplied, actual.BaseActionApplied);
        Assert.Equal("base:mapped", actual.BaseValue);
        Assert.Equal("derived", actual.DerivedValue);
        Assert.True(actual.BaseActionApplied);
    }

    [Fact]
    public void IncludeBaseAllowsDerivedMemberOverridesLikeAutoMapper14()
    {
        var newHeap = new MapperConfiguration(configuration =>
        {
            configuration.CreateMap<BaseActionSource, BaseActionDestination>()
                .ForMember(
                    destination => destination.BaseValue,
                    member => member.MapFrom(source => $"base:{source.BaseValue}"));
            configuration.CreateMap<BaseActionSource, DerivedActionDestination>()
                .IncludeBase<BaseActionSource, BaseActionDestination>()
                .ForMember(
                    destination => destination.BaseValue,
                    member => member.MapFrom(source => $"derived:{source.BaseValue}"));
        }).CreateMapper();
        var autoMapper = new AutoMapper14Configuration(configuration =>
        {
            configuration.CreateMap<BaseActionSource, BaseActionDestination>()
                .ForMember(
                    destination => destination.BaseValue,
                    member => member.MapFrom(source => $"base:{source.BaseValue}"));
            configuration.CreateMap<BaseActionSource, DerivedActionDestination>()
                .IncludeBase<BaseActionSource, BaseActionDestination>()
                .ForMember(
                    destination => destination.BaseValue,
                    member => member.MapFrom(source => $"derived:{source.BaseValue}"));
        }).CreateMapper();
        var source = new BaseActionSource { BaseValue = "mapped" };

        var expected = autoMapper.Map<DerivedActionDestination>(source);
        var actual = newHeap.Map<DerivedActionDestination>(source);

        Assert.Equal(expected.BaseValue, actual.BaseValue);
        Assert.Equal("derived:mapped", actual.BaseValue);
    }

    [Fact]
    public void NestedMapsThroughResolutionContextReuseTheSameContextLikeAutoMapper14()
    {
        var actualContexts = new List<object>();
        var expectedContexts = new List<object>();
        var newHeapConfiguration = new MapperConfiguration(configuration =>
            configuration.CreateMap<RecursiveSource, RecursiveDestination>()
                .ConvertUsing<NewHeapRecursiveConverter>());
        var newHeap = newHeapConfiguration.CreateMapper(serviceType =>
            serviceType == typeof(NewHeapRecursiveConverter)
                ? new NewHeapRecursiveConverter(actualContexts)
                : null);
        var autoMapperConfiguration = new AutoMapper14Configuration(configuration =>
            configuration.CreateMap<RecursiveSource, RecursiveDestination>()
                .ConvertUsing<AutoMapperRecursiveConverter>());
        var autoMapper = autoMapperConfiguration.CreateMapper(serviceType =>
            serviceType == typeof(AutoMapperRecursiveConverter)
                ? new AutoMapperRecursiveConverter(expectedContexts)
                : null);
        var source = new RecursiveSource
        {
            Name = "one",
            Child = new RecursiveSource
            {
                Name = "two",
                Child = new RecursiveSource { Name = "three" }
            }
        };

        var expected = autoMapper.Map<RecursiveDestination>(source);
        var actual = newHeap.Map<RecursiveDestination>(source);

        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Child!.Name, actual.Child!.Name);
        Assert.Equal(expected.Child.Child!.Name, actual.Child.Child!.Name);
        Assert.Single(expectedContexts.Distinct(ReferenceEqualityComparer.Instance));
        Assert.Single(actualContexts.Distinct(ReferenceEqualityComparer.Instance));
    }

    private static bool Changed(
        object source,
        object destination,
        object? sourceMember,
        object? destinationMember)
        => !Equals(sourceMember, destinationMember);

    private static AggregateEntity CreateExistingAggregate()
    {
        var entity = new AggregateEntity
        {
            Navigation = new NavigationEntity { Name = "original" },
            Navigations = [new NavigationEntity { Name = "old" }]
        };
        entity.ResetSetterCalls();
        return entity;
    }

    private sealed class ProductSource
    {
        public List<ProductCategoryLink>? ProductCategories { get; set; }
    }

    private sealed class DuplicateSource
    {
        public string FirstValue { get; set; } = string.Empty;
        public string LastValue { get; set; } = string.Empty;
    }

    private sealed class DuplicateDestination
    {
        public string Value { get; set; } = string.Empty;
    }

    private sealed class NewHeapFirstDuplicateProfile : Profile
    {
        public NewHeapFirstDuplicateProfile()
        {
            CreateMap<DuplicateSource, DuplicateDestination>()
                .ForMember(
                    destination => destination.Value,
                    member => member.MapFrom(source => source.FirstValue));
        }
    }

    private sealed class NewHeapLastDuplicateProfile : Profile
    {
        public NewHeapLastDuplicateProfile()
        {
            CreateMap<DuplicateSource, DuplicateDestination>()
                .ForMember(
                    destination => destination.Value,
                    member => member.MapFrom(source => source.LastValue));
        }
    }

    private sealed class AutoMapperFirstDuplicateProfile : AutoMapper14::AutoMapper.Profile
    {
        public AutoMapperFirstDuplicateProfile()
        {
            CreateMap<DuplicateSource, DuplicateDestination>()
                .ForMember(
                    destination => destination.Value,
                    member => member.MapFrom(source => source.FirstValue));
        }
    }

    private sealed class AutoMapperLastDuplicateProfile : AutoMapper14::AutoMapper.Profile
    {
        public AutoMapperLastDuplicateProfile()
        {
            CreateMap<DuplicateSource, DuplicateDestination>()
                .ForMember(
                    destination => destination.Value,
                    member => member.MapFrom(source => source.LastValue));
        }
    }

    private sealed class ProductCategoryLink
    {
        public Guid CategoryId { get; set; }
    }

    private sealed class ProductDestination
    {
        public IEnumerable<Guid> CategoryIds { get; set; } = [];
    }

    private sealed class AggregateMutate
    {
        public NavigationMutate Navigation { get; set; } = new();
        public List<NavigationMutate> Navigations { get; set; } = [];
    }

    private sealed class AggregateEntity
    {
        private NavigationEntity _navigation = new();
        private List<NavigationEntity> _navigations = [];

        public NavigationEntity Navigation
        {
            get => _navigation;
            set
            {
                NavigationSetterCalls++;
                _navigation = value;
            }
        }

        public List<NavigationEntity> Navigations
        {
            get => _navigations;
            set
            {
                NavigationsSetterCalls++;
                _navigations = value;
            }
        }

        public int NavigationSetterCalls { get; private set; }
        public int NavigationsSetterCalls { get; private set; }

        public void ResetSetterCalls()
        {
            NavigationSetterCalls = 0;
            NavigationsSetterCalls = 0;
        }
    }

    private sealed class NavigationMutate
    {
        public string Name { get; set; } = string.Empty;
    }

    private sealed class NavigationEntity
    {
        public string Name { get; set; } = string.Empty;
    }

    private sealed class ConversionSource
    {
        public string Value { get; set; } = string.Empty;
    }

    private sealed class ConversionDestination
    {
        public int Value { get; set; }
    }

    private sealed class ReadOnlyAggregateMutate
    {
        public List<NavigationMutate> Navigations { get; set; } = [];
    }

    private sealed class ReadOnlyAggregateEntity
    {
        public List<NavigationEntity> Navigations { get; } = [];
    }

    private sealed class DictionarySource
    {
        public IReadOnlyDictionary<string, string>? Values { get; set; }
    }

    private sealed class DictionaryDestination
    {
        public IReadOnlyDictionary<string, string>? Values { get; set; }
    }

    private sealed class DictionaryConversionSource
    {
        public Dictionary<int, string>? Values { get; set; }
    }

    private sealed class DictionaryConversionDestination
    {
        public IReadOnlyDictionary<string, int>? Values { get; set; }
    }

    private sealed class RecursiveSource
    {
        public string Name { get; set; } = string.Empty;
        public RecursiveSource? Child { get; set; }
    }

    private sealed class BaseActionSource
    {
        public string BaseValue { get; set; } = string.Empty;
        public string DerivedValue { get; set; } = string.Empty;
    }

    private class BaseActionDestination
    {
        public string BaseValue { get; set; } = string.Empty;
        public bool BaseActionApplied { get; set; }
    }

    private sealed class DerivedActionDestination : BaseActionDestination
    {
        public string DerivedValue { get; set; } = string.Empty;
    }

    private sealed class RecursiveDestination
    {
        public string Name { get; set; } = string.Empty;
        public RecursiveDestination? Child { get; set; }
    }

    private class BaseRuntimeSource
    {
        public string Name { get; set; } = string.Empty;
    }

    private sealed class DerivedRuntimeSource : BaseRuntimeSource
    {
        public string Detail { get; set; } = string.Empty;
    }

    private class BaseRuntimeDestination
    {
        public string Name { get; set; } = string.Empty;
    }

    private sealed class DerivedRuntimeDestination : BaseRuntimeDestination
    {
        public string Detail { get; set; } = string.Empty;
    }

    private sealed class NewHeapRecursiveConverter(ICollection<object> contexts) :
        ITypeConverter<RecursiveSource, RecursiveDestination>
    {
        public RecursiveDestination Convert(
            RecursiveSource source,
            RecursiveDestination destination,
            ResolutionContext context)
        {
            contexts.Add(context);
            destination ??= new RecursiveDestination();
            destination.Name = source.Name;
            destination.Child = context.Mapper.Map<RecursiveDestination>(source.Child);
            return destination;
        }
    }

    private sealed class AutoMapperRecursiveConverter(ICollection<object> contexts) :
        AutoMapper14::AutoMapper.ITypeConverter<RecursiveSource, RecursiveDestination>
    {
        public RecursiveDestination Convert(
            RecursiveSource source,
            RecursiveDestination destination,
            AutoMapper14::AutoMapper.ResolutionContext context)
        {
            contexts.Add(context);
            destination ??= new RecursiveDestination();
            destination.Name = source.Name;
            destination.Child = context.Mapper.Map<RecursiveDestination>(source.Child);
            return destination;
        }
    }

}
