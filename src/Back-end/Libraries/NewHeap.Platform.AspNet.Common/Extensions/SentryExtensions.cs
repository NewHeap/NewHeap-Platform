using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Microsoft.AspNetCore.Hosting;

public static class NhSentryExtensions
{
    public static IWebHostBuilder UseNewHeapSentry(this IWebHostBuilder builder, Action<SentryOptions>? optionsAction = null)
    {
        Action<SentryOptions> defaultOptionsAction = options =>
        {
            // Add default options
            optionsAction?.Invoke(options);
            // Add hard overrides
        };

        builder.UseSentry(defaultOptionsAction);

        return builder;
    }

    public static IHostApplicationBuilder UseNewHeapSentry(this WebApplicationBuilder builder, Action<SentryOptions>? optionsAction = null)
    {
        builder.WebHost.UseNewHeapSentry(optionsAction);

        return builder;
    }
}
