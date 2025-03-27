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
    Task<NhUser?> FindByIdAsync(string userId);
    
    IRepository<NhUser> GetRepository();
    IQueryable<NhUser> QueryableWithAllIncludes(IQueryable<NhUser>? queryable = null);
    Task<NhUser?> FindOneByAsync(Expression<Func<NhUser, bool>> predicate);
    Task<List<Claim>> GetValidClaims(NhUser user, bool withDivision = false);

    /// <summary>
    ///     Function to check if a user is allowed to login
    /// </summary>
    /// <param name="user"></param>
    /// <returns></returns>
    Task<bool> IsBlocked(NhUser user);

    bool IsOauthAccount(string email);
    bool IsOauthAccount(NhUser user);
    string GenerateRegistrationToken();
    string GenerateRandomPassword(PasswordOptions? passwordOptions = null);
    Task UpdateUserLockout(NhUser user, DateTimeOffset? start = null, DateTimeOffset? end = null);
    Task<NhUser?> FindByIdWithIncludesAsync(Guid userId);

    Task<TaskResult<NhUser>> ChangeActiviveDivisionAsync(Guid id,
        ChangeActiveDivisionAccountModel mutateModel);

    Task<bool> DivisionAccessAsync(Guid? divisionId, IEnumerable<Claim> userClaims,
        IEnumerable<Claim>? requireClaims = null, IEnumerable<string>? requireRoles = null);

    Task<IdentityResult> CreateAsync(NhUser user);
    Task<IdentityResult> UpdateAsync(NhUser user);
    Task<IdentityResult> DeleteAsync(NhUser user);
    string? NormalizeName(string? name);
    string? NormalizeEmail(string? email);
    Task<IList<string>> GetRolesAsync(NhUser user);
    Task<bool> IsInRoleAsync(NhUser user, string role);
    Task<IdentityResult> ConfirmEmailAsync(NhUser user, string token);
    Task<bool> IsEmailConfirmedAsync(NhUser user);
    Task<NhUser> FindByEmailAsync(string requestUserName);
}