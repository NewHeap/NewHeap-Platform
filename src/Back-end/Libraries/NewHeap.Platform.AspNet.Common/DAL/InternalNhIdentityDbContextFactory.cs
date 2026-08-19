using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NewHeap.Platform.AspNet.Common.DAL.Entities;

namespace NewHeap.Platform.AspNet.Common.DAL;

public partial class InternalNhIdentityDbContextFactory<
    TDbContext,
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
{
    private readonly IConfiguration _config;

    public InternalNhIdentityDbContextFactory(IConfiguration config)
    {
        _config = config;
    }

    public TDbContext CreateDbContext(Action<DbContextOptionsBuilder>? dbOptionsAction = null)
    {
        DbContextOptionsBuilder<TDbContext> optionsBuilder = new();
        optionsBuilder.UseConfiguredDatabase(_config);
        dbOptionsAction?.Invoke(optionsBuilder);

        return (TDbContext)Activator.CreateInstance(typeof(TDbContext), optionsBuilder.Options)!;
    }
}
