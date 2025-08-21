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
