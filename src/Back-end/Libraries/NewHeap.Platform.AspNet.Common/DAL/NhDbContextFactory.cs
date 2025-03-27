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
    where TUser : User<TDivision, TDivisionUser, TDivisionUserRole, TDivisionRole, TDivisionRoleClaim, TUser>
    where TDivision : Division<TDivisionUser, TDivisionUserRole, TDivisionRole, TDivisionRoleClaim, TDivision, TUser>
    where TDivisionUser : DivisionUser<TDivisionUserRole, TDivisionUser, TDivisionRole, TDivisionRoleClaim, TDivision, TUser>
    where TDivisionRole : DivisionRole<TDivisionUserRole, TDivisionRoleClaim, TDivisionUser, TDivisionRole, TDivision, TUser>
    where TDivisionUserRole : DivisionUserRole<TDivisionUser, TDivisionRole, TDivisionRoleClaim, TDivisionUserRole, TDivision, TUser>
    where TDivisionRoleClaim : DivisionRoleClaim
    where TUserRole : UserRole
    where TLog : Log<TUser, TLogMessageArgument, TLogMessageTranslated, TLogFile, TDivision, TDivisionUser, TDivisionRole, TDivisionUserRole, TDivisionRoleClaim>
    where TLogMessageArgument : LogMessageArgument
    where TLogFile : LogFile
    where TLogMessageTranslated : LogMessageTranslated
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

    protected virtual IConfigurationRoot CreateConfigurationRoot(
        string basePath,
        string appSettingsFileName = "appsettings",
        string secretsFileName = "secrets")
    {
        var configuration = new ConfigurationBuilder()
            .ConfigureNewHeapAspNetCommonConfiguration(
                basePath: basePath,
                appSettingsFileName: appSettingsFileName,
                secretsFileName: secretsFileName
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