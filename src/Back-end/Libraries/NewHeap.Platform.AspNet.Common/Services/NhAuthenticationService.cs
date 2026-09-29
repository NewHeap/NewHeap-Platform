using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using NewHeap.Platform.AspNet.Common.Authentication;
using NewHeap.Platform.AspNet.Common.Authentication.TwoFactor;
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
    > : INhAuthenticationService, INhMultiFactorAuthenticationService
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
    private readonly NhTwoFactorAuthenticationContext<TUser>? _twoFactorContext;

    /// <summary>
    /// Creates the service with two-factor support. Derived services must use this
    /// constructor when two-factor authentication is enabled; startup fails otherwise.
    /// </summary>
    public NhAuthenticationService(
        SignInManager<TUser> signInManager,
        INhUserManager<TUser> userManager,
        ILogger<AuthenticationService> logger,
        IConfiguration configuration,
        TokenValidationParameters tokenValidationParameters,
        AuthenticationConfiguration authConfiguration,
        NhTwoFactorAuthenticationContext<TUser> twoFactorContext
    )
        : this(signInManager, userManager, logger, configuration, tokenValidationParameters, authConfiguration)
    {
        _twoFactorContext = twoFactorContext;
    }

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

        var twoFactorGate = await EvaluateSessionGateAsync(user, NhAuthenticationFactors.RefreshToken, allowEnrolledUsers: true);
        if (!twoFactorGate.Success)
        {
            return TaskResult<UserToken>.Failed(twoFactorGate);
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
        if (TwoFactor != null)
        {
            // A caller that can only handle a complete session must never receive one for a
            // user who still has to present a second factor.
            var stepResult = await AuthenticateAsync(request, requiredClaims);
            if (!stepResult.Success)
            {
                return TaskResult<UserToken>.Failed(stepResult);
            }

            if (stepResult.Data!.Session == null)
            {
                return NhTwoFactorFailureCodes.Fail<UserToken>(NhTwoFactorFailureCodes.Required);
            }

            return stepResult.Data.Session;
        }

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
    /// <remarks>
    /// When two-factor authentication is enabled, this method fails with
    /// <see cref="NhTwoFactorFailureCodes.Required"/> for users who need a second factor.
    /// Use <see cref="CompleteFirstFactorAsync"/> so those users receive a challenge.
    /// </remarks>
    protected virtual async Task<TaskResult<UserToken>> CreateAuthenticationSessionAsync(
        TUser user,
        IEnumerable<Claim>? requiredClaims = null)
    {
        var twoFactorGate = await EvaluateSessionGateAsync(
            user,
            NhAuthenticationFactors.Custom("unspecified"),
            allowEnrolledUsers: false);
        if (!twoFactorGate.Success)
        {
            return TaskResult<UserToken>.Failed(twoFactorGate);
        }

        return await CreateSessionCoreAsync(user, requiredClaims);
    }

    /// <summary>
    /// Creates a session for a user whose required factors NewHeap verified.
    /// </summary>
    /// <param name="user">The verified user.</param>
    /// <param name="proof">Proof created by a NewHeap verification path.</param>
    /// <param name="requiredClaims">Claims required before a session may be issued.</param>
    protected Task<TaskResult<UserToken>> CreateAuthenticationSessionAsync(
        TUser user,
        NhAuthenticationProof proof,
        IEnumerable<Claim>? requiredClaims = null)
    {
        ArgumentNullException.ThrowIfNull(proof);

        if (proof.UserId != user.Id)
        {
            throw new ArgumentException("The authentication proof belongs to another user.", nameof(proof));
        }

        return CreateSessionCoreAsync(user, requiredClaims);
    }

    /// <summary>
    /// Completes a verified first factor. Returns a session, or a second-factor challenge
    /// when the two-factor policy requires one. Use this from derived services after
    /// verifying a consumer-specific credential such as a PIN.
    /// </summary>
    /// <param name="user">The user whose first factor was verified.</param>
    /// <param name="factor">The verified factor, see <see cref="NhAuthenticationFactors"/>.</param>
    /// <param name="requiredClaims">Claims required before a session may be issued.</param>
    /// <param name="rememberDeviceToken">
    /// A remember-device token from an earlier sign-in; a valid token skips the second factor
    /// when the policy allows it.
    /// </param>
    /// <remarks>
    /// A user whom the policy requires to use a second factor but who has not enrolled one
    /// receives an enrollment step (<see cref="NhAuthenticationStepStatuses.EnrollmentRequired"/>)
    /// instead of a session.
    /// </remarks>
    protected virtual async Task<TaskResult<NhAuthenticationResult>> CompleteFirstFactorAsync(
        TUser user,
        string factor,
        IEnumerable<Claim>? requiredClaims = null,
        string? rememberDeviceToken = null)
    {
        if (!await _signInManager.CanSignInAsync(user)
            || await _userManager.IsLockedOutAsync(user))
        {
            return TaskResult<NhAuthenticationResult>.Failed("User locked out");
        }

        var twoFactor = TwoFactor;
        if (twoFactor != null)
        {
            var evaluation = await twoFactor.EvaluateAsync(user, factor, CancellationToken.None);
            if (evaluation.Requirement.Required)
            {
                var methods = NhTwoFactorService<TUser>.UsableMethods(evaluation);
                if (methods.Count == 0)
                {
                    var enrollment = await twoFactor.CreateEnrollmentAsync(user, factor);
                    if (enrollment == null)
                    {
                        return NhTwoFactorFailureCodes.Fail<NhAuthenticationResult>(NhTwoFactorFailureCodes.EnrollmentRequired);
                    }

                    return NhAuthenticationResult.Pending(enrollment);
                }

                var remembered = evaluation.Requirement.AllowRememberDevice
                    && await twoFactor.IsRememberedDeviceAsync(user, rememberDeviceToken);
                if (!remembered)
                {
                    var challenge = await twoFactor.CreateChallengeAsync(user, factor, methods);
                    return NhAuthenticationResult.Pending(challenge);
                }
            }
        }

        var session = await CreateSessionCoreAsync(user, requiredClaims);
        if (!session.Success)
        {
            return TaskResult<NhAuthenticationResult>.Failed(session);
        }

        return NhAuthenticationResult.Authenticated(session.Data!);
    }

    private async Task<TaskResult<UserToken>> CreateSessionCoreAsync(
        TUser user,
        IEnumerable<Claim>? requiredClaims)
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

        var twoFactorGate = await EvaluateSessionGateAsync(user, NhAuthenticationFactors.Trusted, allowEnrolledUsers: false);
        if (!twoFactorGate.Success)
        {
            return TaskResult<UserToken>.Failed(twoFactorGate);
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


    /// <summary>
    /// The two-factor service when two-factor authentication is enabled and this service was
    /// constructed with the two-factor context; otherwise <see langword="null"/>.
    /// </summary>
    protected NhTwoFactorService<TUser>? TwoFactor
    {
        get
        {
            if (_twoFactorContext?.IsEnabled == true)
            {
                return _twoFactorContext.Service;
            }

            return null;
        }
    }

    public bool IsTwoFactorAvailable => TwoFactor != null;

    public virtual async Task<TaskResult<NhAuthenticationResult>> AuthenticateAsync(
        AuthenticateRequest request,
        IEnumerable<Claim>? requiredClaims = null)
    {
        var user = await FindUserByUsernameAsync(request.UserName);
        if (user == null)
        {
            return TaskResult<NhAuthenticationResult>.Failed("Unknown user");
        }

        if (TwoFactor == null)
        {
            var session = await Authenticate(user, request, requiredClaims);
            if (!session.Success)
            {
                return TaskResult<NhAuthenticationResult>.Failed(session);
            }

            return NhAuthenticationResult.Authenticated(session.Data!);
        }

        var passwordResult = await VerifyPasswordFactorAsync(user, request.Password);
        if (!passwordResult.Success)
        {
            return TaskResult<NhAuthenticationResult>.Failed(passwordResult);
        }

        return await CompleteFirstFactorAsync(
            user,
            NhAuthenticationFactors.Password,
            requiredClaims,
            request.RememberDeviceToken);
    }

    public virtual async Task<TaskResult<NhAuthenticationResult>> VerifyTwoFactorAsync(
        NhTwoFactorVerifyRequest request,
        IEnumerable<Claim>? requiredClaims = null)
    {
        var twoFactor = TwoFactor;
        if (twoFactor == null)
        {
            return NhTwoFactorFailureCodes.Fail<NhAuthenticationResult>(NhTwoFactorFailureCodes.ConfigurationInvalid);
        }

        var pending = await ReadPendingStepAsync(twoFactor, request.ChallengeToken, enrollment: false);
        if (!pending.Success)
        {
            return TaskResult<NhAuthenticationResult>.Failed(pending);
        }

        var (user, challenge) = pending.Data!;
        var evaluation = await twoFactor.EvaluateAsync(user, challenge.Factor, CancellationToken.None);
        var usableMethods = NhTwoFactorService<TUser>.UsableMethods(evaluation);
        if (evaluation.Requirement.Required && !usableMethods.Contains(request.Method))
        {
            return NhTwoFactorFailureCodes.Fail<NhAuthenticationResult>(NhTwoFactorFailureCodes.MethodNotAllowed);
        }

        if (!evaluation.Enrollment.Methods.Contains(request.Method))
        {
            return NhTwoFactorFailureCodes.Fail<NhAuthenticationResult>(NhTwoFactorFailureCodes.MethodNotAllowed);
        }

        var check = await twoFactor.CheckCodeAsync(user, request.Method, request.Code);
        if (!check.IsCandidate)
        {
            _logger.LogInformation("Failed second-factor attempt for user {user}", user.UserName);
            var failure = await twoFactor.RecordFailedAttemptAsync(user, CancellationToken.None);
            return TaskResult<NhAuthenticationResult>.Failed(failure);
        }

        var repository = _userManager.GetRepository();
        await using var transaction = await repository.StartOrGetTransactionScopeAsync();

        try
        {
            var commitResult = await twoFactor.CommitSecondFactorAsync(user, check, challenge, CancellationToken.None);
            if (!commitResult.Success)
            {
                await transaction.RollbackAsync();

                if (!NhTwoFactorFailureCodes.Has(commitResult, NhTwoFactorFailureCodes.InvalidCode))
                {
                    return TaskResult<NhAuthenticationResult>.Failed(commitResult);
                }

                _logger.LogInformation("Failed second-factor attempt for user {user}", user.UserName);
                var failure = await twoFactor.RecordFailedAttemptAfterRollbackAsync(
                    user,
                    transaction.IsMyTransaction,
                    CancellationToken.None);
                return TaskResult<NhAuthenticationResult>.Failed(failure);
            }

            var proof = new NhAuthenticationProof(user.Id, [challenge.Factor, request.Method]);
            var session = await CreateAuthenticationSessionAsync(user, proof, requiredClaims);
            if (!session.Success)
            {
                await transaction.RollbackAsync();
                return TaskResult<NhAuthenticationResult>.Failed(session);
            }

            await transaction.CommitAsync();

            string? rememberDeviceToken = null;
            if (request.RememberDevice
                && twoFactor.Configuration.RememberDeviceEnabled
                && evaluation.Requirement.AllowRememberDevice)
            {
                rememberDeviceToken = await twoFactor.CreateRememberDeviceTokenAsync(user);
            }

            return NhAuthenticationResult.Authenticated(session.Data!, rememberDeviceToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public virtual async Task<TaskResult<NhTwoFactorEmailCodeSentResponse>> SendTwoFactorEmailCodeAsync(string challengeToken)
    {
        var twoFactor = TwoFactor;
        if (twoFactor == null)
        {
            return NhTwoFactorFailureCodes.Fail<NhTwoFactorEmailCodeSentResponse>(NhTwoFactorFailureCodes.ConfigurationInvalid);
        }

        var pending = await ReadPendingStepAsync(twoFactor, challengeToken, enrollment: false);
        if (!pending.Success)
        {
            return TaskResult<NhTwoFactorEmailCodeSentResponse>.Failed(pending);
        }

        var (user, challenge) = pending.Data!;
        var evaluation = await twoFactor.EvaluateAsync(user, challenge.Factor, CancellationToken.None);

        return await twoFactor.SendSignInEmailCodeAsync(
            user,
            NhTwoFactorService<TUser>.UsableMethods(evaluation),
            CancellationToken.None);
    }

    public virtual async Task<TaskResult<NhAuthenticatorSetupViewModel>> BeginEnrollmentAuthenticatorSetupAsync(string enrollmentToken)
    {
        var twoFactor = TwoFactor;
        if (twoFactor == null)
        {
            return NhTwoFactorFailureCodes.Fail<NhAuthenticatorSetupViewModel>(NhTwoFactorFailureCodes.ConfigurationInvalid);
        }

        var pending = await ReadPendingStepAsync(twoFactor, enrollmentToken, enrollment: true);
        if (!pending.Success)
        {
            return TaskResult<NhAuthenticatorSetupViewModel>.Failed(pending);
        }

        return await twoFactor.BeginEnrollmentAuthenticatorSetupAsync(pending.Data!.User);
    }

    public virtual async Task<TaskResult<NhTwoFactorEnrollmentCompletion>> ConfirmEnrollmentAuthenticatorAsync(
        string enrollmentToken,
        string code)
    {
        var twoFactor = TwoFactor;
        if (twoFactor == null)
        {
            return NhTwoFactorFailureCodes.Fail<NhTwoFactorEnrollmentCompletion>(NhTwoFactorFailureCodes.ConfigurationInvalid);
        }

        var pending = await ReadPendingStepAsync(twoFactor, enrollmentToken, enrollment: true);
        if (!pending.Success)
        {
            return TaskResult<NhTwoFactorEnrollmentCompletion>.Failed(pending);
        }

        var user = pending.Data!.User;
        var change = await twoFactor.ConfirmAuthenticatorAsync(user, code, user.Id);

        return await CompleteEnrollmentAsync(user, change);
    }

    public virtual async Task<TaskResult<NhTwoFactorEmailCodeSentResponse>> SendEnrollmentEmailCodeAsync(string enrollmentToken)
    {
        var twoFactor = TwoFactor;
        if (twoFactor == null)
        {
            return NhTwoFactorFailureCodes.Fail<NhTwoFactorEmailCodeSentResponse>(NhTwoFactorFailureCodes.ConfigurationInvalid);
        }

        var pending = await ReadPendingStepAsync(twoFactor, enrollmentToken, enrollment: true);
        if (!pending.Success)
        {
            return TaskResult<NhTwoFactorEmailCodeSentResponse>.Failed(pending);
        }

        return await twoFactor.SendEnrollmentEmailCodeAsync(pending.Data!.User, CancellationToken.None);
    }

    public virtual async Task<TaskResult<NhTwoFactorEnrollmentCompletion>> ConfirmEnrollmentEmailAsync(
        string enrollmentToken,
        string code)
    {
        var twoFactor = TwoFactor;
        if (twoFactor == null)
        {
            return NhTwoFactorFailureCodes.Fail<NhTwoFactorEnrollmentCompletion>(NhTwoFactorFailureCodes.ConfigurationInvalid);
        }

        var pending = await ReadPendingStepAsync(twoFactor, enrollmentToken, enrollment: true);
        if (!pending.Success)
        {
            return TaskResult<NhTwoFactorEnrollmentCompletion>.Failed(pending);
        }

        if (!twoFactor.Configuration.EnrollableMethods(requiredByPolicy: true).Contains(NhTwoFactorMethods.Email))
        {
            return NhTwoFactorFailureCodes.Fail<NhTwoFactorEnrollmentCompletion>(NhTwoFactorFailureCodes.MethodNotAllowed);
        }

        var user = pending.Data!.User;
        var change = await twoFactor.ConfirmEmailSetupAsync(user, code, user.Id);

        return await CompleteEnrollmentAsync(user, change);
    }

    public virtual async Task<TaskResult<NhAuthenticationResult>> AuthenticateExternalAsync(Guid userId, string factor)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user == null)
        {
            return TaskResult<NhAuthenticationResult>.Failed("Unknown user");
        }

        return await CompleteFirstFactorAsync(user, factor);
    }

    public virtual async Task<TaskResult<UserToken>> RenewSessionAsync(NhAuthenticationProof proof)
    {
        ArgumentNullException.ThrowIfNull(proof);

        var user = await _userManager.FindByIdAsync(proof.UserId.ToString());
        if (user == null)
        {
            return TaskResult<UserToken>.Failed("Unknown user");
        }

        return await CreateAuthenticationSessionAsync(user, proof, _authConfiguration.AuthenticateRequiredClaims);
    }

    /// <summary>
    /// Verifies the password as a first factor without resetting failed attempts. The reset
    /// happens when the session is issued, after every required factor succeeded.
    /// </summary>
    protected virtual async Task<TaskResult> VerifyPasswordFactorAsync(TUser user, string password)
    {
        if (!await _signInManager.CanSignInAsync(user)
            || await _userManager.IsLockedOutAsync(user))
        {
            return TaskResult.Failed("User locked out");
        }

        if (await _userManager.CheckPasswordAsync(user, password))
        {
            return TaskResult.Succeeded();
        }

        _logger.LogInformation("Failed login attempt for user {user}", user.UserName);
        await _userManager.AccessFailedAsync(user);

        if (await _userManager.IsLockedOutAsync(user))
        {
            return TaskResult.Failed("User locked out");
        }

        return TaskResult.Failed("Invalid password");
    }

    /// <summary>
    /// Reads a pending challenge or enrollment and checks that it still belongs to the
    /// account state and that the account may sign in.
    /// </summary>
    private async Task<TaskResult<NhPendingTwoFactorStep<TUser>>> ReadPendingStepAsync(
        NhTwoFactorService<TUser> twoFactor,
        string? token,
        bool enrollment)
    {
        var ticket = enrollment ? twoFactor.ReadEnrollment(token) : twoFactor.ReadChallenge(token);
        if (ticket == null)
        {
            return NhTwoFactorFailureCodes.Fail<NhPendingTwoFactorStep<TUser>>(NhTwoFactorFailureCodes.ChallengeExpired);
        }

        var user = await _userManager.FindByIdAsync(ticket.UserId.ToString());
        if (user == null || !await twoFactor.IsTicketUsableAsync(user, ticket))
        {
            return NhTwoFactorFailureCodes.Fail<NhPendingTwoFactorStep<TUser>>(NhTwoFactorFailureCodes.ChallengeExpired);
        }

        if (!await _signInManager.CanSignInAsync(user)
            || await _userManager.IsLockedOutAsync(user))
        {
            return NhTwoFactorFailureCodes.Fail<NhPendingTwoFactorStep<TUser>>(NhTwoFactorFailureCodes.LockedOut);
        }

        return new NhPendingTwoFactorStep<TUser>(user, ticket);
    }

    /// <summary>
    /// Issues the session after a required user enrolled a second factor during sign-in.
    /// </summary>
    private async Task<TaskResult<NhTwoFactorEnrollmentCompletion>> CompleteEnrollmentAsync(
        TUser user,
        TaskResult<NhTwoFactorChangeResult> change)
    {
        if (!change.Success)
        {
            return TaskResult<NhTwoFactorEnrollmentCompletion>.Failed(change);
        }

        var session = await CreateAuthenticationSessionAsync(
            user,
            change.Data!.RenewalProof!,
            _authConfiguration.AuthenticateRequiredClaims);
        if (!session.Success)
        {
            return TaskResult<NhTwoFactorEnrollmentCompletion>.Failed(session);
        }

        return new NhTwoFactorEnrollmentCompletion(session.Data!, change.Data.RecoveryCodes);
    }

    private async Task<TaskResult> EvaluateSessionGateAsync(TUser user, string factor, bool allowEnrolledUsers)
    {
        var twoFactor = TwoFactor;
        if (twoFactor == null)
        {
            return TaskResult.Succeeded();
        }

        var evaluation = await twoFactor.EvaluateAsync(user, factor, CancellationToken.None);
        if (!evaluation.Requirement.Required)
        {
            return TaskResult.Succeeded();
        }

        if (NhTwoFactorService<TUser>.UsableMethods(evaluation).Count == 0)
        {
            return NhTwoFactorFailureCodes.Fail(NhTwoFactorFailureCodes.EnrollmentRequired);
        }

        if (allowEnrolledUsers)
        {
            return TaskResult.Succeeded();
        }

        return NhTwoFactorFailureCodes.Fail(NhTwoFactorFailureCodes.Required);
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
