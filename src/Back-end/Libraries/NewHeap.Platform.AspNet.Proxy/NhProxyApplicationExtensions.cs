using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NewHeap.Platform.AspNet.Proxy;

/// <summary>Pipeline entry points for administration, redirects and managed/native YARP routes.</summary>
public static class NhProxyApplicationExtensions
{
    /// <summary>Installs literal redirects before proxy execution and maps native YARP endpoints.</summary>
    public static WebApplication UseNewHeapProxy(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        ((IApplicationBuilder)app).Map(NhProxyOptions.AdministrationPath, panel =>
        {
            panel.Use(async (context, next) =>
            {
                context.SetEndpoint(null);
                context.Items[typeof(NhProxyAdminController)] = true;
                context.Request.RouteValues = new RouteValueDictionary();
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; img-src data:; style-src 'unsafe-inline'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
                var options = context.RequestServices.GetRequiredService<IOptions<NhProxyOptions>>().Value;
                if (!context.Request.IsHttps && !app.Environment.IsDevelopment())
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsync("Administration requires HTTPS.");
                    return;
                }

                if (string.IsNullOrWhiteSpace(options.Administrator.UserName) || string.IsNullOrWhiteSpace(options.Administrator.PasswordHash))
                {
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    await context.Response.WriteAsync("Administration is unavailable. Configure the administrator account in the host settings.");
                    return;
                }

                try
                {
                    var administration = context.RequestServices.GetRequiredService<INhProxyAdministrationService>();
                    var access = await administration.CheckIpAccessAsync(context, context.RequestAborted);
                    if (!access.Success)
                    {
                        if (HttpMethods.IsPost(context.Request.Method) && context.Request.Path.Equals(new PathString("/Login")))
                        {
                            await administration.SignInAsync(context, new NhProxyLoginRequest { UserName = "", Password = "" }, context.RequestAborted);
                        }

                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        await context.Response.WriteAsync("Administration is not available from this address.");
                        return;
                    }

                    await next(context);
                }
                catch (Exception exception) when (!context.Response.HasStarted && !context.RequestAborted.IsCancellationRequested)
                {
                    context.RequestServices.GetRequiredService<ILogger<NhProxyAdminController>>().LogError(exception, "Proxy administration request failed.");
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    await context.Response.WriteAsync("Administration is temporarily unavailable. Please try again.");
                }
            });
            panel.UseRouting();
            panel.Use(async (context, next) =>
            {
                if (context.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>()?.ControllerTypeInfo.AsType() != typeof(NhProxyAdminController))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                await next(context);
            });
            panel.UseAuthentication();
            panel.UseAuthorization();
            panel.UseEndpoints(endpoints => endpoints.MapAreaControllerRoute("newheap-proxy", "NewHeapProxy", "{action=Index}/{id?}",
                new { controller = "NhProxyAdmin" }));
        });
        app.UseMiddleware<NhProxyRedirectMiddleware>();
        // Explicit routing keeps redirects ahead of route selection, including ambiguous rewrite matches.
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapNewHeapProxy();

        return app;
    }

    /// <summary>Maps native YARP endpoints. UseNewHeapProxy installs the administration and redirect middleware.</summary>
    public static IEndpointRouteBuilder MapNewHeapProxy(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapReverseProxy();

        return endpoints;
    }
}
