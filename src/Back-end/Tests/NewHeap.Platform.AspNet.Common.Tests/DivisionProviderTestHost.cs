using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models;
using NewHeap.Platform.AspNet.Common.Models.Mutate;
using NewHeap.Platform.AspNet.Common.Models.Options;
using NewHeap.Platform.AspNet.Common.Models.View;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Identity.Claims;
using NewHeap.Platform.Common.Models;
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

    /// <summary>The password of the users that <see cref="AddSignInUserAsync"/> adds.</summary>
    public const string SignInCredential = "Divisions1!Password";

    private readonly WebApplication _application;

    private DivisionProviderTestHost(WebApplication application, string label)
    {
        _application = application;
        Label = label;
    }

    /// <summary>The provider's name as the tests write it into their data.</summary>
    public string Label { get; }

    public IServiceProvider Services => _application.Services;

    /// <summary>
    /// The composition on an empty database of its own on <paramref name="provider"/>;
    /// <paramref name="clock"/>, when given, is the application's clock.
    /// </summary>
    public static async Task<DivisionProviderTestHost> CreateAsync(RelationalTestProvider provider, TimeProvider? clock = null)
    {
        var database = await ProviderTestDatabase.CreateAsync(provider);

        return await CreateAsync(database.UseNewHeapProvider, database.Label, clock);
    }

    /// <summary>
    /// The same composition on an EF Core InMemory database of its own. It runs the code path of an
    /// application whose unit tests use InMemory, and is no evidence of relational translation.
    /// </summary>
    public static Task<DivisionProviderTestHost> CreateInMemoryAsync(TimeProvider? clock = null)
    {
        var databaseName = $"divisions-{Guid.NewGuid():N}";

        return CreateAsync(options => options.UseInMemoryDatabase(databaseName), "in-memory", clock);
    }

    private static async Task<DivisionProviderTestHost> CreateAsync(
        Action<DbContextOptionsBuilder> useDatabase,
        string label,
        TimeProvider? clock)
    {
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
            .AddAuthentication<NhUserViewModel<NhDivisionViewModel>, NhDivisionViewModel, NhClaimViewModel>(authentication =>
            {
                // Password sign-in with division claims in the token, as an application with divisions has.
                authentication.AddUserNamePasswordAuthentication(options =>
                {
                    options.EnableRefreshToken = true;
                    options.EnableDivisions = true;
                });
            })
            .WithIdentityEntityFramework(useDatabase)
            .WithIdentity()
            .WithDbLogService(_ => { });

        if (clock is not null)
        {
            builder.Services.AddSingleton(clock);
        }

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

        return new DivisionProviderTestHost(application, label);
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

    /// <summary>Adds a user with <see cref="SignInCredential"/>, who signs in through <see cref="SignInAsync"/>.</summary>
    public async Task<Guid> AddSignInUserAsync(string name)
    {
        var id = await AddUserAsync(name);

        await using var scope = CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<NhUser>>();
        var user = await users.FindByIdAsync(id.ToString())
            ?? throw new InvalidOperationException($"User {id} does not exist.");

        var result = await users.AddPasswordAsync(user, SignInCredential);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(string.Join(", ", result.Errors.Select(error => error.Description)));
        }

        return id;
    }

    /// <summary>
    /// Signs the user in with <see cref="SignInCredential"/> through the platform's password sign-in, which
    /// puts the user's division claims into the token.
    /// </summary>
    public async Task<TaskResult<UserToken>> SignInAsync(Guid userId)
    {
        await using var scope = CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<NhUser>>();
        var user = await users.FindByIdAsync(userId.ToString())
            ?? throw new InvalidOperationException($"User {userId} does not exist.");

        var authentication = scope.ServiceProvider.GetRequiredService<INhAuthenticationService>();

        return await authentication.Authenticate(new AuthenticateRequest(user.UserName!, SignInCredential));
    }

    /// <summary>Gives the user an application permission, such as the division access-all permission.</summary>
    public async Task AddPermissionAsync(Guid userId, string permission)
    {
        await using var scope = CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<NhUser>>();
        var user = await users.FindByIdAsync(userId.ToString())
            ?? throw new InvalidOperationException($"User {userId} does not exist.");

        var result = await users.AddClaimAsync(user, new Claim(NhPlatformClaimTypes.Permission, permission));
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(string.Join(", ", result.Errors.Select(error => error.Description)));
        }
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

/// <summary>
/// The application's clock of a division test, standing still at <paramref name="now"/>, so the
/// UTC day that division lockouts count in does not depend on when the test runs.
/// </summary>
internal sealed class DivisionTestClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow()
    {
        return now.ToUniversalTime();
    }
}
