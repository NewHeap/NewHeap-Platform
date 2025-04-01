using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Exceptions;
using NewHeap.Platform.AspNet.Common.Models;
using NewHeap.Platform.Common.Models;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace NewHeap.Platform.AspNet.Common.Services;

/// <summary>
/// Service for authenticating users
/// </summary>
public class NhAuthenticationService<
    TUser,
    TLogMessageArgument,
    TLogMessageTranslated,
    TLogFile,
    TDivision,
    TDivisionUser,
    TDivisionRole,
    TDivisionUserRole,
    TDivisionRoleClaim
    > : INhAuthenticationService
    where TUser : NhUser<TDivision, TDivisionUser, TDivisionUserRole, TDivisionRole, TDivisionRoleClaim, TUser>
    where TLogMessageArgument : NhLogMessageArgument, new()
    where TLogMessageTranslated : NhLogMessageTranslated, new()
    where TLogFile : NhLogFile, new()
    where TDivision : NhDivision<TDivisionUser, TDivisionUserRole, TDivisionRole, TDivisionRoleClaim, TDivision, TUser>
    where TDivisionRole : NhDivisionRole<TDivisionUserRole, TDivisionRoleClaim, TDivisionUser, TDivisionRole, TDivision, TUser>
    where TDivisionUser : NhDivisionUser<TDivisionUserRole, TDivisionUser, TDivisionRole, TDivisionRoleClaim, TDivision, TUser>
    where TDivisionUserRole : NhDivisionUserRole<TDivisionUser, TDivisionRole, TDivisionRoleClaim, TDivisionUserRole, TDivision, TUser>
    where TDivisionRoleClaim : NhDivisionRoleClaim
{
    private readonly SignInManager<TUser> _signInManager;
    private readonly INhUserManager<TUser> _userManager;
    private readonly ILogger<AuthenticationService> _logger;
    private readonly IConfiguration _configuration;
    private readonly TokenValidationParameters _tokenValidationParameters;

    public NhAuthenticationService(
        SignInManager<TUser> signInManager,
        INhUserManager<TUser> userManager,
        ILogger<AuthenticationService> logger,
        IConfiguration configuration,
        TokenValidationParameters tokenValidationParameters
    )
    {
        _signInManager = signInManager;
        _userManager = userManager;
        _logger = logger;
        _configuration = configuration;
        _tokenValidationParameters = tokenValidationParameters;
    }

    /// <summary>
    /// Refresh authentication token
    /// </summary>
    /// <param name="request">Refresh token to validate</param>
    /// <returns>A new token when refresh token is valid</returns>
    public virtual async Task<TaskResult<UserToken>> RefreshToken(RefreshTokenRequest request)
    {
        var user = await _userManager.FindByEmailAsync(request.UserName);
        if (user == null)
        {
            return TaskResult<UserToken>.Failed("Invalid refresh token");
        }

        if (user.RefreshToken != request.RefreshToken)
        {
            return TaskResult<UserToken>.Failed("Invalid refresh token");
        }

        user.RefreshToken = GenerateRefreshToken();
        await _userManager.UpdateAsync(user);

        var token = await CreateToken(user.Id);

        return new UserToken(new JwtSecurityTokenHandler().WriteToken(token), token.ValidTo, user.RefreshToken,
            token.Issuer);
    }

    /// <summary>
    /// Get issuer for jwt
    /// </summary>
    /// <returns></returns>
    public virtual string GetIssuer()
    {
        return _configuration["NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:Issuer"]!;
    }
    
    /// <summary>
    /// Get key used to sign jwt
    /// </summary>
    /// <returns></returns>
    protected virtual string GetTokenKey()
    {
        return _configuration["NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:Key"]!;
    }

    /// <summary>
    /// Get domain of issuer
    /// </summary>
    /// <returns></returns>
    public virtual string GetIssuerDomain()
    {
        return new Uri(GetIssuer()).Host;
    }

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
    public virtual async Task<TaskResult<UserToken>> Authenticate(AuthenticateRequest request,
        IEnumerable<Claim>? requiredClaims = null)
    {
        var user = await _userManager.FindByEmailAsync(request.UserName);
        if (user == null)
        {
            return TaskResult<UserToken>.Failed("Unknown user");
        }

        var result = await _signInManager.CheckPasswordSignInAsync(user, request.Password, true);

        if (result.IsLockedOut || result.IsNotAllowed)
        {
            return TaskResult<UserToken>.Failed("User locked out");
        }

        if (!result.Succeeded)
        {
            _logger.LogInformation("Failed login attempt for user {user}", user.UserName);
            return TaskResult<UserToken>.Failed("Invalid password");
        }

        var refreshToken = GenerateRefreshToken();

        user.RefreshToken = refreshToken;
        await _userManager.UpdateAsync(user);

        _logger.LogInformation("User {user} logged in", user.UserName);
        var token = await CreateToken(user.Id);

        if (requiredClaims != null)
        {
            foreach (var claim in requiredClaims)
            {
                if (!token.Claims.Any(x => x.Type == claim.Type && x.Value == claim.Value))
                {
                    return TaskResult<UserToken>.Failed("Unauthorized");
                }
            }
        }

        return new UserToken(new JwtSecurityTokenHandler().WriteToken(token), token.ValidTo, refreshToken,
            token.Issuer);
    }

    /// <summary>
    /// Generate a random refresh token
    /// </summary>
    /// <returns></returns>
    protected string GenerateRefreshToken()
    {
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        var refreshToken = Convert.ToBase64String(bytes);
        return refreshToken;
    }

    /// <summary>
    /// Create a JWT for a specific user
    /// </summary>
    /// <param name="userId"></param>
    /// <param name="expiration">Duration token is valid for. Defaults to 1 day</param>
    /// <param name="withDivisionClaims">Default false</param>
    /// <returns></returns>
    /// <exception cref="ConfigurationException">Throws when JWT configuration is missing</exception>
    public virtual async Task<JwtSecurityToken> CreateToken(Guid userId, TimeSpan? expiration = null, bool withDivisionClaims = false)
    {
        if (
            string.IsNullOrWhiteSpace(GetTokenKey())
            || string.IsNullOrWhiteSpace(GetIssuer())
        )
        {
            throw new ConfigurationException("Missing JWT configuration");
        }

        var tokenKey = GetTokenKey();
        var issuer = GetIssuer();

        expiration ??= TimeSpan.FromDays(1);

        var user = await _userManager.FindByIdAsync(userId.ToString());
        var claims = await _userManager.GetValidClaims(user!, withDivisionClaims);
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(tokenKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(issuer,
            issuer,
            claims,
            expires: DateTime.Now.Add(expiration.Value).ToUniversalTime(),
            notBefore: DateTime.Now.ToUniversalTime(),
            signingCredentials: creds);
        return token;
    }

    /// <summary>
    /// Validate and decode a JWT token
    /// </summary>
    /// <param name="token"></param>
    /// <returns></returns>
    public virtual JwtSecurityToken? DecodeToken(string token)
    {
        var handler = new JwtSecurityTokenHandler();
        var claims = handler.ValidateToken(token, _tokenValidationParameters, out var t);
        return t as JwtSecurityToken;
    }
}