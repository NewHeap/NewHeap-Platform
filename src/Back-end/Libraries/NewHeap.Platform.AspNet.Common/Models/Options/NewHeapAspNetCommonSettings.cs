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

    public NhAspNetCommonOptionsBuilder(IConfiguration configuration)
    {
        _configuration = configuration;
        _options = new NewHeapAspNetCommonOptions
        {
            CommonOptions =
                new NewHeapCommonOptions
                {
                    SettingsAction = x => _configuration.GetSection("NewHeap:PlatformCommon:Settings").Bind(x)
                },
            SettingsAction = x => _configuration.GetSection("NewHeap:PlatformAspNetCommon:Settings").Bind(x),
            DbOptionsAction = x => x
                .UseSqlServer(_configuration.GetConnectionString("DefaultConnection")),
            JwtBearerOptionsTokenValidationParametersAction = options =>
            {
                options.ValidIssuer =
                    _configuration["NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:Issuer"];
                options.ValidAudience =
                    _configuration["NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:ValidAudience"];
                options.IssuerSigningKey = new SymmetricSecurityKey(
                    Encoding.UTF8.GetBytes(
                        _configuration["NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:Key"]!));
            },
            DbLogSettingsAction = x =>
                _configuration.GetSection("NewHeap:PlatformAspNetCommon:DbLogServiceSettings").Bind(x)
        };
    }
}