using Hangfire;
using Hangfire.Console;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AspNet.Common.Builders;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models.Mutate;
using NewHeap.Platform.AspNet.Common.Models.Options;
using NewHeap.Platform.AspNet.Common.Models.View;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NewHeap.Platform.AspNet.Common.Services.Notification;
using NewHeap.Platform.Common;

namespace NewHeap.Platform.AspNet.Common;

public interface INewHeapPlatformAspNetCommonConfigurator
<TUser, TUserRole, TDivision, TDivisionUser, TDivisionRole,
    TDivisionUserRole, TDivisionRoleClaim, TLog, TLogMessageArgument, TLogFile, TLogMessageTranslated, TDbLogService,
    TDbContext, TUserManager, TDivisionService, TDivisionMutateModel, TDivisionUserService, TDivisionUserMutateModel>
    where TUser : NhUser<TDivision, TDivisionUser, TDivisionUserRole, TDivisionRole, TDivisionRoleClaim, TUser>, new()
    where TUserRole : NhUserRole, new()
    where TDivision : NhDivision<TDivisionUser, TDivisionUserRole, TDivisionRole, TDivisionRoleClaim, TDivision, TUser>,
    new()
    where TDivisionUser : NhDivisionUser<TDivisionUserRole, TDivisionUser, TDivisionRole, TDivisionRoleClaim, TDivision,
        TUser>, new()
    where TDivisionRole : NhDivisionRole<TDivisionUserRole, TDivisionRoleClaim, TDivisionUser, TDivisionRole, TDivision,
        TUser>, new()
    where TDivisionUserRole : NhDivisionUserRole<TDivisionUser, TDivisionRole, TDivisionRoleClaim, TDivisionUserRole,
        TDivision, TUser>, new()
    where TDivisionRoleClaim : NhDivisionRoleClaim, new()
    where TLog : NhLog<TUser, TLogMessageArgument, TLogMessageTranslated, TLogFile, TDivision, TDivisionUser,
        TDivisionRole, TDivisionUserRole, TDivisionRoleClaim>, new()
    where TLogMessageArgument : NhLogMessageArgument, new()
    where TLogFile : NhLogFile, new()
    where TLogMessageTranslated : NhLogMessageTranslated, new()
    where TDbLogService : NhDbLogService<TLog, TUser, TLogMessageArgument, TLogMessageTranslated, TLogFile, TDivision,
        TDivisionUser, TDivisionRole, TDivisionUserRole, TDivisionRoleClaim>
    where TDbContext : NhIdentityDbContext<TDivision, TDivisionUser, TDivisionRole, TDivisionUserRole,
        TDivisionRoleClaim, TUser, TUserRole, TLog, TLogMessageArgument, TLogFile, TLogMessageTranslated>
    where TUserManager : class, INhUserManager<TUser>
    where TDivisionService : NhDivisionService<TUser, TDivision, TDivisionUser, TDivisionRole, TDivisionUserRole,
        TDivisionRoleClaim, TDivisionMutateModel>
    where TDivisionMutateModel : NhDivisionMutateModel
    where TDivisionUserService : NhDivisionUserService<TUser, TDivision, TDivisionUser, TDivisionRole, TDivisionUserRole
        , TDivisionRoleClaim, TDivisionUserMutateModel>
    where TDivisionUserMutateModel : NhDivisionUserMutateModel
{
    INewHeapPlatformAspNetCommonConfigurator<
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
        >
        AddAuthentication<TUserViewModel, TDivisionViewModel, TClaimViewModel>(
            Action<NhAuthenticationBuilder<TUser, TDivision, TDivisionUser, TDivisionRole, TDivisionUserRole,
                TDivisionRoleClaim, TUserViewModel, TDivisionViewModel, TClaimViewModel>>? configure = null)
        where TUserViewModel : NhUserViewModel<TDivisionViewModel>
        where TDivisionViewModel : NhDivisionViewModel
        where TClaimViewModel : NhClaimViewModel;

    INewHeapPlatformAspNetCommonConfigurator<
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
        >
        ConfigureCommon(
            Action<NewHeapPlatformCommonConfigurator> action);

    INewHeapPlatformAspNetCommonConfigurator<
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
        >
        WithIdentityEntityFramework(Action<DbContextOptionsBuilder> dbOptionsAction);

    INewHeapPlatformAspNetCommonConfigurator<
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
        >
        WithIdentity(
            Action<IdentityOptions>? identityOptionsAction = null);

    INewHeapPlatformAspNetCommonConfigurator<
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
        >
        WithDbLogService(
            Action<DbLogServiceSettings> settingsAction);

    INewHeapPlatformAspNetCommonConfigurator<
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
        >
        WithSignalR(Action<HubOptions>? hubOptionsAction = null);

    INewHeapPlatformAspNetCommonConfigurator<
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
        >
        WithBackgroundOperations(Action<NhBackgroundOperationBuilder> configure);

    INewHeapPlatformAspNetCommonConfigurator<
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
        >
        WithHangfire(
            string nameOrConnectionString,
            Action<IGlobalConfiguration>? hangfireOptionsAction = null,
            Action<ConsoleOptions>? consoleOptionsAction = null,
            Action<BackgroundJobServerOptions>? backgroundJobServerOptions = null,
            DatabaseProvider databaseProvider = DatabaseProvider.SqlServer
        );

    INewHeapNotificationConfigurator<
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
    > WithNotifications(Action<NhNotificationSettings> settingsAction);

    INewHeapPlatformAspNetCommonConfigurator<
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
        >
        WithEvents(Action<NhEventConfigurationBuilder> configure);
}

public interface INewHeapNotificationConfigurator<
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
> : INewHeapPlatformAspNetCommonConfigurator<
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
>
    where TUser : NhUser<TDivision, TDivisionUser, TDivisionUserRole, TDivisionRole, TDivisionRoleClaim, TUser>, new()
    where TUserRole : NhUserRole, new()
    where TDivision : NhDivision<TDivisionUser, TDivisionUserRole, TDivisionRole, TDivisionRoleClaim, TDivision, TUser>,
    new()
    where TDivisionUser : NhDivisionUser<TDivisionUserRole, TDivisionUser, TDivisionRole, TDivisionRoleClaim, TDivision,
        TUser>, new()
    where TDivisionRole : NhDivisionRole<TDivisionUserRole, TDivisionRoleClaim, TDivisionUser, TDivisionRole, TDivision,
        TUser>, new()
    where TDivisionUserRole : NhDivisionUserRole<TDivisionUser, TDivisionRole, TDivisionRoleClaim, TDivisionUserRole,
        TDivision, TUser>, new()
    where TDivisionRoleClaim : NhDivisionRoleClaim, new()
    where TLog : NhLog<TUser, TLogMessageArgument, TLogMessageTranslated, TLogFile, TDivision, TDivisionUser,
        TDivisionRole, TDivisionUserRole, TDivisionRoleClaim>, new()
    where TLogMessageArgument : NhLogMessageArgument, new()
    where TLogFile : NhLogFile, new()
    where TLogMessageTranslated : NhLogMessageTranslated, new()
    where TDbLogService : NhDbLogService<TLog, TUser, TLogMessageArgument, TLogMessageTranslated, TLogFile, TDivision,
        TDivisionUser, TDivisionRole, TDivisionUserRole, TDivisionRoleClaim>
    where TDbContext : NhIdentityDbContext<TDivision, TDivisionUser, TDivisionRole, TDivisionUserRole,
        TDivisionRoleClaim, TUser, TUserRole, TLog, TLogMessageArgument, TLogFile, TLogMessageTranslated>
    where TUserManager : class, INhUserManager<TUser>
    where TDivisionService : NhDivisionService<TUser, TDivision, TDivisionUser, TDivisionRole, TDivisionUserRole,
        TDivisionRoleClaim, TDivisionMutateModel>
    where TDivisionMutateModel : NhDivisionMutateModel
    where TDivisionUserService : NhDivisionUserService<TUser, TDivision, TDivisionUser, TDivisionRole, TDivisionUserRole
        , TDivisionRoleClaim, TDivisionUserMutateModel>
    where TDivisionUserMutateModel : NhDivisionUserMutateModel
{
    INewHeapNotificationConfigurator<
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
    > ConfigureEmailNotificationSettings(Action<NhEmailNotificationSettings> configure);
}
