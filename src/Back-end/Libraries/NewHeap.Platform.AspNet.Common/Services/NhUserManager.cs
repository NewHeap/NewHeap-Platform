using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.Common.Identity.Claims;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.AspNet.Common.Models;
using NewHeap.Platform.Common.Models.Options;
using NewHeap.Platform.AspNet.Common.DAL;

namespace NewHeap.Platform.AspNet.Common.Services;

public partial class NhUserManager : UserManager<User>
{
    private readonly MicrosoftAuthSettings _microsoftAuthSettings;
    private readonly IWebHostEnvironment _environment;
    protected readonly IRepository<User> _userRepository;
    protected readonly RoleManager<UserRole> _roleManager;

    public NhUserManager(
        IWebHostEnvironment environment,
        IUserStore<User> store, 
        IOptions<IdentityOptions> optionsAccessor, 
        IPasswordHasher<User> passwordHasher, 
        IEnumerable<IUserValidator<User>> userValidators, 
        IEnumerable<IPasswordValidator<User>> passwordValidators, 
        ILookupNormalizer keyNormalizer, 
        IdentityErrorDescriber errors, 
        IServiceProvider services, 
        ILogger<UserManager<User>> logger,
        IOptions<MicrosoftAuthSettings> microsoftAuthSettings,
        IRepository<User> userRepository,
        RoleManager<UserRole> roleManager
        ) : base(store, optionsAccessor, passwordHasher, userValidators, passwordValidators, keyNormalizer, errors, services, logger)
    {
        _microsoftAuthSettings = microsoftAuthSettings.Value;
        _environment = environment;
        _userRepository = userRepository;
        _roleManager = roleManager;
    }

    public virtual IRepository<User> GetRepository()
    {
        return _userRepository;
    }

    private IQueryable<User> QueryableWithAllIncludes(IQueryable<User> queryable = null)
    {
        queryable ??= _userRepository.GetAll()
            .Include(x => x.ActiveDivision);

        return queryable;
    }

    public virtual async Task<User> FindOneByAsync(Expression<Func<User, bool>> predicate)
    {
        return await QueryableWithAllIncludes().FirstOrDefaultAsync(predicate);
    }

    public virtual async Task<List<Claim>> GetValidClaims(User user, bool withDivision = false)
    {
        IdentityOptions _options = new IdentityOptions();
        var claims = new List<Claim>
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Email),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Name, user.UserName),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
            new Claim(JwtRegisteredClaimNames.NameId, user.Id.ToString()),
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())
        };

        var userClaims = await GetClaimsAsync(user);
        var userRoles = await GetRolesAsync(user);

        claims.AddRange(userClaims);

        foreach (var userRole in userRoles)
        {
            claims.Add(new Claim(ClaimTypes.Role, userRole));
            var role = await _roleManager.FindByNameAsync(userRole);
            if (role != null)
            {
                var roleClaims = await _roleManager.GetClaimsAsync(role);
                foreach (Claim roleClaim in roleClaims)
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
            var divisionAccessAll = claims.Any(x => x.Type == NhPlatformClaimTypes.Permission && x.Value == Platform.Common.Constants.DivisionPermissionClaimValues.AccessAll);
            var divisionsQuery = _userRepository.GetDbSet<Division>().AsNoTracking() as IQueryable<Division>;

            if (!divisionAccessAll)
            {
                divisionsQuery = divisionsQuery.Where(x => x.DivisionUsers.Any(c =>
                    c.UserId == user.Id
                    && c.LockOutStartDateTime == null ? true : !(c.LockOutStartDateTime.Value.Date <= DateTimeOffset.Now.Date)
                    && c.LockOutEndDateTime == null ? true : !(c.LockOutEndDateTime.Value.Date >= DateTimeOffset.Now.Date)
                ));
            }

            var divisionRolesClaims = await _userRepository.GetDbSet<DivisionRoleClaim>().AsNoTracking().ToListAsync();
            var divisionRolesClaimClaims = new List<Claim>();

            var divisionIds = await divisionsQuery.Select(x => x.Id).ToListAsync();
            foreach (var divisionId in divisionIds)
            {
                var divisionRolesQuery = _userRepository.GetDbSet<DivisionRole>().AsNoTracking() as IQueryable<DivisionRole>;
                if (!divisionAccessAll)
                {
                    divisionRolesQuery = divisionRolesQuery.Where(x => x.DivisionUserRoles.Any(c => c.DivisionUser.UserId == user.Id && c.DivisionUser.DivisionId == divisionId));
                }

                // List of roles this user has in this division
                var divisionRoles = await divisionRolesQuery.Select(x => new { x.Id, x.Name }).ToListAsync();

                foreach (var divisionRole in divisionRoles)
                {
                    var claimValue = divisionId.ToString() + "_" + divisionRole.Name;
                    var claim = divisionRolesClaimClaims.FirstOrDefault(x => x.Type == NhPlatformClaimTypes.DivisionRole && x.Value == claimValue);
                    if (claim == null)
                    {
                        claim = new Claim(NhPlatformClaimTypes.DivisionRole, claimValue);
                        divisionRolesClaimClaims.Add(claim);
                    }
                }

                foreach (var divisionRoleClaim in divisionRolesClaims.Where(x => divisionRoles.Any(c => c.Id == x.DivisionRoleId))
                    .GroupBy(x => new { x.ClaimType, x.ClaimValue })
                    .Select(x => x.FirstOrDefault()))
                {
                    var claimValue = divisionId.ToString() + "_" + divisionRoleClaim.ClaimValue;
                    var claim = divisionRolesClaimClaims.FirstOrDefault(x => x.Type == divisionRoleClaim.ClaimType && x.Value == claimValue);
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
    /// Function to check if a user is allowed to login
    /// </summary>
    /// <param name="user"></param>
    /// <returns></returns>
    public virtual async Task<bool> IsBlocked(User user)
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
        return _microsoftAuthSettings.AuthDomains?.Contains(email.Split(new[] {'@'})[1],
            StringComparer.InvariantCultureIgnoreCase) == true;
    }

    public virtual bool IsOauthAccount(User user)
    {
        return IsOauthAccount(user.NormalizedEmail);
    }

    public virtual string GenerateRegistrationToken()
    {
        return Guid.NewGuid().ToString() + '-' + Guid.NewGuid().ToString();
    }

    public virtual string GenerateRandomPassword(PasswordOptions passwordOptions = null)
    {
        if (passwordOptions == null)
        {
            passwordOptions = Options.Password;
        }

        string[] randomChars = new[] {
            "ABCDEFGHJKLMNOPQRSTUVWXYZ",    // uppercase 
            "abcdefghijkmnopqrstuvwxyz",    // lowercase
            "0123456789",                   // digits
            "!@$?_-"                        // non-alphanumeric
        };

        Random rand = new Random(Environment.TickCount);
        List<char> chars = new List<char>();

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

        for (int i = chars.Count; i < passwordOptions.RequiredLength
            || chars.Distinct().Count() < passwordOptions.RequiredUniqueChars; i++)
        {
            string rcs = randomChars[rand.Next(0, randomChars.Length)];
            chars.Insert(rand.Next(0, chars.Count), rcs[rand.Next(0, rcs.Length)]);
        }

        return new string(chars.ToArray());
    }

    public virtual async Task UpdateUserLockout(User user, DateTimeOffset? start = null, DateTimeOffset? end = null)
    {
        if (user == null)
        {
            throw new ArgumentNullException(nameof(user));
        }
        await SetLockoutEndDateAsync(user, end);
        var userEntity = await _userRepository.FindOneByAsync(x => x.Id == user.Id);
        userEntity.LockoutStart = start;
        await _userRepository.SaveChangesAsync();
    }

    public Task<User> FindByIdWithIncludesAsync(Guid userId)
    {
        return _userRepository
            .GetAll()
            .Where(x => x.Id == userId)
            .Include(x => x.ActiveDivision)
            .FirstOrDefaultAsync()
        ;
    }

    public virtual async Task<TaskResult<User>> ChangeActiviveDivisionAsync(Guid id, ChangeActiveDivisionAccountModel mutateModel)
    {
        var result = new TaskResult<User>();

        var user = await _userRepository.GetAll()
            .Where(x => x.Id == id)
            .FirstOrDefaultAsync();

        if (user == null)
        {
            result.AddError(string.Empty, "User not found.");
        }

        if (mutateModel.DivisionId.HasValue)
        {
            var claims = await GetValidClaims(user);
            if (!claims.Any(x => x.Type == NhPlatformClaimTypes.Permission && x.Value == Platform.Common.Constants.DivisionPermissionClaimValues.AccessAll))
            {
                if (!await _userRepository.GetDbSet<DivisionUser>().AnyAsync(x => x.UserId == id && x.DivisionId == mutateModel.DivisionId))
                {
                    result.AddError(string.Empty, "User division mapping not found.");
                }
            }
        }

        if (!result.Success)
        {
            return result;
        }

        user.ActiveDivisionId = mutateModel.DivisionId;
        await _userRepository.SaveChangesAsync();

        return result;
    }

    public virtual Task<bool> DivisionAccessAsync(Guid? divisionId, IEnumerable<Claim> userClaims, IEnumerable<Claim> requireClaims = null, IEnumerable<string> requireRoles = null)
    {
        if (!divisionId.HasValue || (requireRoles?.Any() == false && requireRoles?.Any() == false))
        {
            return Task.FromResult(false);
        }

        if (!userClaims.Any(x => x.Type == NhPlatformClaimTypes.Permission && x.Value == Platform.Common.Constants.PermissionClaimValues.AuthenticatedAccess))
        {
            return Task.FromResult(false);
        }

        if (requireRoles?.Any() == true)
        {
            foreach (var requiredRoles in requireRoles)
            {
                if (!userClaims.Any(x => x.Type == NhPlatformClaimTypes.DivisionRole && x.Value == $"{divisionId}_{requiredRoles}"))
                {
                    return Task.FromResult(false);
                }
            }
        }

        if (requireClaims?.Any() == true)
        {
            foreach (var requiredClaim in requireClaims)
            {
                if (!userClaims.Any(x => x.Type == requiredClaim.Type && x.Value == $"{divisionId}_{requiredClaim.Value}"))
                {
                    return Task.FromResult(false);
                }
            }
        }

        return Task.FromResult(true);
    }
}
