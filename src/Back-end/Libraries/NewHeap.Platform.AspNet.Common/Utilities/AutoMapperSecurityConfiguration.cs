using AutoMapper;
using AutoMapper.Internal;

namespace NewHeap.Platform.AspNet.Common.Utilities;

internal static class AutoMapperSecurityConfiguration
{
    internal const int DefaultMaxDepth = 64;

    internal static void Apply(IProfileExpression profile)
    {
        profile.Internal().ForAllMaps(ApplyDepthGuard);
    }

    internal static void Apply(IMapperConfigurationExpression configuration)
    {
        configuration.Internal().ForAllMaps(ApplyDepthGuard);
    }

    private static void ApplyDepthGuard(TypeMap typeMap, IMappingExpression mappingExpression)
    {
        if (typeMap.MaxDepth == 0)
        {
            mappingExpression.MaxDepth(DefaultMaxDepth);
        }
    }
}
