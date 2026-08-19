using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using NewHeap.Platform.AspNet.Common.DAL.Entities;

namespace NewHeap.Platform.AspNet.Common.DAL;

public abstract partial class NhDbContextFactory<
    TDBContext,
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
    > : IDesignTimeDbContextFactory<TDBContext>
    where TUser : NhUser<TDivision, TDivisionUser, TDivisionUserRole, TDivisionRole, TDivisionRoleClaim, TUser>
    where TDivision : NhDivision<TDivisionUser, TDivisionUserRole, TDivisionRole, TDivisionRoleClaim, TDivision, TUser>
    where TDivisionUser : NhDivisionUser<TDivisionUserRole, TDivisionUser, TDivisionRole, TDivisionRoleClaim, TDivision, TUser>
    where TDivisionRole : NhDivisionRole<TDivisionUserRole, TDivisionRoleClaim, TDivisionUser, TDivisionRole, TDivision, TUser>
    where TDivisionUserRole : NhDivisionUserRole<TDivisionUser, TDivisionRole, TDivisionRoleClaim, TDivisionUserRole, TDivision, TUser>
    where TDivisionRoleClaim : NhDivisionRoleClaim
    where TUserRole : NhUserRole
    where TLog : NhLog<TUser, TLogMessageArgument, TLogMessageTranslated, TLogFile, TDivision, TDivisionUser, TDivisionRole, TDivisionUserRole, TDivisionRoleClaim>
    where TLogMessageArgument : NhLogMessageArgument
    where TLogFile : NhLogFile
    where TLogMessageTranslated : NhLogMessageTranslated
    where TDBContext : NhIdentityDbContext<
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
{
    public abstract TDBContext CreateDbContext(string[] args);

    protected virtual IConfigurationRoot CreateConfigurationRoot()
    {
        return CreateConfigurationRoot(
            basePath: Directory.GetCurrentDirectory()
        );
    }

    protected virtual IConfigurationRoot CreateConfigurationRoot(string[] args)
    {
        return CreateConfigurationRoot(
            basePath: Directory.GetCurrentDirectory(),
            args: args
        );
    }

    protected virtual IConfigurationRoot CreateConfigurationRoot(
        string basePath,
        string appSettingsFileName = "appsettings",
        string secretsFileName = "secrets",
        bool environmentFileIsOptional = true
        )
    {
        var configuration = new ConfigurationBuilder()
            .ConfigureNewHeapAspNetCommonConfiguration(
                basePath: basePath,
                appSettingsFileName: appSettingsFileName,
                secretsFileName: secretsFileName,
                environmentFileIsOptional: environmentFileIsOptional
            )
            .Build();

        return configuration;
    }

    protected virtual IConfigurationRoot CreateConfigurationRoot(
        string basePath,
        string[] args,
        string appSettingsFileName = "appsettings",
        string secretsFileName = "secrets",
        bool environmentFileIsOptional = true
        )
    {
        var configuration = new ConfigurationBuilder()
            .ConfigureNewHeapAspNetCommonConfiguration(
                basePath: basePath,
                args: args,
                appSettingsFileName: appSettingsFileName,
                secretsFileName: secretsFileName,
                environmentFileIsOptional: environmentFileIsOptional
            )
            .Build();

        return configuration;
    }

    protected virtual DbContextOptionsBuilder<TDBContext> CreateBuilder()
    {
        DbContextOptionsBuilder<TDBContext> builder = new();

        return builder;
    }
}
