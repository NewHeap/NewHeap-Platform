using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Sentry.AspNetCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Microsoft.AspNetCore.Hosting;

public static class NhSentryExtensions
{
    public static IWebHostBuilder UseNewHeapSentry(this IWebHostBuilder builder, Action<SentryAspNetCoreOptions>? optionsAction = null)
    {
        Action<SentryAspNetCoreOptions> defaultOptionsAction = options =>
        {
            // Add default options
            options.Environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                ?? "Unknown";

            options.SendDefaultPii = true;
            options.MaxRequestBodySize = Sentry.Extensibility.RequestSize.Small;
            options.MinimumBreadcrumbLevel = Microsoft.Extensions.Logging.LogLevel.Debug;
            options.MinimumEventLevel = Microsoft.Extensions.Logging.LogLevel.Warning;
            options.AttachStacktrace = true;
            //options.Debug = true;
            options.DiagnosticLevel = SentryLevel.Error;
            options.TracesSampleRate = 1.0; // Adjust as needed for performance
            //options.Release = ""; // TODO;
            options.ServerName = Environment.MachineName;

            optionsAction?.Invoke(options);
            // Add hard overrides
        };

        builder.UseSentry(defaultOptionsAction);

        return builder;
    }

    public static IHostApplicationBuilder UseNewHeapSentry(this WebApplicationBuilder builder, Action<SentryAspNetCoreOptions>? optionsAction = null)
    {
        builder.WebHost.UseNewHeapSentry(optionsAction);

        return builder;
    }
}
