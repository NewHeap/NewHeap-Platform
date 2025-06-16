using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using Newtonsoft.Json;
using System.Linq;

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

public interface INhIdentityDbContext
{
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
    > : IdentityDbContext<TUser, TUserRole, Guid>, INhIdentityDbContext
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
    public DbSet<NhNotification> Notifications { get; set; }
    public DbSet<NhNotificationDelivery> NotificationDeliveries { get; set; }
    public DbSet<NhUserNotification> UserNotifications { get; set; }
    public DbSet<NhUserNotificationMessage> UserNotificationMessages { get; set; }

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
        });

        builder.Entity<TDivisionUser>(entity =>
        {
            entity.HasIndex(x => new { x.DivisionId, x.UserId })
                .IsUnique();

            entity
                .HasOne(x => x.Division)
                .WithMany(x => x.DivisionUsers)
                .HasForeignKey(x => x.DivisionId)
                .OnDelete(DeleteBehavior.Cascade)
                ;

            entity
                .HasOne(x => x.User)
                .WithMany(x => x.DivisionUsers)
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade)
                ;
        });

        builder.Entity<TDivisionUserRole>(entity =>
        {
            entity.HasKey(x => new { x.DivisionUserId, x.DivisionRoleId });

            entity
                .HasOne(x => x.DivisionUser)
                .WithMany(x => x.DivisionUserRoles)
                .HasForeignKey(x => x.DivisionUserId)
                .OnDelete(DeleteBehavior.Cascade)
                ;

            entity
                .HasOne(x => x.DivisionRole)
                .WithMany(x => x.DivisionUserRoles)
                .HasForeignKey(x => x.DivisionRoleId)
                .OnDelete(DeleteBehavior.Cascade)
                ;
        });

        builder.Entity<TDivisionRole>(entity =>
        {
            entity
                .HasMany(x => x.DivisionRoleClaims)
                .WithOne()
                .HasForeignKey(x => x.DivisionRoleId)
                .OnDelete(DeleteBehavior.Cascade)
            ;
        });

        #endregion

        #region Notification

        builder.Entity<NhNotification>(entity =>
        {
            entity
                .HasOne(typeof(TUser))
                .WithMany()
                .HasForeignKey(nameof(NhNotification.CreatedByUserId))
                .HasPrincipalKey("Id")
                .OnDelete(DeleteBehavior.SetNull)
                .IsRequired(false)
            ;
        });

        builder.Entity<NhNotificationDelivery>(entity =>
        {
            entity
                .HasOne(x => x.Notification)
                .WithMany(x => x.Deliveries)
                .HasForeignKey(x => x.NotificationId)
                .IsRequired(true)
                .OnDelete(DeleteBehavior.Cascade)
            ;

            entity
                .Property(e => e.Data)
                .HasConversion(
                    v => v == null ? null : JsonConvert.SerializeObject(v, ConvertJsonSerializerSettings),
                    v => string.IsNullOrWhiteSpace(v) ? null : JsonConvert.DeserializeObject(v, ConvertJsonSerializerSettings));
        });

        #endregion

        #region User Notification

        builder.Entity<NhUserNotification>(entity =>
        {
            entity
                .HasOne(typeof(TUser))
                .WithMany()
                .HasForeignKey(nameof(NhUserNotification.UserId))
                .HasPrincipalKey("Id")
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired(true) // TODO:
            ;
        });

        builder.Entity<NhUserNotificationMessage>(entity =>
        {
            entity
                .HasOne(x => x.UserNotification)
                .WithMany(x => x.Messages)
                .HasForeignKey(x => x.UserNotificationId)
                .IsRequired(true)
                .OnDelete(DeleteBehavior.Cascade)
            ;

            //entity
            //    .Property(e => e.Data)
            //    .HasConversion(
            //        v => v == null ? null : JsonConvert.SerializeObject(v, ConvertJsonSerializerSettings),
            //        v => string.IsNullOrWhiteSpace(v) ? null : JsonConvert.DeserializeObject(v, ConvertJsonSerializerSettings));
        });

        #endregion
    }
}