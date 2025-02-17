using System;
using System.IO;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NewHeap.Platform.AspNet.Common.Models.Options;
using NewHeap.Platform.Common.Extensions;
using NewHeap.Platform.Common.Models.Options;
using NewHeap.Platform.Common.Services;
using StackExchange.Utils;

namespace NewHeap.Platform.AspNet.Common.Extensions;
public static partial class ServiceCollectionExtensions
{
    public static NewHeapPlatformAspNetCommonConfigurator AddNewHeapPlatformAspNetCommon(this IServiceCollection services, Action<NewHeapAspNetCommonOptions> options)
    {
        if (services == null) throw new ArgumentNullException(nameof(services));
        if (options == null) throw new ArgumentNullException(nameof(options));

        var optionsObj = new NewHeapAspNetCommonOptions();
        options.Invoke(optionsObj);

        return services.AddNewHeapPlatformAspNetCommon(optionsObj);
    }

    public static NewHeapPlatformAspNetCommonConfigurator AddNewHeapPlatformAspNetCommon(this IServiceCollection services, NewHeapAspNetCommonOptions optionsObj)
    {
        //Must register the options object as a singleton so it can be injected into the DbContext etc.
        services.AddSingleton(optionsObj);
        var commonConfigurator = services.AddNewHeapPlatformCommon(optionsObj.CommonOptions);

        return new NewHeapPlatformAspNetCommonConfigurator(services, commonConfigurator, optionsObj);
    }

    public static IApplicationBuilder UseNewHeapPlatformAspNetCommon(this IApplicationBuilder app)
    {
        return app;
    }
}