using Hangfire;
using Hangfire.Console;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Middlewares;
using NewHeap.Platform.AspNet.Common.Models.Options;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.AspNet.Policy.AuthorizationHandlers;
using NewHeap.Platform.AspNet.Policy.Requirements;
using NewHeap.Platform.Common;
using NewHeap.Platform.Common.Translations;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;

namespace NewHeap.Platform.AspNet.Common;

public partial class NewHeapPlatformAspNetCommonApplicationBuilderOptions
{ 
    public bool UseHsts { get; set; } = true;
    public bool UseHttpsRedirection { get; set; } = true;
    public Action<CorsPolicyBuilder>? CorsPolicyBuilderAction { get; set; }
    public Action<IRouteBuilder>? MvcConfigureRoutesAction { get; set; }
    public Action<IEndpointRouteBuilder>? EndpointRouteConfigureAction { get; set; }
}

public partial class NewHeapPlatformAspNetCommonApplicationBuilder
{
    private readonly IApplicationBuilder _applicationBuilder;
    private readonly IWebHostEnvironment _env;
    private readonly IServiceProvider _services;
    private readonly NewHeapPlatformAspNetCommonApplicationBuilderOptions _options;
    private readonly NewHeapAspNetCommonSettings _aspNetCommonSettings;

    public NewHeapPlatformAspNetCommonApplicationBuilder(
        IApplicationBuilder applicationBuilder,
        IWebHostEnvironment env,
        IServiceProvider services,
        NewHeapPlatformAspNetCommonApplicationBuilderOptions options
    )
    {
        _applicationBuilder = applicationBuilder;
        _env = env;
        _services = services;
        _options = options;
        _aspNetCommonSettings = services.GetRequiredService<IOptions<NewHeapAspNetCommonSettings>>().Value;

        ConfigureDefault();
    }

    private void ConfigureDefault()
    {
        ConfigureHealthEndpoints();
        ConfigureCors();

        _applicationBuilder.UseAuthentication();

        if(_options.UseHsts)
        {
            _applicationBuilder.UseHsts();
        }

        if (_options.UseHttpsRedirection)
        {
            _applicationBuilder.UseHttpsRedirection();
        }

        //Note, the localization middleware must be configured before any middleware which might check the request culture
        var requestLocalizationOptions = _applicationBuilder.ApplicationServices.GetRequiredService<IOptions<RequestLocalizationOptions>>();
        _applicationBuilder.UseRequestLocalization(requestLocalizationOptions.Value);

        _applicationBuilder.UseMiddleware<ResponseHeaderDisallowNoFollowMiddleware>();

        _applicationBuilder.UseMvc(options => {
            _options.MvcConfigureRoutesAction?.Invoke(options);
        });

        _applicationBuilder.UseRouting();
        _applicationBuilder.UseAuthorization();

        _applicationBuilder.UseEndpoints(endpoints =>
        {
            endpoints.MapControllers();

            _options.EndpointRouteConfigureAction?.Invoke(endpoints);
        });
    }

    private void ConfigureHealthEndpoints()
    {
        _applicationBuilder.UseHealthChecks("/health");
    }

    private void ConfigureCors()
    {
        _applicationBuilder.UseCors(builder =>
        {
            builder
               .AllowAnyHeader()
               .AllowAnyMethod()
               .AllowCredentials()
                .WithExposedHeaders("Content-Disposition")
               .SetPreflightMaxAge(TimeSpan.FromMinutes(10))
               .WithOrigins(_aspNetCommonSettings.AllowedOrigins);

            _options.CorsPolicyBuilderAction?.Invoke(builder);
        });
    }

    public NewHeapPlatformAspNetCommonApplicationBuilder UseStaticFiles(Action<StaticFileOptions>? options = null)
    {
        var staticFileOptions = new StaticFileOptions();
        options?.Invoke(staticFileOptions);

        _applicationBuilder.UseStaticFiles(staticFileOptions);

        return this;
    }

    public NewHeapPlatformAspNetCommonApplicationBuilder UseExceptionHandler(Action<ExceptionHandlerOptions>? options = null)
    {
        var exceptionHandlerOptions = new ExceptionHandlerOptions();

        exceptionHandlerOptions.ExceptionHandler = async context =>
        {
            var exceptionHandlerPathFeature = context.Features.Get<IExceptionHandlerPathFeature>();
            var exception = exceptionHandlerPathFeature?.Error;

            var handler = context.RequestServices.GetRequiredService<ExceptionHandlerService>();
            await handler.HandleExceptionAsync(context, exception);
        };

        options?.Invoke(exceptionHandlerOptions);

        _applicationBuilder.UseExceptionHandler(exceptionHandlerOptions);

        return this;
    }

    public NewHeapPlatformAspNetCommonApplicationBuilder UserHangfireDashboard(
        string pathMatch = "/hangfire",
        Action<DashboardOptions>? optionsAction = null,
        JobStorage? storage = null
        )
    {
        var options = new DashboardOptions();
        optionsAction?.Invoke(options);

        _applicationBuilder.UseHangfireDashboard(pathMatch, options, storage);

        return this;
    }
}
