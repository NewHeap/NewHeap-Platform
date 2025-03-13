using Microsoft.AspNetCore.Identity;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models;
using NewHeap.Platform.Common.Models;
using System.Linq.Expressions;
using System.Security.Claims;

namespace NewHeap.Platform.AspNet.Common.Services;

public interface INhUserManager
{
    Task<User?> FindByIdAsync(string userId);
    
    IRepository<User> GetRepository();
    IQueryable<User> QueryableWithAllIncludes(IQueryable<User>? queryable = null);
    Task<User?> FindOneByAsync(Expression<Func<User, bool>> predicate);
    Task<List<Claim>> GetValidClaims(User user, bool withDivision = false);

    /// <summary>
    ///     Function to check if a user is allowed to login
    /// </summary>
    /// <param name="user"></param>
    /// <returns></returns>
    Task<bool> IsBlocked(User user);

    bool IsOauthAccount(string email);
    bool IsOauthAccount(User user);
    string GenerateRegistrationToken();
    string GenerateRandomPassword(PasswordOptions? passwordOptions = null);
    Task UpdateUserLockout(User user, DateTimeOffset? start = null, DateTimeOffset? end = null);
    Task<User?> FindByIdWithIncludesAsync(Guid userId);

    Task<TaskResult<User>> ChangeActiviveDivisionAsync(Guid id,
        ChangeActiveDivisionAccountModel mutateModel);

    Task<bool> DivisionAccessAsync(Guid? divisionId, IEnumerable<Claim> userClaims,
        IEnumerable<Claim>? requireClaims = null, IEnumerable<string>? requireRoles = null);

    Task<IdentityResult> CreateAsync(User user);
    Task<IdentityResult> UpdateAsync(User user);
    Task<IdentityResult> DeleteAsync(User user);
    string? NormalizeName(string? name);
    string? NormalizeEmail(string? email);
    Task<IList<string>> GetRolesAsync(User user);
    Task<bool> IsInRoleAsync(User user, string role);
    Task<IdentityResult> ConfirmEmailAsync(User user, string token);
    Task<bool> IsEmailConfirmedAsync(User user);
    Task<User> FindByEmailAsync(string requestUserName);
}