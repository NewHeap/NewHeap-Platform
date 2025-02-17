using System;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NewHeap.Platform.Common.Models.Options;
using NewHeap.Platform.Common.Services;
using StackExchange.Utils;

namespace NewHeap.Platform.Common.Extensions;
public static partial class ServiceCollectionExtensions
{
    public static NewHeapPlatformCommonConfigurator AddNewHeapPlatformCommon(this IServiceCollection services, Action<NewHeapCommonOptions> options)
    {
        if (services == null) throw new ArgumentNullException(nameof(services));
        if (options == null) throw new ArgumentNullException(nameof(options));

        var optionsObj = new NewHeapCommonOptions();
        options.Invoke(optionsObj);

        return services.AddNewHeapPlatformCommon(optionsObj);
    }

    public static NewHeapPlatformCommonConfigurator AddNewHeapPlatformCommon(this IServiceCollection services, NewHeapCommonOptions optionsObj)
    {
        return new NewHeapPlatformCommonConfigurator(services, optionsObj);
    }
}