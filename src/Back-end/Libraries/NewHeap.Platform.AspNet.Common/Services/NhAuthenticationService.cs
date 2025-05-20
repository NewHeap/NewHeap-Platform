using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using NewHeap.Platform.AspNet.Common.Authentication;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Exceptions;
using NewHeap.Platform.AspNet.Common.Models;
using NewHeap.Platform.Common.Identity.Claims;
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
    TDivision,
    TDivisionUser,
    TDivisionRole,
    TDivisionUserRole,
    TDivisionRoleClaim
    > : INhAuthenticationService
    where TUser : NhUser<TDivision, TDivisionUser, TDivisionUserRole, TDivisionRole, TDivisionRoleClaim, TUser>
    where TDivision : NhDivision<TDivisionUser, TDivisionUserRole, TDivisionRole, TDivisionRoleClaim, TDivision, TUser>
    where TDivisionRole : NhDivisionRole<TDivisionUserRole, TDivisionRoleClaim, TDivisionUser, TDivisionRole, TDivision, TUser>
    where TDivisionUser : NhDivisionUser<TDivisionUserRole, TDivisionUser, TDivisionRole, TDivisionRoleClaim, TDivision, TUser>
    where TDivisionUserRole : NhDivisionUserRole<TDivisionUser, TDivisionRole, TDivisionRoleClaim, TDivisionUserRole, TDivision, TUser>
    where TDivisionRoleClaim : NhDivisionRoleClaim
{
    protected readonly SignInManager<TUser> _signInManager;
    protected readonly INhUserManager<TUser> _userManager;
    protected readonly ILogger<AuthenticationService> _logger;
    protected readonly IConfiguration _configuration;
    protected readonly TokenValidationParameters _tokenValidationParameters;
    protected readonly AuthenticationConfiguration _authConfiguration;

    public NhAuthenticationService(
        SignInManager<TUser> signInManager,
        INhUserManager<TUser> userManager,
        ILogger<AuthenticationService> logger,
        IConfiguration configuration,
        TokenValidationParameters tokenValidationParameters,
        AuthenticationConfiguration authConfiguration
    )
    {
        _signInManager = signInManager;
        _userManager = userManager;
        _logger = logger;
        _configuration = configuration;
        _tokenValidationParameters = tokenValidationParameters;
        _authConfiguration = authConfiguration;
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

        return await RefreshToken(user);
    }

    protected virtual async Task<TaskResult<UserToken>> RefreshToken(TUser user)
    {
        user.RefreshToken = GenerateRefreshToken();
        await _userManager.UpdateAsync(user);

        var token = await CreateToken(
            user.Id,
            withDivisionClaims: _authConfiguration.DivisionsEnabled
        );

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

        return await Authenticate(user, request, requiredClaims);
    }

    protected virtual async Task<TaskResult<UserToken>> Authenticate(TUser user, AuthenticateRequest request, IEnumerable<Claim>? requiredClaims = null)
    {
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
        var token = await CreateToken(
            user.Id,
            withDivisionClaims: _authConfiguration.DivisionsEnabled
        );

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
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user == null)
        {
            throw new InvalidOperationException("Invalid user id");
        }
        
        var claims = await _userManager.GetValidClaims(user!, withDivisionClaims);
        return await CreateToken(userId, claims, expiration);
    }

    public virtual async Task<JwtSecurityToken> CreateToken(Guid userId, IEnumerable<Claim> c, TimeSpan? expiration = null)
    {
        if (
            string.IsNullOrWhiteSpace(GetTokenKey())
            || string.IsNullOrWhiteSpace(GetIssuer())
        )
        {
            throw new ConfigurationException("Missing JWT configuration");
        }
        var claims = c.ToList();
        var tokenKey = GetTokenKey();
        var issuer = GetIssuer();

        expiration ??= TimeSpan.FromDays(1);

        if (!claims.Any(x => x.Type != ClaimTypes.NameIdentifier))
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, userId.ToString()));
        }
        
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

    public virtual async Task<TaskResult<UserToken>> Impersonate(Guid currentUserId, ImpersonateRequest request)
    {
        var currentUser = await _userManager.FindByIdAsync(currentUserId.ToString());
        if (currentUser == null)
        {
            return TaskResult<UserToken>.Failed("Invalid request");
        }

        if (!request.UserId.HasValue)
        {
            return TaskResult<UserToken>.Failed("Invalid request");
        }

        var impersonateUser = await _userManager.FindByIdAsync(request.UserId!.Value.ToString());
        if (impersonateUser == null)
        {
            return TaskResult<UserToken>.Failed("Invalid request");
        }

        return await Impersonate(currentUser, impersonateUser);
    }

    protected virtual async Task<TaskResult<UserToken>> Impersonate(TUser currentUser, TUser user)
    {
        var claims = await _userManager.GetValidClaims(user!, _authConfiguration.DivisionsEnabled);
        claims.Add(new Claim(NhPlatformClaimTypes.ImpersontateOriginUserId, currentUser.Id.ToString()));
        var token = await CreateToken(
            user.Id, 
            c: claims, 
            expiration: null
        );

        return new UserToken(
            new JwtSecurityTokenHandler().WriteToken(token), 
            token.ValidTo, 
            user.RefreshToken,
            token.Issuer
        );
    }

    public virtual async Task<TaskResult<UserToken>> ImpersonateRevert(Guid impersonatedUserId, Guid originUserId)
    {
        var impersonatedUser = await _userManager.FindByIdAsync(impersonatedUserId.ToString());
        if (impersonatedUser == null)
        {
            return TaskResult<UserToken>.Failed("Invalid request");
        }

        var originUser = await _userManager.FindByIdAsync(originUserId.ToString());
        if (originUser == null)
        {
            return TaskResult<UserToken>.Failed("Invalid request");
        }

        return await Impersonate(impersonatedUser, originUser);
    }

    protected virtual async Task<TaskResult<UserToken>> ImpersonateRevert(TUser impersonatedUser, TUser originUser)
    {
        var claims = await _userManager.GetValidClaims(originUser!, _authConfiguration.DivisionsEnabled);
        var token = await CreateToken(
            originUser.Id,
            c: claims,
            expiration: null
        );

        return new UserToken(
            new JwtSecurityTokenHandler().WriteToken(token),
            token.ValidTo,
            originUser.RefreshToken,
            token.Issuer
        );
    }
}