using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
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
    /// Refresh/Claim/Authenticate authentication token
    /// </summary>
    /// <param name="request">Refresh token to validate</param>
    /// <returns>A new token when refresh token is valid</returns>
    public virtual async Task<TaskResult<UserToken>> AuthenticateRefreshTokenAsync(RefreshTokenRequest request)
    {
        var user = await FindUserByUsernameAsync(request.UserName);

        if (user == null)
        {
            return TaskResult<UserToken>.Failed("Invalid refresh token");
        }

        var repository = _userManager.GetRepository();
        await using var transaction = await repository.StartOrGetTransactionScopeAsync();

        try
        {
            var refreshTokens = repository.GetDbSet<NhUserAuthRefreshToken>();
            var now = DateTimeOffset.UtcNow;

            await NhRefreshTokenOperations.DeleteExpiredAsync(refreshTokens, user.Id, now);

            var consumed = await NhRefreshTokenOperations.TryConsumeAsync(
                refreshTokens,
                user.Id,
                request.RefreshToken,
                now);

            if (!consumed)
            {
                await transaction.RollbackAsync();
                return TaskResult<UserToken>.Failed("Invalid refresh token");
            }

            var createResult = await CreateRefreshTokenAsync(user);
            if (!createResult.Success)
            {
                await transaction.RollbackAsync();
                return TaskResult<UserToken>.Failed("Could not create refresh token");
            }

            var refreshTokenInfo = createResult.Data!;
            var token = await CreateToken(
                user.Id,
                withDivisionClaims: _authConfiguration.DivisionsEnabled,
                expiration: _authConfiguration.ExpirationTimespanToken);

            await transaction.CommitAsync();

            return CreateUserToken(
                token,
                refreshTokenInfo.RefreshToken,
                refreshTokenInfo.ExpirationDateTime.DateTime);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    protected virtual async Task<TaskResult<(string RefreshToken, DateTimeOffset ExpirationDateTime)>> CreateRefreshTokenAsync(TUser user)
    {
        var repo = _userManager.GetRepository()
            .GetDbSet<NhUserAuthRefreshToken>();

        var token = await CreateToken(
            user.Id,
            withDivisionClaims: _authConfiguration.DivisionsEnabled,
            expiration: _authConfiguration.ExpirationTimespanRefreshToken
        );

        DateTimeOffset expirationDateTimeOffset = token.ValidTo;

        var refreshToken = new NhUserAuthRefreshToken()
        {
            UserId = user.Id,
            Token = GenerateRefreshToken(),
            ExpiryDateTime = expirationDateTimeOffset
        };

        await repo.AddAsync(refreshToken);
        await _userManager.GetRepository().SaveChangesAsync();

        // Cleanup old token via ExecuteDeleteAsync
        await NhRefreshTokenOperations.DeleteExpiredAsync(
            repo,
            user.Id,
            DateTimeOffset.UtcNow);

        return TaskResult<(string RefreshToken, DateTimeOffset ExpirationDateTime)>.Succeeded(
            (refreshToken.Token, refreshToken.ExpiryDateTime)
        );
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

    protected virtual Task<TUser?> FindUserByUsernameAsync(string username)
    {
        return _userManager.FindByNameAsync(username);
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
    public virtual async Task<TaskResult<UserToken>> Authenticate(
        AuthenticateRequest request,
        IEnumerable<Claim>? requiredClaims = null)
    {
        var user = await FindUserByUsernameAsync(request.UserName);
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

        return await CreateAuthenticationSessionAsync(user, requiredClaims);
    }

    /// <summary>
    /// Creates a normal NewHeap access-token and refresh-token session after a derived
    /// authentication service has already verified a consumer-specific credential.
    /// </summary>
    /// <remarks>
    /// Use this extension point for credentials such as a PIN, passkey or trusted
    /// upstream assertion. The caller remains responsible for verifying that credential
    /// and recording failed attempts. NewHeap enforces account eligibility and lockout,
    /// resets an existing failed-attempt count after successful verification, and checks
    /// required claims without changing the user's password or revoking another device's
    /// refresh token.
    /// </remarks>
    /// <param name="user">The user whose consumer-specific credential was verified.</param>
    /// <param name="requiredClaims">Claims required before a session may be issued.</param>
    /// <returns>The newly created authentication session.</returns>
    protected virtual async Task<TaskResult<UserToken>> CreateAuthenticationSessionAsync(
        TUser user,
        IEnumerable<Claim>? requiredClaims = null)
    {
        if (!await _signInManager.CanSignInAsync(user)
            || await _userManager.IsLockedOutAsync(user))
        {
            return TaskResult<UserToken>.Failed("User locked out");
        }

        if (await _userManager.GetAccessFailedCountAsync(user) > 0)
        {
            var resetAccessFailedResult = await _userManager.ResetAccessFailedCountAsync(user);
            if (!resetAccessFailedResult.Succeeded)
            {
                return TaskResult<UserToken>.Failed("Could not update authentication state");
            }
        }

        var token = await CreateToken(
            user.Id,
            withDivisionClaims: _authConfiguration.DivisionsEnabled,
            expiration: _authConfiguration.ExpirationTimespanToken
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

        var refreshTokenResult = await CreateRefreshTokenAsync(user);
        if (!refreshTokenResult.Success)
        {
            return TaskResult<UserToken>.Failed("Could not create refresh token");
        }

        var refreshTokenInfo = refreshTokenResult.Data!;

        _logger.LogInformation("User {user} logged in", user.UserName);

        return CreateUserToken(token, refreshTokenInfo.RefreshToken, refreshTokenInfo.ExpirationDateTime.DateTime);
    }

    protected virtual TaskResult<UserToken> CreateUserToken(JwtSecurityToken token, string? refreshToken, DateTime? refreshTokenValidTo)
    {
        return new UserToken(
            Token: new JwtSecurityTokenHandler().WriteToken(token),
            ValidTo: token.ValidTo,
            RefreshToken: refreshToken,
            RefreshValidTo: refreshTokenValidTo,
            token.Issuer
        );
    }

    /// <summary>
    /// Generate a random refresh token
    /// </summary>
    /// <returns></returns>
    protected string GenerateRefreshToken()
    {
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(128);
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

        var claims = await GetClaimsAsync(user!.Id);
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

        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user == null)
        {
            throw new InvalidOperationException("Invalid user id");
        }

        var claims = c.ToList();
        var tokenKey = GetTokenKey();
        var issuer = GetIssuer();

        expiration ??= TimeSpan.FromDays(1);

        if (!claims.Any(x => x.Type == ClaimTypes.NameIdentifier))
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, userId.ToString()));
        }

        var securityStampClaimType = _signInManager.UserManager.Options.ClaimsIdentity.SecurityStampClaimType;
        var securityStamp = await _signInManager.UserManager.GetSecurityStampAsync(user);
        if (string.IsNullOrEmpty(securityStamp))
        {
            throw new InvalidOperationException("The user does not have a security stamp.");
        }

        claims.RemoveAll(x => x.Type == securityStampClaimType);
        claims.Add(new Claim(securityStampClaimType, securityStamp));
        
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(tokenKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var now = DateTime.UtcNow;

        var token = new JwtSecurityToken(issuer,
            issuer,
            claims,
            expires: now.Add(expiration.Value),
            notBefore: now,
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
        var claimsPrincipal = ValidateToken(token, out var t);
        return t as JwtSecurityToken;
    }

    public virtual ClaimsPrincipal? ValidateToken(string token, out SecurityToken validatedToken)
    {
        var handler = new JwtSecurityTokenHandler();
        var claimsPrincipal = handler.ValidateToken(token, _tokenValidationParameters, out var t);
        validatedToken = t;

        return claimsPrincipal;
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

    protected virtual async Task<List<Claim>> GetClaimsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var claims = await _userManager.GetValidClaimsByUserIdAsync(userId, _authConfiguration.DivisionsEnabled, cancellationToken);
        return claims;
    }

    /// <summary>
    /// Creates an access token that lets <paramref name="currentUser"/> act as <paramref name="user"/>.
    /// </summary>
    /// <remarks>
    /// The impersonation session deliberately has no refresh token. A refresh would rebuild
    /// the token from the target user's claims and silently drop the impersonation origin,
    /// turning the session into an ordinary long-lived login as the target user. The session
    /// therefore ends when the access token expires or the origin user reverts it.
    /// </remarks>
    protected virtual async Task<TaskResult<UserToken>> Impersonate(TUser currentUser, TUser user)
    {
        var claims = await GetClaimsAsync(user!.Id);
        claims.Add(new Claim(NhPlatformClaimTypes.ImpersonateOriginUserId, currentUser.Id.ToString()));
        var token = await CreateToken(
            user.Id,
            c: claims,
            expiration: null
        );

        return CreateUserToken(
            token,
            refreshToken: null,
            refreshTokenValidTo: null
        );
    }

    public virtual async Task<TaskResult<UserToken>> LoginWithoutValidations(Guid userId, bool iAmSureThatIKnowWhatImDoing = false)
    {
        if (!iAmSureThatIKnowWhatImDoing)
        {
            return TaskResult<UserToken>.Failed("No");
        }

        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user == null)
        {
            return TaskResult<UserToken>.Failed("Unknown user");
        }

        var refreshTokenResult = await CreateRefreshTokenAsync(user);
        if (!refreshTokenResult.Success)
        {
            return TaskResult<UserToken>.Failed("Could not create refresh token");
        }

        var refreshToken = refreshTokenResult.Data!;


        var claims = await GetClaimsAsync(userId);
        var token = await CreateToken(
            userId,
            c: claims,
            expiration: null
        );

        return CreateUserToken(
            token,
            refreshToken.RefreshToken,
            refreshToken.ExpirationDateTime.DateTime
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

        return await ImpersonateRevert(impersonatedUser, originUser);
    }

    protected virtual async Task<TaskResult<UserToken>> ImpersonateRevert(TUser impersonatedUser, TUser originUser)
    {
        var claims = await GetClaimsAsync(originUser!.Id);
        var token = await CreateToken(
            originUser.Id,
            c: claims,
            expiration: null
        );

        var refreshTokenResult = await CreateRefreshTokenAsync(originUser);
        if (!refreshTokenResult.Success)
        {
            return TaskResult<UserToken>.Failed("Could not create refresh token");
        }

        var refreshToken = refreshTokenResult.Data!;

        return CreateUserToken(
            token,
            refreshToken.RefreshToken,
            refreshToken.ExpirationDateTime.DateTime
        );
    }


    public virtual void WriteTokenToCookie(HttpContext httpContext, UserToken token, string? authCookieName = null, string? refreshCookieName = null)
    {
        var domain = new Uri(token.Issuer).Host;

        authCookieName ??= _authConfiguration.CookieName;
        refreshCookieName ??= _authConfiguration.RefreshCookieName;

        if(string.IsNullOrWhiteSpace(authCookieName))
        {
            throw new ConfigurationException("Auth cookie name is not configured");
        }

        if(string.IsNullOrEmpty(refreshCookieName) && _authConfiguration.RefreshTokenEnabled)
        {
            throw new ConfigurationException("Refresh cookie name is not configured");
        }

        httpContext!.Response.Cookies.Append(_authConfiguration.CookieName!, token.Token, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Expires = token.ValidTo,
            Domain = domain,
            IsEssential = true,
        });

        if (_authConfiguration.RefreshTokenEnabled && !string.IsNullOrWhiteSpace(token.RefreshToken))
        {
            httpContext.Response.Cookies.Append(_authConfiguration.RefreshCookieName!, token.RefreshToken!, new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Lax,
                Expires = DateTimeOffset.Now.AddDays(2),
                Domain = domain,
                IsEssential = true,
            });
        }
        else
        {
            token.RefreshToken = null;
        }
    }
}
