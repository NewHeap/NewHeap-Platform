using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using System.Text;

namespace NewHeap.Platform.AspNet.Common.Extensions;

public static class TokenValidationParameterExtensions
{
    /// <summary>
    /// Configures the token validation parameters for JWT bearer authentication
    /// </summary>
    /// <param name="cfg"></param>
    /// <param name="configuration"></param>
    public static void ConfigureNhJwtBearerValidationOptions(this TokenValidationParameters cfg,
        IConfiguration configuration)
    {
        cfg.ValidIssuer = configuration["NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:Issuer"];
        cfg.ValidAudience = configuration["NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:Issuer"];
        cfg.IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(
            configuration["NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:Key"]!));
        cfg.ValidateLifetime = true;
        cfg.ClockSkew = TimeSpan.Zero;
    }
}