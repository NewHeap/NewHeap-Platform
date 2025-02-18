using System;
using System.IO;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.Models.Options;
using NewHeap.Platform.Common.Extensions;
using NewHeap.Platform.Common.Models.Options;
using NewHeap.Platform.Common.Services;
using StackExchange.Utils;

namespace NewHeap.Platform.AspNet.Common.Extensions;
public static partial class ServiceCollectionExtensions
{
    public static NewHeapPlatformAspNetCommonConfigurator<TDbContext> AddNewHeapPlatformAspNetCommon<TDbContext>(
        this IServiceCollection services, 
        NewHeapAspNetCommonOptions optionsObj
        )
        where TDbContext : NhDbContext
    {
        if (services == null) throw new ArgumentNullException(nameof(services));
        if (optionsObj == null) throw new ArgumentNullException(nameof(optionsObj));

        var commonConfigurator = services.AddNewHeapPlatformCommon(optionsObj.CommonOptions);

        return new NewHeapPlatformAspNetCommonConfigurator<TDbContext>(services, commonConfigurator, optionsObj);
    }

    public static NewHeapPlatformAspNetCommonApplicationBuilder UseNewHeapPlatformAspNetCommon(
        this IApplicationBuilder app, 
        IWebHostEnvironment env, 
        IServiceProvider serviceProvider, 
        NewHeapPlatformAspNetCommonApplicationBuilderOptions options)
    {
        return new NewHeapPlatformAspNetCommonApplicationBuilder(app, env, serviceProvider, options);
    }
}