using AutoMapper;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Localization;
using Microsoft.IdentityModel.Tokens;
using NewHeap.Platform.Common.Models.Options;
using System.Text;

namespace NewHeap.Platform.AspNet.Common.Models.Options;

public class NewHeapAspNetCommonSettings
{
    public string DefaultCulture { get; set; } = "";
    public string[] SupportedCultures { get; set; } = [];
    public string[] AllowedOrigins { get; set; } = [];
    public string SelfBaseUrl { get; set; } = "";
    public bool RecurringJobsEnabled { get; set; }
}

public class NewHeapAspNetCommonOptions
{
    public const string DefaultSettingsPrefix = "NewHeap:PlatformAspNetCommon";
    public static NewHeapAspNetCommonOptionsBuilder Builder(IConfiguration configuration)
        => new(configuration);

    public required NewHeapCommonOptions CommonOptions { get; set; }
    public required Action<NewHeapAspNetCommonSettings> SettingsAction { get; set; }
    public Action<AuthenticationOptions>? AuthenticationOptionsAction { get; set; }
    public required Action<TokenValidationParameters> JwtBearerOptionsTokenValidationParametersAction { get; set; }
    public Action<JwtBearerOptions>? JwtBearerOptionsAction { get; set; }
    public Action<AuthorizationOptions>? AuthorizationOptionsAction { get; set; }
    public Action<LocalizationOptions>? LocalizationOptionsAction { get; set; }
    public Action<MvcOptions>? MvcOptionsAction { get; set; }
    public Action<MvcDataAnnotationsLocalizationOptions>? MvcDataAnnotationsLocalizationOptionsAction { get; set; }
    public Action<ApiBehaviorOptions>? ApiBehaviorOptionsAction { get; set; }
    public Action<CorsOptions>? CorsOptionsAction { get; set; }
    public Action<IMapperConfigurationExpression>? AutoMapperConfigurationAction { get; set; }
}

public class NewHeapAspNetCommonOptionsBuilder
{
    private readonly IConfiguration _configuration;
    private NewHeapAspNetCommonOptions? _options;

    public NewHeapAspNetCommonOptionsBuilder(IConfiguration configuration)
    {
        _configuration = configuration;
        _options = new NewHeapAspNetCommonOptions
        {
            CommonOptions = NewHeapCommonOptions.Builder(configuration).Build(),
            SettingsAction =
                x => _configuration.GetSection($"{NewHeapAspNetCommonOptions.DefaultSettingsPrefix}:Settings").Bind(x),
            JwtBearerOptionsTokenValidationParametersAction = options =>
            {
                options.ValidIssuer =
                    _configuration[$"{NewHeapAspNetCommonOptions.DefaultSettingsPrefix}:Authorization:JWT:Token:Issuer"];
                options.ValidAudience =
                    _configuration[$"{NewHeapAspNetCommonOptions.DefaultSettingsPrefix}:Authorization:JWT:Token:ValidAudience"];
                options.IssuerSigningKey = new SymmetricSecurityKey(
                    Encoding.UTF8.GetBytes(
                        _configuration[$"{NewHeapAspNetCommonOptions.DefaultSettingsPrefix}:Authorization:JWT:Token:Key"]!));
            }
        };
    }

    public NewHeapAspNetCommonOptionsBuilder ConfgureCommonOptions(NewHeapCommonOptions options)
    {
        ThrowIfBuild();
        _options!.CommonOptions = options;
        return this;
    }

    public NewHeapAspNetCommonOptionsBuilder ConfigureSettings(Action<NewHeapAspNetCommonSettings> settingsAction)
    {
        ThrowIfBuild();
        _options!.SettingsAction = settingsAction;
        return this;
    }

    public NewHeapAspNetCommonOptionsBuilder ConfigureAuthentication(Action<AuthenticationOptions> action)
    {
        ThrowIfBuild();
        _options!.AuthenticationOptionsAction = action;
        return this;
    }

    public NewHeapAspNetCommonOptionsBuilder ConfigureJwtBearerValidationOptions(Action<TokenValidationParameters> action)
    {
        ThrowIfBuild();
        _options!.JwtBearerOptionsTokenValidationParametersAction = action;
        return this;
    }

    public NewHeapAspNetCommonOptionsBuilder ConfigureJwtBearer(Action<JwtBearerOptions> action)
    {
        ThrowIfBuild();
        _options!.JwtBearerOptionsAction = action;
        return this;
    }

    public NewHeapAspNetCommonOptionsBuilder ConfigureAuthorization(Action<AuthorizationOptions> action)
    {
        ThrowIfBuild();
        _options!.AuthorizationOptionsAction = action;
        return this;
    }

    public NewHeapAspNetCommonOptionsBuilder ConfigureLocalization(Action<LocalizationOptions> action)
    {
        ThrowIfBuild();
        _options!.LocalizationOptionsAction = action;
        return this;
    }

    public NewHeapAspNetCommonOptionsBuilder ConfigureMvc(Action<MvcOptions> action)
    {
        ThrowIfBuild();
        _options!.MvcOptionsAction = action;
        return this;
    }

    public NewHeapAspNetCommonOptionsBuilder ConfigureMvcDataAnnotationsLocalization(
        Action<MvcDataAnnotationsLocalizationOptions> action)
    {
        ThrowIfBuild();
        _options!.MvcDataAnnotationsLocalizationOptionsAction = action;
        return this;
    }

    public NewHeapAspNetCommonOptionsBuilder ConfigureApiBehavior(Action<ApiBehaviorOptions> action)
    {
        ThrowIfBuild();
        _options!.ApiBehaviorOptionsAction = action;
        return this;
    }

    public NewHeapAspNetCommonOptionsBuilder ConfigureCors(Action<CorsOptions> action)
    {
        ThrowIfBuild();
        _options!.CorsOptionsAction = action;
        return this;
    }

    public NewHeapAspNetCommonOptionsBuilder ConfigureAutoMapper(Action<IMapperConfigurationExpression> action)
    {
        ThrowIfBuild();
        _options!.AutoMapperConfigurationAction = action;
        return this;
    }


    public NewHeapAspNetCommonOptions Build()
    {
        var options = _options;
        _options = null;
        return options!;
    }

    private void ThrowIfBuild()
    {
        if (_options == null)
        {
            throw new InvalidOperationException("The options have already been built.");
        }
    }
}