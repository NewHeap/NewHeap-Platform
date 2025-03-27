using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using Newtonsoft.Json;

namespace NewHeap.Platform.AspNet.Common.DAL;

public abstract partial class NhIdentityDbContext : NhIdentityDbContext<
    NhDivision,
    NhDivisionUser,
    NhDivisionRole,
    NhDivisionUserRole,
    NhDivisionRoleClaim,
    NhUser,
    NhUserRole,
    NhLog,
    NhLogMessageArgument,
    NhLogFile,
    NhLogMessageTranslated>
{
    public NhIdentityDbContext()
    {
    }

    public NhIdentityDbContext(DbContextOptions contextOptions)
        : base(contextOptions)
    {
    }
}

public abstract partial class NhIdentityDbContext<
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
    > : IdentityDbContext<TUser, TUserRole, Guid>
    where TUser : User<TDivision, TDivisionUser, TDivisionUserRole, TDivisionRole, TDivisionRoleClaim, TUser>
    where TDivision : Division<TDivisionUser, TDivisionUserRole, TDivisionRole, TDivisionRoleClaim, TDivision, TUser>
    where TDivisionUser : DivisionUser<TDivisionUserRole, TDivisionUser, TDivisionRole, TDivisionRoleClaim, TDivision, TUser>
    where TDivisionRole : DivisionRole<TDivisionUserRole, TDivisionRoleClaim, TDivisionUser, TDivisionRole, TDivision, TUser>
    where TDivisionUserRole : DivisionUserRole<TDivisionUser, TDivisionRole, TDivisionRoleClaim, TDivisionUserRole, TDivision, TUser>
    where TDivisionRoleClaim : NhDivisionRoleClaim
    where TUserRole : NhUserRole
    where TLog : Log<TUser, TLogMessageArgument, TLogMessageTranslated, TLogFile, TDivision, TDivisionUser, TDivisionRole, TDivisionUserRole, TDivisionRoleClaim>
    where TLogMessageArgument : NhLogMessageArgument
    where TLogFile : NhLogFile
    where TLogMessageTranslated : NhLogMessageTranslated
{
    public static readonly JsonSerializerSettings ConvertJsonSerializerSettings =
        new() { NullValueHandling = NullValueHandling.Ignore };

    public NhIdentityDbContext()
    {
    }

    public NhIdentityDbContext(DbContextOptions contextOptions)
        : base(contextOptions)
    {
    }

    public DbSet<TDivision> Divisions { get; set; }
    public DbSet<TLog> Logs { get; set; }
    public DbSet<TLogMessageArgument> LogMessageArguments { get; set; }
    public DbSet<TLogFile> LogFiles { get; set; }
    public DbSet<TLogMessageTranslated> LogMessageTranslateds { get; set; }
    public DbSet<TDivisionRole> DivisionRoles { get; set; }
    public DbSet<TDivisionUser> DivisionUsers { get; set; }
    public DbSet<TDivisionUserRole> DivisionUserRoles { get; set; }
    public DbSet<TDivisionRoleClaim> DivisionRoleClaims { get; set; }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        #region User

        builder.Entity<TUser>()
            .HasOne(x => x.ActiveDivision)
            .WithMany()
            .HasForeignKey(x => x.ActiveDivisionId)
            .OnDelete(DeleteBehavior.SetNull)
            ;

        builder.Entity<TUser>()
            .HasMany(x => x.DivisionUsers)
            .WithOne()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade)
        ;

        #endregion

        #region Log

        builder.Entity<TLog>()
            .HasIndex(x => x.Tag)
            ;

        builder.Entity<TLog>()
            .HasIndex(x => new { x.ObjectTypeFull, x.ObjectId })
            ;

        builder.Entity<TLog>()
            .HasIndex(x => new { x.ObjectType, x.ObjectId })
            ;

        builder.Entity<TLog>()
            .HasOne(x => x.User)
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.SetNull)
            ;

        builder.Entity<TLog>()
            .HasOne(x => x.Division)
            .WithMany()
            .HasForeignKey(x => x.DivisionId)
            .OnDelete(DeleteBehavior.SetNull)
            ;

        builder.Entity<TLog>()
            .HasMany(x => x.Files)
            .WithOne()
            .HasForeignKey(x => x.LogId)
            .OnDelete(DeleteBehavior.Cascade)
        ;

        builder.Entity<TLog>()
            .HasMany(x => x.MessageArguments)
            .WithOne()
            .HasForeignKey(x => x.LogId)
            .OnDelete(DeleteBehavior.Cascade)
        ;

        builder.Entity<TLog>()
           .HasMany(x => x.MessageArguments)
           .WithOne()
           .HasForeignKey(x => x.LogId)
           .OnDelete(DeleteBehavior.Cascade)
        ;

        builder.Entity<TLog>()
           .HasMany(x => x.MessageTranslateds)
           .WithOne()
           .HasForeignKey(x => x.LogId)
           .OnDelete(DeleteBehavior.Cascade)
        ;
        #endregion

        #region Division

        builder.Entity<TDivision>(entity =>
        {
            entity
                .HasMany(x => x.DivisionUsers)
                .WithOne()
                .HasForeignKey(x => x.DivisionId)
                .OnDelete(DeleteBehavior.Cascade)
            ;
        });

        builder.Entity<TDivisionUser>(entity =>
        {
            entity.HasIndex(x => new { x.DivisionId, x.UserId })
                .IsUnique();

            entity
                .HasMany(x => x.DivisionUserRoles)
                .WithOne()
                .HasForeignKey(x => x.DivisionUserId)
                .OnDelete(DeleteBehavior.Cascade)
            ;
        });

        builder.Entity<TDivisionRole>(entity =>
        {
            entity
                .HasMany(x => x.DivisionUserRoles)
                .WithOne()
                .HasForeignKey(x => x.DivisionRoleId)
                .OnDelete(DeleteBehavior.Cascade)
            ;

            entity
                .HasMany(x => x.DivisionRoleClaims)
                .WithOne()
                .HasForeignKey(x => x.DivisionRoleId)
                .OnDelete(DeleteBehavior.Cascade)
            ;
        });

        builder.Entity<TDivisionRoleClaim>(entity =>
        {
            entity.HasIndex(x => x.DivisionRoleId);
        });

        #endregion
    }
}