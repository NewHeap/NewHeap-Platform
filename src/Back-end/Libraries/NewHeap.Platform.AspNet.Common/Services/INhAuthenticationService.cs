using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Exceptions;
using NewHeap.Platform.AspNet.Common.Models;
using NewHeap.Platform.Common.Models;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace NewHeap.Platform.AspNet.Common.Services;

/// <summary>
/// 
/// </summary>
public interface INhAuthenticationService
{
    /// <summary>
    /// Refresh authentication token
    /// </summary>
    /// <param name="request">Refresh token to validate</param>
    /// <returns>A new token when refresh token is valid</returns>
    Task<TaskResult<UserToken>> RefreshToken(RefreshTokenRequest request);

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    string GetIssuer();
    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    string GetIssuerDomain();

    /// <summary>
    /// Authenticate a user using username and password
    /// </summary>
    /// <param name="request">Credentials to verify</param>
    /// <param name="requiredClaims">
    /// Collection of claims that the user must have for authentication to succeed.
    /// When a user doesn't have all required claims, authentication will fail.
    /// If null, no claims are required.
    /// </param>
    /// <returns></returns>
    Task<TaskResult<UserToken>> Authenticate(AuthenticateRequest request,
        IEnumerable<Claim>? requiredClaims = null);

    /// <summary>
    /// Create a JWT for a specific user
    /// </summary>
    /// <param name="user"></param>
    /// <param name="expiration">Duration token is valid for. Defaults to 1 day</param>
    /// <returns></returns>
    /// <exception cref="ConfigurationException">Throws when JWT configuration is missing</exception>
    Task<JwtSecurityToken> CreateToken(NhUser user, TimeSpan? expiration = null);

    JwtSecurityToken? DecodeToken(string token);
}