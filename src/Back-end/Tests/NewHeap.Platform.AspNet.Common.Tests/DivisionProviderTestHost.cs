using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models.Mutate;
using NewHeap.Platform.AspNet.Common.Models.Options;
using NewHeap.Platform.AspNet.Common.Models.View;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Identity.Claims;
using NewHeap.Platform.Tests.Relational;
using System.Security.Claims;

namespace NewHeap.Platform.AspNet.Common.Tests;

/// <summary>
/// The NewHeap Identity composition of an application on an empty database of its own, for the
/// division tests on each relational provider: the real user manager, division services, mapping
/// profile and division authorization handler, with helpers that seed divisions, division roles,
/// users and memberships.
/// </summary>
internal sealed class DivisionProviderTestHost : IAsyncDisposable
{
    /// <summary>A division policy that names no claim or role: membership alone decides.</summary>
    public const string MembershipPolicy = "test.division.member";

    /// <summary>A division policy that requires the <c>general.view</c> division permission.</summary>
    public const string GeneralViewPolicy = "test.division.general-view";

    private readonly WebApplication _application;

    private DivisionProviderTestHost(WebApplication application, string label)
    {
        _application = application;
        Label = label;
    }

    /// <summary>The provider's name as the tests write it into their data.</summary>
    public string Label { get; }

    public IServiceProvider Services => _application.Services;

    public static async Task<DivisionProviderTestHost> CreateAsync(RelationalTestProvider provider)
    {
        var database = await ProviderTestDatabase.CreateAsync(provider);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:Issuer"] = "https://divisions.example.test",
            ["NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:Key"] = "division-provider-tests-signing-key-2026",
        });

        builder.Services
            .AddNewHeapPlatformAspNetCommon<
                NhUser,
                NhUserRole,
                NhDivision,
                NhDivisionUser,
                NhDivisionRole,
                NhDivisionUserRole,
                NhDivisionRoleClaim,
                NhLog,
                NhLogMessageArgument,
                NhLogFile,
                NhLogMessageTranslated,
                NhDbLogService,
                DivisionTestDbContext,
                NhUserManager,
                NhDivisionService,
                NhDivisionMutateModel,
                NhDivisionUserService,
                NhDivisionUserMutateModel>(NewHeapAspNetCommonOptions.Builder(builder.Configuration).Build())
            .WithIdentityEntityFramework(database.UseNewHeapProvider)
            .WithIdentity()
            .WithDbLogService(_ => { });

        builder.Services.AddHttpContextAccessor();
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(MembershipPolicy, policy => policy.RequireActiveDivisionAccess());
            options.AddPolicy(
                GeneralViewPolicy,
                policy => policy.RequireActiveDivisionAccess(
                    null,
                    new Claim(NhPlatformClaimTypes.DivisionPermission, "general.view")));
        });

        var application = builder.Build();
        await using (var scope = application.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<DivisionTestDbContext>().Database.EnsureCreatedAsync();
        }

        return new DivisionProviderTestHost(application, database.Label);
    }

    public AsyncServiceScope CreateScope()
    {
        return _application.Services.CreateAsyncScope();
    }

    public async Task<Guid> AddDivisionAsync(string name)
    {
        var id = Guid.NewGuid();
        await WithContextAsync(context => context.Set<NhDivision>().Add(new NhDivision
        {
            Id = id,
            Name = $"{name} {Label}",
            Description = name,
            TimeZoneId = "UTC",
            UserSelectAllowed = true
        }));

        return id;
    }

    /// <summary>Adds a division role with the given division permissions.</summary>
    public async Task<Guid> AddDivisionRoleAsync(string name, params string[] permissions)
    {
        var id = Guid.NewGuid();
        await WithContextAsync(context =>
        {
            context.Set<NhDivisionRole>().Add(new NhDivisionRole { Id = id, Name = name });
            foreach (var permission in permissions)
            {
                context.Set<NhDivisionRoleClaim>().Add(new NhDivisionRoleClaim
                {
                    Id = Guid.NewGuid(),
                    DivisionRoleId = id,
                    ClaimType = NhPlatformClaimTypes.DivisionPermission,
                    ClaimValue = permission
                });
            }
        });

        return id;
    }

    public async Task<Guid> AddUserAsync(string name)
    {
        var id = Guid.NewGuid();
        var email = $"{name}-{Label}-{id:N}@divisions.example.test";
        await WithContextAsync(context => context.Set<NhUser>().Add(new NhUser
        {
            Id = id,
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString("N")
        }));

        return id;
    }

    /// <summary>Makes the user a member of the division with the given division roles.</summary>
    public async Task<Guid> AddMembershipAsync(
        Guid userId,
        Guid divisionId,
        IReadOnlyCollection<Guid> roleIds,
        DateTimeOffset? lockOutStart = null,
        DateTimeOffset? lockOutEnd = null)
    {
        var id = Guid.NewGuid();
        await WithContextAsync(context =>
        {
            context.Set<NhDivisionUser>().Add(new NhDivisionUser
            {
                Id = id,
                UserId = userId,
                DivisionId = divisionId,
                LockOutStartDateTime = lockOutStart,
                LockOutEndDateTime = lockOutEnd
            });
            foreach (var roleId in roleIds)
            {
                context.Set<NhDivisionUserRole>().Add(new NhDivisionUserRole
                {
                    DivisionUserId = id,
                    DivisionRoleId = roleId
                });
            }
        });

        return id;
    }

    public async Task<T> ReadAsync<T>(Func<DivisionTestDbContext, Task<T>> read)
    {
        await using var scope = CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DivisionTestDbContext>();
        return await read(context);
    }

    /// <summary>
    /// A signed-in request of the user with the active-division header, served by
    /// <paramref name="scope"/>; <paramref name="accessAll"/> adds the division access-all permission.
    /// </summary>
    public static DefaultHttpContext CreateRequest(
        AsyncServiceScope scope,
        Guid userId,
        Guid? activeDivisionId,
        bool accessAll = false)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(NhPlatformClaimTypes.Permission, Platform.Common.Constants.PermissionClaimValues.AuthenticatedAccess)
        };
        if (accessAll)
        {
            claims.Add(new Claim(NhPlatformClaimTypes.Permission, Platform.Common.Constants.DivisionPermissionClaimValues.AccessAll));
        }

        var httpContext = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"))
        };
        if (activeDivisionId.HasValue)
        {
            httpContext.Request.Headers[Constants.HttpHeaderKeys.ActiveDivisionId] = activeDivisionId.Value.ToString();
        }

        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = httpContext;
        return httpContext;
    }

    /// <summary>
    /// A controller of <paramref name="scope"/> that serves <paramref name="httpContext"/>;
    /// <paramref name="parameters"/> supplies constructor arguments the container does not have.
    /// </summary>
    public static TController CreateController<TController>(
        AsyncServiceScope scope,
        HttpContext httpContext,
        params object[] parameters)
        where TController : ControllerBase
    {
        var controller = ActivatorUtilities.CreateInstance<TController>(scope.ServiceProvider, parameters);
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    public ValueTask DisposeAsync()
    {
        return _application.DisposeAsync();
    }

    private async Task WithContextAsync(Action<DivisionTestDbContext> change)
    {
        await using var scope = CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DivisionTestDbContext>();
        change(context);
        await context.SaveChangesAsync();
    }
}

internal sealed class DivisionTestDbContext(DbContextOptions<DivisionTestDbContext> options) : NhIdentityDbContext(options);
