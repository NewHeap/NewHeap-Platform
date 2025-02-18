using AutoMapper;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.IdentityModel.Tokens;
using NewHeap.Platform.Common.Models.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NewHeap.Platform.AspNet.Common.Models.Options;

public class NewHeapAspNetCommonOptions
{
    public required NewHeapCommonOptions CommonOptions { get; set; }
    public required Action<NewHeapAspNetCommonSettings> SettingsAction { get; set; }
    public required Action<DbLogSettings> DbLogSettingsAction { get; set; }
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
