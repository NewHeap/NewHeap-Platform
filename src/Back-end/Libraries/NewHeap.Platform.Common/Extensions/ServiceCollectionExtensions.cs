using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.Common.Models.Options;

namespace NewHeap.Platform.Common.Extensions;

public static partial class ServiceCollectionExtensions
{
    public static NewHeapPlatformCommonConfigurator AddNewHeapPlatformCommon(this IServiceCollection services,
        NewHeapCommonOptions optionsObj)
    {
        if (services == null)
        {
            throw new ArgumentNullException(nameof(services));
        }

        if (optionsObj == null)
        {
            throw new ArgumentNullException(nameof(optionsObj));
        }

        return new NewHeapPlatformCommonConfigurator(services, optionsObj);
    }
}