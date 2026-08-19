using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.Common.Identity.Claims;
using SampleProjectManagement.Api.Authorization;
using SampleProjectManagement.DAL;
using SampleProjectManagement.DAL.Entities;
using System.Security.Claims;

namespace SampleProjectManagement.Api.Services;

public static class SampleDevelopmentIdentitySeeder
{
    public const string DemoUserEmail = SampleAuthorizationDefaults.ManagerEmail;
    public const string DemoUserPassword = SampleAuthorizationDefaults.Password;

    public static async Task SeedAsync(IServiceProvider serviceProvider)
    {
        var roleManager = serviceProvider.GetRequiredService<RoleManager<NhUserRole>>();
        var userManager = serviceProvider.GetRequiredService<UserManager<NhUser>>();
        var dbContext = serviceProvider.GetRequiredService<SampleProjectManagementDbContext>();

        await EnsureApplicationRoleAsync(
            roleManager,
            SampleAuthorizationDefaults.ManagerRole,
            [
                "app.project.view",
                "app.project.manage",
                "app.project.confidential.view"
            ]);
        await EnsureApplicationRoleAsync(
            roleManager,
            SampleAuthorizationDefaults.ViewerRole,
            ["app.project.view"]);

        var manager = await EnsureUserAsync(
            userManager,
            SampleAuthorizationDefaults.ManagerEmail);
        var viewer = await EnsureUserAsync(
            userManager,
            SampleAuthorizationDefaults.ViewerEmail);
        var divisionEditor = await EnsureUserAsync(
            userManager,
            SampleAuthorizationDefaults.DivisionEditorEmail);
        var projectEditor = await EnsureUserAsync(
            userManager,
            SampleAuthorizationDefaults.ProjectEditorEmail);

        await EnsureUserRoleAsync(
            userManager,
            manager,
            SampleAuthorizationDefaults.ManagerRole);
        await EnsureUserRoleAsync(
            userManager,
            viewer,
            SampleAuthorizationDefaults.ViewerRole);

        await EnsureDivisionAsync(
            dbContext,
            SampleAuthorizationDefaults.NorthDivisionId,
            "Sample North",
            "Division used by the division- and project-permission samples.");
        await EnsureDivisionAsync(
            dbContext,
            SampleAuthorizationDefaults.SouthDivisionId,
            "Sample South",
            "Second division used to prove that division access is scoped.");

        var divisionEditorRole = await EnsureDivisionRoleAsync(
            dbContext,
            SampleAuthorizationDefaults.DivisionEditorRole,
            [
                "general.view",
                "project.view",
                "project.manage",
                "project.confidential.view"
            ]);
        var projectMemberRole = await EnsureDivisionRoleAsync(
            dbContext,
            SampleAuthorizationDefaults.ProjectMemberRole,
            ["general.view"]);

        await EnsureDivisionAssignmentAsync(
            dbContext,
            manager.Id,
            SampleAuthorizationDefaults.NorthDivisionId,
            projectMemberRole.Id);
        await EnsureDivisionAssignmentAsync(
            dbContext,
            manager.Id,
            SampleAuthorizationDefaults.SouthDivisionId,
            projectMemberRole.Id);
        await EnsureDivisionAssignmentAsync(
            dbContext,
            viewer.Id,
            SampleAuthorizationDefaults.NorthDivisionId,
            projectMemberRole.Id);
        await EnsureDivisionAssignmentAsync(
            dbContext,
            divisionEditor.Id,
            SampleAuthorizationDefaults.NorthDivisionId,
            divisionEditorRole.Id);
        await EnsureDivisionAssignmentAsync(
            dbContext,
            projectEditor.Id,
            SampleAuthorizationDefaults.NorthDivisionId,
            projectMemberRole.Id);

        await SetActiveDivisionAsync(
            userManager,
            manager,
            SampleAuthorizationDefaults.NorthDivisionId);
        await SetActiveDivisionAsync(
            userManager,
            viewer,
            SampleAuthorizationDefaults.NorthDivisionId);
        await SetActiveDivisionAsync(
            userManager,
            divisionEditor,
            SampleAuthorizationDefaults.NorthDivisionId);
        await SetActiveDivisionAsync(
            userManager,
            projectEditor,
            SampleAuthorizationDefaults.NorthDivisionId);

        await EnsureProjectAsync(
            dbContext,
            SampleAuthorizationDefaults.AlphaProjectId,
            SampleAuthorizationDefaults.NorthDivisionId,
            manager.Id,
            "AUTH-ALPHA",
            "Authorization Alpha");
        await EnsureProjectAsync(
            dbContext,
            SampleAuthorizationDefaults.BetaProjectId,
            SampleAuthorizationDefaults.NorthDivisionId,
            manager.Id,
            "AUTH-BETA",
            "Authorization Beta");

        await EnsureUserClaimAsync(
            userManager,
            projectEditor,
            new Claim(
                SampleProjectClaimTypes.ProjectPermission,
                SampleProjectClaimTypes.PermissionValue(
                    SampleAuthorizationDefaults.AlphaProjectId,
                    "confidential.view")));
        await EnsureUserClaimAsync(
            userManager,
            projectEditor,
            new Claim(
                SampleProjectClaimTypes.ProjectRole,
                SampleProjectClaimTypes.RoleValue(
                    SampleAuthorizationDefaults.AlphaProjectId,
                    "project-editor")));
    }

    private static async Task EnsureApplicationRoleAsync(
        RoleManager<NhUserRole> roleManager,
        string roleName,
        IReadOnlyCollection<string> permissions)
    {
        if (!await roleManager.RoleExistsAsync(roleName))
        {
            EnsureSucceeded(
                await roleManager.CreateAsync(new NhUserRole(roleName)),
                $"{roleName} role");
        }

        var role = await roleManager.FindByNameAsync(roleName)
            ?? throw new InvalidOperationException($"Role '{roleName}' could not be loaded.");
        var roleClaims = await roleManager.GetClaimsAsync(role);

        foreach (var permission in permissions)
        {
            if (roleClaims.Any(claim =>
                    claim.Type == NhPlatformClaimTypes.Permission &&
                    claim.Value == permission))
            {
                continue;
            }

            EnsureSucceeded(
                await roleManager.AddClaimAsync(
                    role,
                    new Claim(NhPlatformClaimTypes.Permission, permission)),
                $"{roleName} {permission} claim");
        }
    }

    private static async Task<NhUser> EnsureUserAsync(
        UserManager<NhUser> userManager,
        string email)
    {
        var user = await userManager.FindByEmailAsync(email);
        if (user is not null)
        {
            return user;
        }

        user = new NhUser
        {
            Email = email,
            UserName = email,
            EmailConfirmed = true
        };

        EnsureSucceeded(
            await userManager.CreateAsync(user, SampleAuthorizationDefaults.Password),
            $"{email} demo user");
        return user;
    }

    private static async Task EnsureUserRoleAsync(
        UserManager<NhUser> userManager,
        NhUser user,
        string roleName)
    {
        if (!await userManager.IsInRoleAsync(user, roleName))
        {
            EnsureSucceeded(
                await userManager.AddToRoleAsync(user, roleName),
                $"{user.Email} role assignment");
        }
    }

    private static async Task EnsureDivisionAsync(
        SampleProjectManagementDbContext dbContext,
        Guid id,
        string name,
        string description)
    {
        if (await dbContext.Divisions.AnyAsync(division => division.Id == id))
        {
            return;
        }

        dbContext.Divisions.Add(new NhDivision
        {
            Id = id,
            Name = name,
            Description = description,
            TimeZoneId = "Europe/Amsterdam",
            UserSelectAllowed = true
        });
        await dbContext.SaveChangesAsync();
    }

    private static async Task<NhDivisionRole> EnsureDivisionRoleAsync(
        SampleProjectManagementDbContext dbContext,
        string roleName,
        IReadOnlyCollection<string> permissions)
    {
        var role = await dbContext.DivisionRoles
            .SingleOrDefaultAsync(item => item.Name == roleName);
        if (role is null)
        {
            role = new NhDivisionRole
            {
                Id = Guid.NewGuid(),
                Name = roleName
            };
            dbContext.DivisionRoles.Add(role);
            await dbContext.SaveChangesAsync();
        }

        var existingClaims = await dbContext.DivisionRoleClaims
            .Where(claim => claim.DivisionRoleId == role.Id)
            .ToListAsync();
        foreach (var permission in permissions)
        {
            if (existingClaims.Any(claim =>
                    claim.ClaimType == NhPlatformClaimTypes.DivisionPermission &&
                    claim.ClaimValue == permission))
            {
                continue;
            }

            dbContext.DivisionRoleClaims.Add(new NhDivisionRoleClaim
            {
                Id = Guid.NewGuid(),
                DivisionRoleId = role.Id,
                ClaimType = NhPlatformClaimTypes.DivisionPermission,
                ClaimValue = permission
            });
        }

        await dbContext.SaveChangesAsync();
        return role;
    }

    private static async Task EnsureDivisionAssignmentAsync(
        SampleProjectManagementDbContext dbContext,
        Guid userId,
        Guid divisionId,
        Guid divisionRoleId)
    {
        var divisionUser = await dbContext.DivisionUsers
            .SingleOrDefaultAsync(item =>
                item.UserId == userId && item.DivisionId == divisionId);
        if (divisionUser is null)
        {
            divisionUser = new NhDivisionUser
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                DivisionId = divisionId
            };
            dbContext.DivisionUsers.Add(divisionUser);
            await dbContext.SaveChangesAsync();
        }

        if (!await dbContext.DivisionUserRoles.AnyAsync(item =>
                item.DivisionUserId == divisionUser.Id &&
                item.DivisionRoleId == divisionRoleId))
        {
            dbContext.DivisionUserRoles.Add(new NhDivisionUserRole
            {
                DivisionUserId = divisionUser.Id,
                DivisionRoleId = divisionRoleId
            });
            await dbContext.SaveChangesAsync();
        }
    }

    private static async Task SetActiveDivisionAsync(
        UserManager<NhUser> userManager,
        NhUser user,
        Guid divisionId)
    {
        if (user.ActiveDivisionId == divisionId)
        {
            return;
        }

        user.ActiveDivisionId = divisionId;
        EnsureSucceeded(
            await userManager.UpdateAsync(user),
            $"{user.Email} active division");
    }

    private static async Task EnsureProjectAsync(
        SampleProjectManagementDbContext dbContext,
        Guid id,
        Guid divisionId,
        Guid ownerUserId,
        string key,
        string name)
    {
        if (await dbContext.Projects.AnyAsync(project => project.Id == id))
        {
            return;
        }

        dbContext.Projects.Add(new Project
        {
            Id = id,
            DivisionId = divisionId,
            OwnerUserId = ownerUserId,
            Key = key,
            Name = name,
            Description = "Seeded authorization sample project.",
            Status = ProjectStatus.Active
        });
        await dbContext.SaveChangesAsync();
    }

    private static async Task EnsureUserClaimAsync(
        UserManager<NhUser> userManager,
        NhUser user,
        Claim claim)
    {
        var claims = await userManager.GetClaimsAsync(user);
        if (!claims.Any(item => item.Type == claim.Type && item.Value == claim.Value))
        {
            EnsureSucceeded(
                await userManager.AddClaimAsync(user, claim),
                $"{user.Email} {claim.Type} claim");
        }
    }

    private static void EnsureSucceeded(IdentityResult result, string operation)
    {
        if (result.Succeeded)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Unable to create the {operation}: " +
            string.Join(", ", result.Errors.Select(error => error.Description)));
    }
}
