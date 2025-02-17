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
    public static IServiceCollection AddSiersScheduleConnector(this IServiceCollection services, Action<NewHeapCommonSettings> options)
    {
        if (services == null) throw new ArgumentNullException(nameof(services));
        if (options == null) throw new ArgumentNullException(nameof(options));

        var optionsObj = new NewHeapCommonSettings();
        options.Invoke(optionsObj);

        //Must register the options object as a singleton so it can be injected into the DbContext etc.
        services.AddSingleton(optionsObj);

        services.AddScoped<LogHelperService>();
        services.AddScoped<ValidationService>();

        // Optioneel maken
        services.AddScoped<MailService>();
        services.AddScoped<MicrosoftAuthService>();

        return services;
    }
}