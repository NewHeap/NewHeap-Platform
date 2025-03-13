using Hangfire;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Identity.Claims;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using WebAPI.DAL;

namespace WebAPI.Jobs;

public class DatabaseJobs
{
    private readonly IServiceScopeFactory _serviceScopeFactory;


    public DatabaseJobs(IServiceScopeFactory serviceScopeFactory)
    {
        _serviceScopeFactory = serviceScopeFactory ?? throw new ArgumentNullException(nameof(serviceScopeFactory));
    }

    #region Seeding

    /// <summary>
    ///     Seed database
    /// </summary>
    /// <returns></returns>
    [AutomaticRetry(Attempts = 0)]
    public async Task Seed()
    {
        using (var scope = _serviceScopeFactory.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            await SeedRoles(scope);

            await SeedDivisionRoles(scope);

            await SeedUsers(scope, dbContext);
        }
    }

    private async Task SeedRoles(IServiceScope scope)
    {
        var userRoles = new[] { "SuperAdministrator", "Administrator", "User" };

        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<UserRole>>();

        Func<UserRole, IEnumerable<Claim>, Task> addIdentityRoleClaims = async (identityRole, claims) =>
        {
            var identityRoleClaims = await roleManager.GetClaimsAsync(identityRole);

            foreach (var claim in claims)
            {
                var existingClaim =
                    identityRoleClaims.FirstOrDefault(x => x.Type.Equals(claim.Type) && x.Value.Equals(claim.Value));
                if (null == existingClaim)
                {
                    await roleManager.AddClaimAsync(identityRole, claim);
                }
            }

            var removed = identityRoleClaims
                .Where(x => !claims.Any(y => x.Type == y.Type && x.Value == y.Value))
                .ToList();

            foreach (var claim in removed)
            {
                await roleManager.RemoveClaimAsync(identityRole, claim);
            }
        };

        foreach (var role in userRoles)
        {
            UserRole identityRole = new(role);
            if (!await roleManager.RoleExistsAsync(role))
            {
                await roleManager.CreateAsync(identityRole);
            }

            identityRole = await roleManager.FindByNameAsync(role);

            if (identityRole.Name == "SuperAdministrator" || identityRole.Name == "Administrator")
            {
                List<Claim> identityRoleClaims = new()
                {
                    new Claim(NhPlatformClaimTypes.Permission, "app.general.minimum-administrator"),
                    new Claim(NhPlatformClaimTypes.Permission, "app.hip.access"),
                    new Claim(NhPlatformClaimTypes.Permission, "app.settings.view"),
                    new Claim(NhPlatformClaimTypes.Permission, "app.settings.menu.view"),
                    new Claim(NhPlatformClaimTypes.Permission, "app.settings.update"),
                    new Claim(NhPlatformClaimTypes.Permission, "app.hangfire.administrator"),
                    new Claim(NhPlatformClaimTypes.Permission, "app.user.view"),
                    new Claim(NhPlatformClaimTypes.Permission, "app.user.menu.view"),
                    new Claim(NhPlatformClaimTypes.Permission, "app.user.create"),
                    new Claim(NhPlatformClaimTypes.Permission, "app.user.update"),
                    new Claim(NhPlatformClaimTypes.Permission, "app.user.delete"),
                    new Claim(NhPlatformClaimTypes.Permission, "app.user.lockout"),
                    new Claim(NhPlatformClaimTypes.Permission, "app.person.view"),
                    new Claim(NhPlatformClaimTypes.Permission, "app.log.view"),
                    new Claim(NhPlatformClaimTypes.Permission, "app.log.menu.view"),
                    new Claim(NhPlatformClaimTypes.Permission, "app.log.create"),
                    new Claim(NhPlatformClaimTypes.Permission, "app.log.update"),
                    new Claim(NhPlatformClaimTypes.Permission, "app.log.delete"),
                    new Claim(NhPlatformClaimTypes.Permission, "app.division.view"),
                    new Claim(NhPlatformClaimTypes.Permission, "app.division.menu.view"),
                    new Claim(NhPlatformClaimTypes.Permission, "app.division.create"),
                    new Claim(NhPlatformClaimTypes.Permission, "app.division.update"),
                    new Claim(NhPlatformClaimTypes.Permission, "app.division.delete"),
                    new Claim(NhPlatformClaimTypes.Permission, "app.division.user.mutate"),
                    new Claim(NhPlatformClaimTypes.Permission, "app.division.access-all"),
                    new Claim(NhPlatformClaimTypes.Permission, "app.address.view"),
                    new Claim(NhPlatformClaimTypes.Permission, "app.address.manage")
                };

                if (identityRole.Name == "SuperAdministrator")
                {
                    identityRoleClaims.AddRange(new[]
                    {
                        new Claim(NhPlatformClaimTypes.Permission, "app.developer.general"),
                        new Claim(NhPlatformClaimTypes.Permission, "app.general.maximum-administrator")
                    });
                }

                await addIdentityRoleClaims(identityRole, identityRoleClaims);
            }

            var defaultPermissionRoles = new[] { "User" };

            if (defaultPermissionRoles.Contains(identityRole.Name))
            {
                var identityRoleClaims = new[]
                {
                    new Claim(NhPlatformClaimTypes.Permission, "app.hip.access"),
                    new Claim(NhPlatformClaimTypes.Permission, "app.user.view"),
                    new Claim(NhPlatformClaimTypes.Permission, "app.person.view"),
                    new Claim(NhPlatformClaimTypes.Permission, "app.log.view"),
                    new Claim(NhPlatformClaimTypes.Permission, "app.division.view"),
                    new Claim(NhPlatformClaimTypes.Permission, "app.address.view"),
                    new Claim(NhPlatformClaimTypes.Permission, "app.address.manage")
                };

                await addIdentityRoleClaims(identityRole, identityRoleClaims);
            }
        }
    }

    private async Task SeedUsers(IServiceScope scope, AppDbContext dbContext)
    {
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();

        var user = await userManager.FindByNameAsync("info@newheap.com");
        if (user == null)
        {
            var email = "info@newheap.com";
            User userToInsert = new()
            {
                Email = email,
                UserName = email,
                EmailConfirmed = true,
                RefreshToken = "",
                Id = Guid.Parse("07e35556-54f2-4975-a563-417eb5fbfa7d")
            };

            var result = await userManager.CreateAsync(userToInsert, "NewHeap123!");

            //Add roles
            await userManager.AddToRoleAsync(await userManager.FindByNameAsync("info@newheap.com"),
                "SuperAdministrator");
        }

        var backgroundWorkerUserEmail = configuration["AppSettings:SystemUser"];
        if (!string.IsNullOrWhiteSpace(backgroundWorkerUserEmail))
        {
            var backgroundWorkerUser = await userManager.FindByNameAsync(backgroundWorkerUserEmail);
            if (backgroundWorkerUser == null)
            {
                User userToInsert = new()
                {
                    Email = backgroundWorkerUserEmail,
                    UserName = backgroundWorkerUserEmail,
                    EmailConfirmed = true,
                    Id = Guid.Parse("d1c237fe-7d51-476f-8412-4d2424114ce6")
                };

                var result =
                    await userManager.CreateAsync(userToInsert, configuration["AppSettings:SystemUserPassword"]!);

                //Add roles
                await userManager.AddToRoleAsync((await userManager.FindByNameAsync(backgroundWorkerUserEmail))!,
                    "SuperAdministrator");
            }
        }

        await dbContext.SaveChangesAsync();
    }

    private async Task SeedDivisionRoles(IServiceScope scope)
    {
        var divisionRoleNames = new[] { "SuperAdministrator", "Administrator", "User" };
        var divisionManager = scope.ServiceProvider.GetRequiredService<DivisionService>();

        Func<DivisionRole, IEnumerable<Claim>, Task> addDivisionRoleClaims = async (divisionRole, claims) =>
        {
            var divisionRoleClaimRepository = divisionManager.GetRoleClaimRepository();
            var divisionRoleClaims = await divisionRoleClaimRepository.GetAll()
                .Where(x => x.DivisionRoleId == divisionRole.Id).ToListAsync();

            List<DivisionRoleClaim> newRoleClaims = new();

            foreach (var claim in claims)
            {
                var existingClaim = divisionRoleClaims.FirstOrDefault(x =>
                    x.ClaimType.Equals(claim.Type) && x.ClaimValue.Equals(claim.Value));
                if (null == existingClaim)
                {
                    newRoleClaims.Add(new DivisionRoleClaim
                    {
                        DivisionRoleId = divisionRole.Id, ClaimType = claim.Type, ClaimValue = claim.Value
                    });
                }
            }

            if (newRoleClaims.Any())
            {
                await divisionRoleClaimRepository.AddRangeAsync(newRoleClaims);
            }

            var removed = divisionRoleClaims
                .Where(x => !claims.Any(y => x.ClaimType == y.Type && x.ClaimValue == y.Value))
                .ToList();

            if (removed.Any())
            {
                divisionRoleClaimRepository.RemoveRange(removed);
            }

            if (newRoleClaims.Any() || removed.Any())
            {
                await divisionRoleClaimRepository.SaveChangesAsync();
            }
        };

        foreach (var divisionRoleName in divisionRoleNames)
        {
            if (!await divisionManager.RoleExistsAsync(divisionRoleName))
            {
                await divisionManager.RoleCreateAsync(divisionRoleName);
            }
        }

        await divisionManager.GetRoleRepository().GetAll().Where(x => !divisionRoleNames.Contains(x.Name))
            .ExecuteDeleteAsync();

        var divisionRoles = await divisionManager.GetRoleRepository().GetAll().ToListAsync();

        foreach (var divisionRole in divisionRoles)
        {
            if (divisionRole.Name == "SuperAdministrator")
            {
                var divisionRoleClaims =
                    new[] { new Claim(NhPlatformClaimTypes.DivisionPermission, "general.view") };

                await addDivisionRoleClaims(divisionRole, divisionRoleClaims);
            }

            if (divisionRole.Name == "Administrator")
            {
                var divisionRoleClaims =
                    new[] { new Claim(NhPlatformClaimTypes.DivisionPermission, "general.view") };

                await addDivisionRoleClaims(divisionRole, divisionRoleClaims);
            }

            if (divisionRole.Name == "User")
            {
                var divisionRoleClaims =
                    new[] { new Claim(NhPlatformClaimTypes.DivisionPermission, "general.view") };

                await addDivisionRoleClaims(divisionRole, divisionRoleClaims);
            }
        }
    }

    #endregion
}