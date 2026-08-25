using Hangfire;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AspNet.Common.Builders;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Middlewares;
using NewHeap.Platform.AspNet.Common.Models.Options;
using NewHeap.Platform.AspNet.Common.Models.View;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NewHeap.Platform.AspNet.Common.Utilities;

namespace NewHeap.Platform.AspNet.Common;

public partial class NewHeapPlatformAspNetCommonApplicationBuilderOptions
{
    public static NewHeapPlatformAspNetCommonOptionsBuilder Builder => new();
    
    public bool UseHsts { get; set; } = true;
    public bool UseHttpsRedirection { get; set; } = true;
    public bool DisableHeaderDisallowNoFollowMiddleware { get; set; } = false;
    public Action<CorsPolicyBuilder>? CorsPolicyBuilderAction { get; set; }
    public Action<IRouteBuilder>? MvcConfigureRoutesAction { get; set; }
    public Action<IEndpointRouteBuilder>? EndpointRouteConfigureAction { get; set; }
    public Action? AddMiddlewaresAction { get; set; }
}

public class NewHeapPlatformAspNetCommonOptionsBuilder
{
    private NewHeapPlatformAspNetCommonApplicationBuilderOptions? _options;

    public NewHeapPlatformAspNetCommonOptionsBuilder()
    {
        _options = new NewHeapPlatformAspNetCommonApplicationBuilderOptions();
    }
    public NewHeapPlatformAspNetCommonOptionsBuilder UseHsTs(bool use = true)
    {
        ThrowIfBuild();
        _options!.UseHsts = use;
        return this;
    }

    public NewHeapPlatformAspNetCommonOptionsBuilder UseHttpsRedirection(bool use = true)
    {
        ThrowIfBuild();
        _options!.UseHttpsRedirection = use;
        return this;
    }

    public NewHeapPlatformAspNetCommonOptionsBuilder DisableHeaderDisallowNoFollowMiddleware(bool disable = true)
    {
        ThrowIfBuild();
        _options!.DisableHeaderDisallowNoFollowMiddleware = disable;
        return this;
    }

    public NewHeapPlatformAspNetCommonOptionsBuilder UseCors(Action<CorsPolicyBuilder> action)
    {
        ThrowIfBuild();
        _options!.CorsPolicyBuilderAction = action;
        return this;
    }

    public NewHeapPlatformAspNetCommonOptionsBuilder UseMvc(Action<IRouteBuilder> action)
    {
        ThrowIfBuild();
        _options!.MvcConfigureRoutesAction = action;
        return this;
    }

    public NewHeapPlatformAspNetCommonOptionsBuilder UseEndpoints(Action<IEndpointRouteBuilder> action)
    {
        ThrowIfBuild();
        _options!.EndpointRouteConfigureAction = action;
        return this;
    }

    public NewHeapPlatformAspNetCommonOptionsBuilder UseMiddlewares(Action action)
    {
        ThrowIfBuild();
        _options!.AddMiddlewaresAction = action;
        return this;
    }

    private void ThrowIfBuild()
    {
        if (_options == null)
        {
            throw new InvalidOperationException("The options have already been built.");
        }
    }

    public NewHeapPlatformAspNetCommonApplicationBuilderOptions Build()
    {
        var options = _options;
        _options = null;
        return options!;
    }
}

public class NewHeapPlatformAspNetCommonApplicationBuilder
{
    private readonly IApplicationBuilder _applicationBuilder;
    private readonly NewHeapAspNetCommonSettings _aspNetCommonSettings;
    private readonly IWebHostEnvironment _env;
    private readonly NewHeapPlatformAspNetCommonApplicationBuilderOptions _options;
    private readonly IServiceProvider _services;

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
        var startupClasses = services.GetServices<IStartupConfiguration>();
        foreach (var configuration in startupClasses)
        {
            configuration.Configure(_applicationBuilder, services);
        }
    }

    private void ConfigureDefault()
    {
        _applicationBuilder.UseNewHeapTraceIdentifier();

        ConfigureHealthEndpoints();
        ConfigureCors();
        
        _applicationBuilder.UseAuthentication();

        if (_options.UseHsts)
        {
            _applicationBuilder.UseHsts();
        }

        if (_options.UseHttpsRedirection)
        {
            _applicationBuilder.UseHttpsRedirection();
        }

        //Note, the localization middleware must be configured before any middleware which might check the request culture
        IOptions<RequestLocalizationOptions>? requestLocalizationOptions = _applicationBuilder.ApplicationServices
            .GetRequiredService<IOptions<RequestLocalizationOptions>>();
        _applicationBuilder.UseRequestLocalization(requestLocalizationOptions.Value);

        if (!_options.DisableHeaderDisallowNoFollowMiddleware)
        {
            _applicationBuilder.UseMiddleware<ResponseHeaderDisallowNoFollowMiddleware>();
        }

        _options.AddMiddlewaresAction?.Invoke();

        _applicationBuilder.UseMvc(options =>
        {
            _options.MvcConfigureRoutesAction?.Invoke(options);
        });

        _applicationBuilder.UseRouting();
        _applicationBuilder.UseAuthorization();

        _applicationBuilder.UseEndpoints(endpoints =>
        {
            endpoints.MapControllers();

            var backgroundOperationMarker = _services.GetService<NhBackgroundOperationSignalRMarker>();
            if (backgroundOperationMarker is not null)
            {
                var backgroundOperationOptions = _services.GetRequiredService<NhBackgroundOperationsOptions>();
                endpoints.MapHub<NhBackgroundOperationHub>(backgroundOperationOptions.HubPath);
            }

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
                .WithExposedHeaders("Content-Disposition", TraceIdentifierMiddleware.CorrelationIdHeaderName)
                .SetPreflightMaxAge(TimeSpan.FromMinutes(10))
                .WithOrigins(_aspNetCommonSettings.AllowedOrigins);

            _options.CorsPolicyBuilderAction?.Invoke(builder);
        });
    }

    public NewHeapPlatformAspNetCommonApplicationBuilder UseStaticFiles(Action<StaticFileOptions>? options = null)
    {
        StaticFileOptions? staticFileOptions = new();
        options?.Invoke(staticFileOptions);

        _applicationBuilder.UseStaticFiles(staticFileOptions);

        return this;
    }

    public NewHeapPlatformAspNetCommonApplicationBuilder UseExceptionHandler(
        Action<ExceptionHandlerOptions>? options = null)
    {
        ExceptionHandlerOptions? exceptionHandlerOptions = new();

        exceptionHandlerOptions.ExceptionHandler = async context =>
        {
            var exceptionHandlerPathFeature =
                context.Features.Get<IExceptionHandlerPathFeature>();
            var exception = exceptionHandlerPathFeature?.Error;

            var handler = context.RequestServices.GetRequiredService<ExceptionHandlerService>();
            await handler.HandleExceptionAsync(context, exception);
        };

        options?.Invoke(exceptionHandlerOptions);

        _applicationBuilder.UseExceptionHandler(exceptionHandlerOptions);

        return this;
    }

    public NewHeapPlatformAspNetCommonApplicationBuilder UseHangfireDashboard(
        string pathMatch = "/hangfire",
        Action<DashboardOptions>? optionsAction = null,
        JobStorage? storage = null
    )
    {
        DashboardOptions? options = new();
        optionsAction?.Invoke(options);

        _applicationBuilder.UseHangfireDashboard(pathMatch, options, storage);

        return this;
    }

    public NewHeapPlatformAspNetCommonApplicationBuilder UseNhAuthentication<
        TUser,
        TDivision,
        TDivisionUser,
        TDivisionRole,
        TDivisionUserRole,
        TDivisionRoleClaim,
        TUserViewModel,
        TDivisionViewModel,
        TClaimViewModel
        >(Action<NhAuthenticationConfigurationBuilder<TUser, TDivision, TDivisionUser, TDivisionRole, TDivisionUserRole, TDivisionRoleClaim, TUserViewModel, TDivisionViewModel, TClaimViewModel>>? configure = null)
        where TUser : NhUser<TDivision, TDivisionUser, TDivisionUserRole, TDivisionRole, TDivisionRoleClaim, TUser>
        where TDivision : NhDivision<TDivisionUser, TDivisionUserRole, TDivisionRole, TDivisionRoleClaim, TDivision, TUser>
        where TDivisionRole : NhDivisionRole<TDivisionUserRole, TDivisionRoleClaim, TDivisionUser, TDivisionRole, TDivision, TUser>
        where TDivisionUser : NhDivisionUser<TDivisionUserRole, TDivisionUser, TDivisionRole, TDivisionRoleClaim, TDivision, TUser>
        where TDivisionUserRole : NhDivisionUserRole<TDivisionUser, TDivisionRole, TDivisionRoleClaim, TDivisionUserRole, TDivision, TUser>
        where TDivisionRoleClaim : NhDivisionRoleClaim
        where TUserViewModel : NhUserViewModel<TDivisionViewModel>
        where TDivisionViewModel : NhDivisionViewModel
        where TClaimViewModel : NhClaimViewModel
    {
        var builder = new NhAuthenticationConfigurationBuilder<TUser, TDivision, TDivisionUser, TDivisionRole, TDivisionUserRole, TDivisionRoleClaim, TUserViewModel, TDivisionViewModel, TClaimViewModel>();
        configure?.Invoke(builder);
        builder.Build(_applicationBuilder, _services);
        return this;
    }
}
