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

public class NewHeapAspNetCommonOptions
{
    public static NhAspNetCommonOptionsBuilder Builder(string connectionStringName, IConfiguration configuration,
        string organizationName)
        => new(connectionStringName, configuration, organizationName);

    public required NewHeapCommonOptions CommonOptions { get; set; }
    public required Action<NewHeapAspNetCommonSettings> SettingsAction { get; set; }
    public required Action<DbLogServiceSettings> DbLogSettingsAction { get; set; }
    public required Action<DbContextOptionsBuilder> DbOptionsAction { get; set; }
    public Action<IdentityOptions>? IdentityOptionsAction { get; set; }
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

public class NewHeapAspNetCommonSettings
{
    public string DefaultCulture { get; set; } = "";
    public string[] SupportedCultures { get; set; } = [];
    public string[] AllowedOrigins { get; set; } = [];
    public string SelfBaseUrl { get; set; } = "";
    public bool RecurringJobsEnabled { get; set; }
}

public class NhAspNetCommonOptionsBuilder
{
    private readonly IConfiguration _configuration;
    private NewHeapAspNetCommonOptions? _options;

    public NhAspNetCommonOptionsBuilder(string connectionStringName, IConfiguration configuration,
        string organizationName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationName);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionStringName);

        _configuration = configuration;
        _options = new NewHeapAspNetCommonOptions
        {
            CommonOptions =
                new NewHeapCommonOptions
                {
                    SettingsAction = x =>
                        _configuration.GetSection($"{organizationName}:PlatformCommon:Settings").Bind(x)
                },
            SettingsAction =
                x => _configuration.GetSection($"{organizationName}:PlatformAspNetCommon:Settings").Bind(x),
            DbOptionsAction = x => x
                .UseSqlServer(_configuration.GetConnectionString(connectionStringName)),
            JwtBearerOptionsTokenValidationParametersAction = options =>
            {
                options.ValidIssuer =
                    _configuration[$"{organizationName}:PlatformAspNetCommon:Authorization:JWT:Token:Issuer"];
                options.ValidAudience =
                    _configuration[$"{organizationName}:PlatformAspNetCommon:Authorization:JWT:Token:ValidAudience"];
                options.IssuerSigningKey = new SymmetricSecurityKey(
                    Encoding.UTF8.GetBytes(
                        _configuration[$"{organizationName}:PlatformAspNetCommon:Authorization:JWT:Token:Key"]!));
            },
            DbLogSettingsAction = x =>
                _configuration.GetSection($"{organizationName}:PlatformAspNetCommon:DbLogServiceSettings").Bind(x)
        };
    }

    public NhAspNetCommonOptionsBuilder WithCommonOptions(NewHeapCommonOptions options)
    {
        ThrowIfBuild();
        _options!.CommonOptions = options;
        return this;
    }

    public NhAspNetCommonOptionsBuilder ConfigureSettings(Action<NewHeapAspNetCommonSettings> settingsAction)
    {
        ThrowIfBuild();
        _options!.SettingsAction = settingsAction;
        return this;
    }

    public NhAspNetCommonOptionsBuilder ConfigureDbLogging(Action<DbLogServiceSettings> action)
    {
        ThrowIfBuild();
        _options!.DbLogSettingsAction = action;
        return this;
    }

    public NhAspNetCommonOptionsBuilder ConfigureDbContext(Action<DbContextOptionsBuilder> dbOptionsAction)
    {
        ThrowIfBuild();
        _options!.DbOptionsAction = dbOptionsAction;
        return this;
    }

    public NhAspNetCommonOptionsBuilder ConfigureIdentity(Action<IdentityOptions> action)
    {
        ThrowIfBuild();
        _options!.IdentityOptionsAction = action;
        return this;
    }

    public NhAspNetCommonOptionsBuilder ConfigureAuthentication(Action<AuthenticationOptions> action)
    {
        ThrowIfBuild();
        _options!.AuthenticationOptionsAction = action;
        return this;
    }

    public NhAspNetCommonOptionsBuilder ConfigureJwtBearerValidationOptions(Action<TokenValidationParameters> action)
    {
        ThrowIfBuild();
        _options!.JwtBearerOptionsTokenValidationParametersAction = action;
        return this;
    }

    public NhAspNetCommonOptionsBuilder ConfigureJwtBearer(Action<JwtBearerOptions> action)
    {
        ThrowIfBuild();
        _options!.JwtBearerOptionsAction = action;
        return this;
    }

    public NhAspNetCommonOptionsBuilder ConfigureAuthorization(Action<AuthorizationOptions> action)
    {
        ThrowIfBuild();
        _options!.AuthorizationOptionsAction = action;
        return this;
    }

    public NhAspNetCommonOptionsBuilder ConfigureLocalization(Action<LocalizationOptions> action)
    {
        ThrowIfBuild();
        _options!.LocalizationOptionsAction = action;
        return this;
    }

    public NhAspNetCommonOptionsBuilder ConfigureMvc(Action<MvcOptions> action)
    {
        ThrowIfBuild();
        _options!.MvcOptionsAction = action;
        return this;
    }

    public NhAspNetCommonOptionsBuilder ConfigureMvcDataAnnotationsLocalization(
        Action<MvcDataAnnotationsLocalizationOptions> action)
    {
        ThrowIfBuild();
        _options!.MvcDataAnnotationsLocalizationOptionsAction = action;
        return this;
    }

    public NhAspNetCommonOptionsBuilder ConfigureApiBehavior(Action<ApiBehaviorOptions> action)
    {
        ThrowIfBuild();
        _options!.ApiBehaviorOptionsAction = action;
        return this;
    }

    public NhAspNetCommonOptionsBuilder ConfigureCors(Action<CorsOptions> action)
    {
        ThrowIfBuild();
        _options!.CorsOptionsAction = action;
        return this;
    }

    public NhAspNetCommonOptionsBuilder ConfigureAutoMapper(Action<IMapperConfigurationExpression> action)
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