using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models;
using NewHeap.Platform.Common.Identity.Claims;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Models.Options;
using System.IdentityModel.Tokens.Jwt;
using System.Linq.Expressions;
using System.Security.Claims;

namespace NewHeap.Platform.AspNet.Common.Services;

public interface INhUserManager
{
    Task<bool> DivisionAccessAsync(Guid? divisionId, IEnumerable<Claim> userClaims, IEnumerable<Claim>? requireClaims = null, IEnumerable<string>? requireRoles = null);
    string GenerateRandomPassword(PasswordOptions? passwordOptions = null);
    string GenerateRegistrationToken();
    bool IsOauthAccount(string email);

    Task<List<Claim>> GetValidClaimsByUserId(Guid userId, bool withDivision = false);
}

public interface INhUserManager<TUser> : INhUserManager
    where TUser : class
{
    Task<TaskResult<TUser>> ChangeActiviveDivisionAsync(Guid id, ChangeActiveDivisionAccountModel mutateModel);
    Task<TUser?> FindByIdWithIncludesAsync(Guid userId);

    Task<TUser?> FindOneByAsync(Expression<Func<TUser, bool>> predicate);

    IRepository<TUser> GetRepository();
    Task<List<Claim>> GetValidClaims(TUser user, bool withDivision = false);
    Task<bool> IsBlocked(TUser user);
    bool IsOauthAccount(TUser user);
    IQueryable<TUser> QueryableWithAllIncludes(IQueryable<TUser>? queryable = null);
    Task UpdateUserLockout(TUser user, DateTimeOffset? start = null, DateTimeOffset? end = null);

    #region Generated for Identity framework base class
    Task<IdentityResult> CreateAsync(TUser user);
    Task<IdentityResult> CreateAsync(TUser user, string password);
    Task<IdentityResult> UpdateAsync(TUser user);
    Task<IdentityResult> DeleteAsync(TUser user);
    Task<TUser?> FindByIdAsync(string userId);
    Task<TUser?> FindByNameAsync(string userName);
    Task<TUser?> FindByEmailAsync(string email);
    Task<string?> GetUserIdAsync(TUser user);
    Task<string?> GetUserNameAsync(TUser user);
    Task<IdentityResult> SetUserNameAsync(TUser user, string userName);
    Task<string?> GetEmailAsync(TUser user);
    Task<IdentityResult> SetEmailAsync(TUser user, string email);
    Task<bool> IsEmailConfirmedAsync(TUser user);
    Task<IdentityResult> ConfirmEmailAsync(TUser user, string token);
    Task<IdentityResult> ChangeEmailAsync(TUser user, string newEmail, string token);
    Task<IdentityResult> AddPasswordAsync(TUser user, string password);
    Task<IdentityResult> ChangePasswordAsync(TUser user, string currentPassword, string newPassword);
    Task<IdentityResult> RemovePasswordAsync(TUser user);
    Task<bool> HasPasswordAsync(TUser user);
    Task<bool> CheckPasswordAsync(TUser user, string password);
    Task<string> GeneratePasswordResetTokenAsync(TUser user);
    Task<IdentityResult> ResetPasswordAsync(TUser user, string token, string newPassword);
    Task<IList<Claim>> GetClaimsAsync(TUser user);
    Task<IdentityResult> AddClaimAsync(TUser user, Claim claim);
    Task<IdentityResult> AddClaimsAsync(TUser user, IEnumerable<Claim> claims);
    Task<IdentityResult> RemoveClaimAsync(TUser user, Claim claim);
    Task<IdentityResult> RemoveClaimsAsync(TUser user, IEnumerable<Claim> claims);
    Task<IdentityResult> ReplaceClaimAsync(TUser user, Claim claim, Claim newClaim);
    Task<IList<TUser>> GetUsersForClaimAsync(Claim claim);
    Task<IdentityResult> AddToRoleAsync(TUser user, string role);
    Task<IdentityResult> RemoveFromRoleAsync(TUser user, string role);
    Task<IList<string>> GetRolesAsync(TUser user);
    Task<bool> IsInRoleAsync(TUser user, string role);
    Task<IList<TUser>> GetUsersInRoleAsync(string role);
    Task<string> GenerateEmailConfirmationTokenAsync(TUser user);
    Task<string> GenerateChangeEmailTokenAsync(TUser user, string newEmail);
    Task<string> GenerateChangePhoneNumberTokenAsync(TUser user, string phoneNumber);
    Task<bool> VerifyChangePhoneNumberTokenAsync(TUser user, string token, string phoneNumber);
    Task<IdentityResult> ChangePhoneNumberAsync(TUser user, string phoneNumber, string token);
    Task<string?> GetPhoneNumberAsync(TUser user);
    Task<IdentityResult> SetPhoneNumberAsync(TUser user, string phoneNumber);
    Task<bool> IsPhoneNumberConfirmedAsync(TUser user);
    Task<bool> GetTwoFactorEnabledAsync(TUser user);
    Task<IdentityResult> SetTwoFactorEnabledAsync(TUser user, bool enabled);
    Task<string> GenerateTwoFactorTokenAsync(TUser user, string tokenProvider);
    Task<bool> VerifyTwoFactorTokenAsync(TUser user, string tokenProvider, string token);
    Task<IList<string>> GetValidTwoFactorProvidersAsync(TUser user);
    Task<string?> GetAuthenticatorKeyAsync(TUser user);
    Task<IdentityResult> ResetAuthenticatorKeyAsync(TUser user);
    Task<IEnumerable<string>?> GenerateNewTwoFactorRecoveryCodesAsync(TUser user, int number);
    Task<IdentityResult> RedeemTwoFactorRecoveryCodeAsync(TUser user, string code);
    Task<int> CountRecoveryCodesAsync(TUser user);
    Task<IdentityResult> AccessFailedAsync(TUser user);
    Task<IdentityResult> ResetAccessFailedCountAsync(TUser user);
    Task<int> GetAccessFailedCountAsync(TUser user);
    Task<bool> IsLockedOutAsync(TUser user);
    Task<IdentityResult> SetLockoutEnabledAsync(TUser user, bool enabled);
    Task<bool> GetLockoutEnabledAsync(TUser user);
    Task<DateTimeOffset?> GetLockoutEndDateAsync(TUser user);
    Task<IdentityResult> SetLockoutEndDateAsync(TUser user, DateTimeOffset? lockoutEnd);
    #endregion
}

public partial class NhUserManager : NhUserManager<
    NhUser,
    NhUserRole,
    NhLogMessageArgument,
    NhLogMessageTranslated,
    NhLogFile,
    NhDivision,
    NhDivisionUser,
    NhDivisionRole,
    NhDivisionUserRole,
    NhDivisionRoleClaim>
{
    public NhUserManager(
        IWebHostEnvironment environment, 
        IUserStore<NhUser> store, 
        IOptions<IdentityOptions> optionsAccessor, 
        IPasswordHasher<NhUser> passwordHasher, 
        IEnumerable<IUserValidator<NhUser>> userValidators, 
        IEnumerable<IPasswordValidator<NhUser>> passwordValidators, 
        ILookupNormalizer keyNormalizer, 
        IdentityErrorDescriber errors, 
        IServiceProvider services, 
        ILogger<UserManager<NhUser>> logger, 
        IOptions<MicrosoftAuthSettings> microsoftAuthSettings, 
        IRepository<NhUser> userRepository, 
        RoleManager<NhUserRole> roleManager
        ) : base(
            environment, 
            store, optionsAccessor, passwordHasher, userValidators, passwordValidators, 
            keyNormalizer, errors, services, logger, microsoftAuthSettings, userRepository, roleManager)
    {
    }
}

public abstract partial class NhUserManager<
    TUser,
    TUserRole,
    TLogMessageArgument,
    TLogMessageTranslated,
    TLogFile,
    TDivision,
    TDivisionUser,
    TDivisionRole,
    TDivisionUserRole,
    TDivisionRoleClaim
    > : UserManager<TUser>, INhUserManager<TUser> 
    where TUser : NhUser<TDivision, TDivisionUser, TDivisionUserRole, TDivisionRole, TDivisionRoleClaim, TUser>, new()
    where TUserRole : NhUserRole
    where TLogMessageArgument : NhLogMessageArgument, new()
    where TLogMessageTranslated : NhLogMessageTranslated, new()
    where TLogFile : NhLogFile, new()
    where TDivision : NhDivision<TDivisionUser, TDivisionUserRole, TDivisionRole, TDivisionRoleClaim, TDivision, TUser>
    where TDivisionRole : NhDivisionRole<TDivisionUserRole, TDivisionRoleClaim, TDivisionUser, TDivisionRole, TDivision, TUser>
    where TDivisionUser : NhDivisionUser<TDivisionUserRole, TDivisionUser, TDivisionRole, TDivisionRoleClaim, TDivision, TUser>
    where TDivisionUserRole : NhDivisionUserRole<TDivisionUser, TDivisionRole, TDivisionRoleClaim, TDivisionUserRole, TDivision, TUser>
    where TDivisionRoleClaim : NhDivisionRoleClaim
{
    private readonly IWebHostEnvironment _environment;
    private readonly MicrosoftAuthSettings _microsoftAuthSettings;
    protected readonly RoleManager<TUserRole> _roleManager;
    protected readonly IRepository<TUser> _userRepository;

    public NhUserManager(
        IWebHostEnvironment environment,
        IUserStore<TUser> store,
        IOptions<IdentityOptions> optionsAccessor,
        IPasswordHasher<TUser> passwordHasher,
        IEnumerable<IUserValidator<TUser>> userValidators,
        IEnumerable<IPasswordValidator<TUser>> passwordValidators,
        ILookupNormalizer keyNormalizer,
        IdentityErrorDescriber errors,
        IServiceProvider services,
        ILogger<UserManager<TUser>> logger,
        IOptions<MicrosoftAuthSettings> microsoftAuthSettings,
        IRepository<TUser> userRepository,
        RoleManager<TUserRole> roleManager
    ) : base(store, optionsAccessor, passwordHasher, userValidators, passwordValidators, keyNormalizer, errors,
        services, logger)
    {
        _microsoftAuthSettings = microsoftAuthSettings.Value;
        _environment = environment;
        _userRepository = userRepository;
        _roleManager = roleManager;
    }

    public virtual IRepository<TUser> GetRepository()
    {
        return _userRepository;
    }

    public IQueryable<TUser> QueryableWithAllIncludes(IQueryable<TUser>? queryable = null)
    {
        queryable ??= _userRepository.GetAll()
            .Include(x => x.ActiveDivision);

        return queryable;
    }

    public virtual async Task<TUser?> FindOneByAsync(Expression<Func<TUser, bool>> predicate)
    {
        return await QueryableWithAllIncludes().FirstOrDefaultAsync(predicate);
    }

    public Task<List<Claim>> GetValidClaims(TUser user, bool withDivision = false) 
    { 
        return GetValidClaimsByUserId(user.Id, withDivision);
    }

    public virtual async Task<List<Claim>> GetValidClaimsByUserId(Guid userId, bool withDivision = false)
    {
        var user = await FindByIdAsync(userId.ToString());

        IdentityOptions _options = new();
        List<Claim> claims =
        [
            new Claim(JwtRegisteredClaimNames.Sub, user.Email!),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Name, user.UserName!),
            new Claim(ClaimTypes.Email, user.Email!),
            new Claim(JwtRegisteredClaimNames.Email, user.Email!),
            new Claim(JwtRegisteredClaimNames.NameId, user.Id.ToString()),
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(NhPlatformClaimTypes.Permission, Platform.Common.Constants.PermissionClaimValues.AuthenticatedAccess),
        ];

        IList<Claim> userClaims = await GetClaimsAsync(user);
        IList<string> userRoles = await GetRolesAsync(user);

        claims.AddRange(userClaims);

        foreach (var userRole in userRoles)
        {
            claims.Add(new Claim(ClaimTypes.Role, userRole));
            var role = await _roleManager.FindByNameAsync(userRole);
            if (role != null)
            {
                IList<Claim> roleClaims = await _roleManager.GetClaimsAsync(role);
                foreach (var roleClaim in roleClaims)
                {
                    if (!claims.Where(x => x.Type == roleClaim.Type && x.Value == roleClaim.Value).Any())
                    {
                        claims.Add(roleClaim);
                    }
                }
            }
        }

        #region User division claims

        if (withDivision)
        {
            var divisionAccessAll = claims.Any(x =>
                x.Type == NhPlatformClaimTypes.Permission &&
                x.Value == Platform.Common.Constants.DivisionPermissionClaimValues.AccessAll);
            IQueryable<TDivision> divisionsQuery = _userRepository.GetDbSet<TDivision>().AsNoTracking();

            if (!divisionAccessAll)
            {
                divisionsQuery = divisionsQuery.Where(x => x.DivisionUsers.Any(c =>
                    c.UserId == user.Id
                    && c.LockOutStartDateTime == null ? true :
                    !(c.LockOutStartDateTime!.Value.Date <= DateTimeOffset.Now.Date)
                    && c.LockOutEndDateTime == null ? true :
                    !(c.LockOutEndDateTime!.Value.Date >= DateTimeOffset.Now.Date)
                ));
            }

            List<TDivisionRoleClaim> divisionRolesClaims =
                await _userRepository.GetDbSet<TDivisionRoleClaim>().AsNoTracking().ToListAsync();
            List<Claim> divisionRolesClaimClaims = new();

            var divisionIds = await divisionsQuery.Select(x => x.Id).ToListAsync();
            foreach (var divisionId in divisionIds)
            {
                IQueryable<TDivisionRole> divisionRolesQuery = _userRepository.GetDbSet<TDivisionRole>().AsNoTracking();
                if (!divisionAccessAll)
                {
                    divisionRolesQuery = divisionRolesQuery.Where(x =>
                        x.DivisionUserRoles.Any(c =>
                            c.DivisionUser.UserId == user.Id && c.DivisionUser.DivisionId == divisionId));
                }

                // List of roles this user has in this division
                var divisionRoles = await divisionRolesQuery.Select(x => new { x.Id, x.Name }).ToListAsync();

                foreach (var divisionRole in divisionRoles)
                {
                    var claimValue = divisionId + "_" + divisionRole.Name;
                    var claim = divisionRolesClaimClaims.FirstOrDefault(x =>
                        x.Type == NhPlatformClaimTypes.DivisionRole && x.Value == claimValue);
                    if (claim == null)
                    {
                        claim = new Claim(NhPlatformClaimTypes.DivisionRole, claimValue);
                        divisionRolesClaimClaims.Add(claim);
                    }
                }

                foreach (var divisionRoleClaim in divisionRolesClaims
                             .Where(x => divisionRoles.Any(c => c.Id == x.DivisionRoleId))
                             .GroupBy(x => new { x.ClaimType, x.ClaimValue })
                             .Select(x => x.FirstOrDefault()))
                {
                    var claimValue = divisionId + "_" + divisionRoleClaim!.ClaimValue;
                    var claim = divisionRolesClaimClaims.FirstOrDefault(x =>
                        x.Type == divisionRoleClaim.ClaimType && x.Value == claimValue);
                    if (claim == null)
                    {
                        claim = new Claim(divisionRoleClaim.ClaimType, claimValue);
                        divisionRolesClaimClaims.Add(claim);
                    }
                }
            }

            //Clean up
            claims.AddRange(divisionRolesClaimClaims);
        }

        #endregion

        return claims;
    }

    /// <summary>
    ///     Function to check if a user is allowed to login
    /// </summary>
    /// <param name="user"></param>
    /// <returns></returns>
    public virtual async Task<bool> IsBlocked(TUser user)
    {
        if (await IsLockedOutAsync(user))
        {
            return true;
        }

        if (user.LockoutStart.HasValue && user.LockoutStart.Value <= DateTimeOffset.Now)
        {
            if (!user.LockoutEnd.HasValue || user.LockoutEnd.Value >= DateTimeOffset.Now)
            {
                return true;
            }
        }

        return false;
    }

    public virtual bool IsOauthAccount(string email)
    {
        return _microsoftAuthSettings.AuthDomains?.Contains(email.Split(new[] { '@' })[1],
            StringComparer.InvariantCultureIgnoreCase) == true;
    }

    public virtual bool IsOauthAccount(TUser user)
    {
        return IsOauthAccount(user.NormalizedEmail!);
    }

    public virtual string GenerateRegistrationToken()
    {
        return Guid.NewGuid().ToString() + '-' + Guid.NewGuid();
    }

    public virtual string GenerateRandomPassword(PasswordOptions? passwordOptions = null)
    {
        if (passwordOptions == null)
        {
            passwordOptions = Options.Password;
        }

        string[] randomChars = new[]
        {
            "ABCDEFGHJKLMNOPQRSTUVWXYZ", // uppercase 
            "abcdefghijkmnopqrstuvwxyz", // lowercase
            "0123456789", // digits
            "!@$?_-" // non-alphanumeric
        };

        Random rand = new(Environment.TickCount);
        List<char> chars = new();

        if (passwordOptions.RequireUppercase)
        {
            chars.Insert(rand.Next(0, chars.Count), randomChars[0][rand.Next(0, randomChars[0].Length)]);
        }

        if (passwordOptions.RequireLowercase)
        {
            chars.Insert(rand.Next(0, chars.Count), randomChars[1][rand.Next(0, randomChars[1].Length)]);
        }

        if (passwordOptions.RequireDigit)
        {
            chars.Insert(rand.Next(0, chars.Count), randomChars[2][rand.Next(0, randomChars[2].Length)]);
        }

        if (passwordOptions.RequireNonAlphanumeric)
        {
            chars.Insert(rand.Next(0, chars.Count), randomChars[3][rand.Next(0, randomChars[3].Length)]);
        }

        for (var i = chars.Count;
             i < passwordOptions.RequiredLength
             || chars.Distinct().Count() < passwordOptions.RequiredUniqueChars;
             i++)
        {
            var rcs = randomChars[rand.Next(0, randomChars.Length)];
            chars.Insert(rand.Next(0, chars.Count), rcs[rand.Next(0, rcs.Length)]);
        }

        return new string(chars.ToArray());
    }

    public virtual async Task UpdateUserLockout(TUser user, DateTimeOffset? start = null, DateTimeOffset? end = null)
    {
        if (user == null)
        {
            throw new ArgumentNullException(nameof(user));
        }

        await SetLockoutEndDateAsync(user, end);
        var userEntity = await _userRepository.FindOneByAsync(x => x.Id == user.Id);
        userEntity!.LockoutStart = start;
        await _userRepository.SaveChangesAsync();
    }

    public Task<TUser?> FindByIdWithIncludesAsync(Guid userId)
    {
        return _userRepository
                .GetAll()
                .Where(x => x.Id == userId)
                .Include(x => x.ActiveDivision)
                .FirstOrDefaultAsync()
            ;
    }

    public virtual async Task<TaskResult<TUser>> ChangeActiviveDivisionAsync(Guid id,
        ChangeActiveDivisionAccountModel mutateModel)
    {
        TaskResult<TUser> result = new();

        var user = await _userRepository.GetAll()
            .Where(x => x.Id == id)
            .FirstOrDefaultAsync();

        if (user == null)
        {
            result.AddError(string.Empty, "User not found.");
        }

        if (mutateModel.DivisionId.HasValue)
        {
            List<Claim> claims = await GetValidClaims(user!);
            if (!claims.Any(x =>
                    x.Type == NhPlatformClaimTypes.Permission &&
                    x.Value == Platform.Common.Constants.DivisionPermissionClaimValues.AccessAll))
            {
                if (!await _userRepository.GetDbSet<TDivisionUser>()
                        .AnyAsync(x => x.UserId == id && x.DivisionId == mutateModel.DivisionId))
                {
                    result.AddError(string.Empty, "User division mapping not found.");
                }
            }
        }

        if (!result.Success)
        {
            return result;
        }

        user!.ActiveDivisionId = mutateModel.DivisionId;
        await _userRepository.SaveChangesAsync();

        return result;
    }

    public virtual Task<bool> DivisionAccessAsync(Guid? divisionId, IEnumerable<Claim> userClaims,
        IEnumerable<Claim>? requireClaims = null, IEnumerable<string>? requireRoles = null)
    {
        if (!divisionId.HasValue || (requireRoles?.Any() == false && requireRoles?.Any() == false))
        {
            return Task.FromResult(false);
        }

        if (!userClaims.Any(x =>
                x.Type == NhPlatformClaimTypes.Permission &&
                x.Value == Platform.Common.Constants.PermissionClaimValues.AuthenticatedAccess))
        {
            return Task.FromResult(false);
        }

        if (requireRoles?.Any() == true)
        {
            foreach (var requiredRoles in requireRoles)
            {
                if (!userClaims.Any(x =>
                        x.Type == NhPlatformClaimTypes.DivisionRole && x.Value == $"{divisionId}_{requiredRoles}"))
                {
                    return Task.FromResult(false);
                }
            }
        }

        if (requireClaims?.Any() == true)
        {
            foreach (var requiredClaim in requireClaims)
            {
                if (!userClaims.Any(x =>
                        x.Type == requiredClaim.Type && x.Value == $"{divisionId}_{requiredClaim.Value}"))
                {
                    return Task.FromResult(false);
                }
            }
        }

        return Task.FromResult(true);
    }
}