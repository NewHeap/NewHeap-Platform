using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AspNet.Common;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models.Mutate;
using NewHeap.Platform.AspNet.Common.Models.Options;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common;

namespace NewHeap.Platform.AspNet.Common;

public static partial class ServiceCollectionExtensions
{
    public static NewHeapPlatformAspNetCommonConfigurator<
        TUser,
        TUserRole,
        TDivision,
        TDivisionUser,
        TDivisionRole,
        TDivisionUserRole,
        TDivisionRoleClaim,
        TLog,
        TLogMessageArgument,
        TLogFile,
        TLogMessageTranslated,
        TDbLogService,
        TDbContext,
        TUserManager,
        TDivisionService,
        TDivisionMutateModel,
        TDivisionUserService,
        TDivisionUserMutateModel
    > AddNewHeapPlatformAspNetCommon<
        TUser,
        TUserRole,
        TDivision,
        TDivisionUser,
        TDivisionRole,
        TDivisionUserRole,
        TDivisionRoleClaim,
        TLog,
        TLogMessageArgument,
        TLogFile,
        TLogMessageTranslated,
        TDbLogService,
        TDbContext,
        TUserManager,
        TDivisionService,
        TDivisionMutateModel,
        TDivisionUserService,
        TDivisionUserMutateModel
    >(
        this IServiceCollection services,
        NewHeapAspNetCommonOptions optionsObj
    )
        where TUser : NhUser<TDivision, TDivisionUser, TDivisionUserRole, TDivisionRole, TDivisionRoleClaim, TUser>, new()
        where TUserRole : NhUserRole, new()
        where TDivision : NhDivision<TDivisionUser, TDivisionUserRole, TDivisionRole, TDivisionRoleClaim, TDivision, TUser>, new()
        where TDivisionUser : NhDivisionUser<TDivisionUserRole, TDivisionUser, TDivisionRole, TDivisionRoleClaim, TDivision, TUser>, new()
        where TDivisionRole : NhDivisionRole<TDivisionUserRole, TDivisionRoleClaim, TDivisionUser, TDivisionRole, TDivision, TUser>, new()
        where TDivisionUserRole : NhDivisionUserRole<TDivisionUser, TDivisionRole, TDivisionRoleClaim, TDivisionUserRole, TDivision, TUser>, new()
        where TDivisionRoleClaim : NhDivisionRoleClaim, new()
        where TLog : NhLog<TUser, TLogMessageArgument, TLogMessageTranslated, TLogFile, TDivision, TDivisionUser, TDivisionRole, TDivisionUserRole, TDivisionRoleClaim>, new()
        where TLogMessageArgument : NhLogMessageArgument, new()
        where TLogFile : NhLogFile, new()
        where TLogMessageTranslated : NhLogMessageTranslated, new()
        where TDbLogService : NhDbLogService<
            TLog,
            TUser,
            TLogMessageArgument,
            TLogMessageTranslated,
            TLogFile,
            TDivision,
            TDivisionUser,
            TDivisionRole,
            TDivisionUserRole,
            TDivisionRoleClaim
        >
        where TDbContext : NhIdentityDbContext<
            TDivision,
            TDivisionUser,
            TDivisionRole,
            TDivisionUserRole,
            TDivisionRoleClaim,
            TUser,
            TUserRole,
            TLog,
            TLogMessageArgument,
            TLogFile,
            TLogMessageTranslated
        >
        where TUserManager : UserManager<TUser>, INhUserManager<TUser>
        where TDivisionService : NhDivisionService<TUser, TDivision, TDivisionUser, TDivisionRole, TDivisionUserRole, TDivisionRoleClaim, TDivisionMutateModel>
        where TDivisionMutateModel : NhDivisionMutateModel
        where TDivisionUserService : NhDivisionUserService<TUser, TDivision, TDivisionUser, TDivisionRole, TDivisionUserRole, TDivisionRoleClaim, TDivisionUserMutateModel>
        where TDivisionUserMutateModel : NhDivisionUserMutateModel
    {
        if (services == null)
        {
            throw new ArgumentNullException(nameof(services));
        }

        if (optionsObj == null)
        {
            throw new ArgumentNullException(nameof(optionsObj));
        }

        var commonConfigurator = services.AddNewHeapPlatformCommon(optionsObj.CommonOptions);

        return new NewHeapPlatformAspNetCommonConfigurator<
            TUser,
            TUserRole,
            TDivision,
            TDivisionUser,
            TDivisionRole,
            TDivisionUserRole,
            TDivisionRoleClaim,
            TLog,
            TLogMessageArgument,
            TLogFile,
            TLogMessageTranslated,
            TDbLogService,
            TDbContext,
            TUserManager,
            TDivisionService,
            TDivisionMutateModel,
            TDivisionUserService,
            TDivisionUserMutateModel
        >(services, commonConfigurator, optionsObj);
    }

    public static NewHeapPlatformAspNetCommonApplicationBuilder UseNewHeapPlatformAspNetCommon(
        this IApplicationBuilder app,
        IWebHostEnvironment env,
        IServiceProvider serviceProvider,
        NewHeapPlatformAspNetCommonApplicationBuilderOptions options)
    {
        return new NewHeapPlatformAspNetCommonApplicationBuilder(app, env, serviceProvider, options);
    }

    public static IServiceCollection AddScopedNhDbRepository(
        this IServiceCollection serviceCollection,
        Type entityType
    )
    {
        var iRepType = typeof(IRepository<>).MakeGenericType(entityType);
        var repType = typeof(Repository<>).MakeGenericType(entityType);

        serviceCollection.AddScoped(iRepType, serviceProvider =>
        {
            var dbContext = (DbContext)serviceProvider.GetRequiredService<INhIdentityDbContext>();
            return Activator.CreateInstance(repType, dbContext)!;
        });
        
        return serviceCollection;
    }
    
    public static IServiceCollection AddScopedNhDbRepository<TEntity>(
        this IServiceCollection serviceCollection
    )
        where TEntity : class
    {

        serviceCollection.AddScoped<IRepository<TEntity>, Repository<TEntity>>(serviceProvider =>
        {
            var dbContext = (DbContext)serviceProvider.GetRequiredService<INhIdentityDbContext>();
            return new Repository<TEntity>(dbContext, serviceProvider);
        });

        return serviceCollection;
    }
}