using System;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NewHeap.Platform.Common.Models.Options;
using NewHeap.Platform.Common.Services;
using StackExchange.Utils;

namespace NewHeap.Platform.Common;
public static partial class ServiceCollectionExtensions
{
    public static NewHeapPlatformCommonConfigurator AddNewHeapPlatformCommon(this IServiceCollection services, NewHeapCommonOptions optionsObj)
    {
        if (services == null) throw new ArgumentNullException(nameof(services));
        if (optionsObj == null) throw new ArgumentNullException(nameof(optionsObj));

        return new NewHeapPlatformCommonConfigurator(services, optionsObj);
    }
}