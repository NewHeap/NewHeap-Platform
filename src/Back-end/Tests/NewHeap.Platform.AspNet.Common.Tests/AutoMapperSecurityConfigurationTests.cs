using AutoMapper;
using AutoMapper.Internal;
using NewHeap.Platform.AspNet.Common.Utilities;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

public sealed class AutoMapperSecurityConfigurationTests
{
    [Fact]
    public void BuiltInProfileAppliesTheDepthGuardWhenUsedIndependently()
    {
        var configuration = new MapperConfiguration(options =>
            options.AddProfile<AutomapperProfileConfiguration>());

        Assert.All(
            configuration.Internal().GetAllTypeMaps(),
            typeMap => Assert.Equal(AutoMapperSecurityConfiguration.DefaultMaxDepth, typeMap.MaxDepth));
    }

    [Fact]
    public void SecurityDefaultsLimitEveryUnboundedMapWithoutOverridingConsumerConfiguration()
    {
        var configuration = new MapperConfiguration(options =>
        {
            options.AddProfile<ConsumerProfile>();
            AutoMapperSecurityConfiguration.Apply(options);
        });

        var circularMaps = FindCircularMaps(configuration).ToArray();
        var defaultedMap = configuration.Internal()
            .FindTypeMapFor<CircularNode, CircularNodeViewModel>();
        var explicitlyLimitedMap = configuration.Internal()
            .FindTypeMapFor<ExplicitNode, ExplicitNodeViewModel>();
        var flatMap = configuration.Internal()
            .FindTypeMapFor<FlatSource, FlatDestination>();

        Assert.NotEmpty(circularMaps);
        Assert.All(circularMaps, typeMap => Assert.True(typeMap.MaxDepth > 0));
        Assert.Equal(AutoMapperSecurityConfiguration.DefaultMaxDepth, defaultedMap!.MaxDepth);
        Assert.Equal(12, explicitlyLimitedMap!.MaxDepth);
        Assert.Equal(AutoMapperSecurityConfiguration.DefaultMaxDepth, flatMap!.MaxDepth);
    }

    private static IEnumerable<TypeMap> FindCircularMaps(IConfigurationProvider configuration)
    {
        var allMaps = configuration.Internal().GetAllTypeMaps();
        var mapsByDestinationType = allMaps.ToLookup(typeMap => typeMap.DestinationType);

        return allMaps.Where(typeMap =>
            IsCircular(typeMap, mapsByDestinationType, new HashSet<TypeMap>()));
    }

    private static bool IsCircular(
        TypeMap current,
        ILookup<Type, TypeMap> mapsByDestinationType,
        HashSet<TypeMap> path)
    {
        if (!path.Add(current))
        {
            return true;
        }

        foreach (var memberMap in current.MemberMaps.Where(memberMap => !memberMap.Ignored))
        {
            foreach (var nextMap in mapsByDestinationType[memberMap.DestinationType])
            {
                if (IsCircular(nextMap, mapsByDestinationType, new HashSet<TypeMap>(path)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private sealed class ConsumerProfile : Profile
    {
        public ConsumerProfile()
        {
            CreateMap<CircularNode, CircularNodeViewModel>();
            CreateMap<ExplicitNode, ExplicitNodeViewModel>().MaxDepth(12);
            CreateMap<FlatSource, FlatDestination>();
        }
    }

    private sealed class CircularNode
    {
        public CircularNode? Child { get; set; }
    }

    private sealed class CircularNodeViewModel
    {
        public CircularNodeViewModel? Child { get; set; }
    }

    private sealed class ExplicitNode
    {
        public ExplicitNode? Child { get; set; }
    }

    private sealed class ExplicitNodeViewModel
    {
        public ExplicitNodeViewModel? Child { get; set; }
    }

    private sealed class FlatSource
    {
        public string Value { get; set; } = "";
    }

    private sealed class FlatDestination
    {
        public string Value { get; set; } = "";
    }
}
