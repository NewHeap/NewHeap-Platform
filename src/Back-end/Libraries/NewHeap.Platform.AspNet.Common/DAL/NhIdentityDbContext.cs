using Microsoft.AspNetCore.Identity;
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
    public DbSet<NhBackgroundOperation> BackgroundOperations { get; set; }
    public DbSet<NhBackgroundOperationAttempt> BackgroundOperationAttempts { get; set; }
    public DbSet<NhBackgroundOperationStep> BackgroundOperationSteps { get; set; }
    public DbSet<NhBackgroundOperationEvent> BackgroundOperationEvents { get; set; }
    public DbSet<NhBackgroundOperationCheckpoint> BackgroundOperationCheckpoints { get; set; }
    public DbSet<NhBackgroundOperationIdempotencyRecord> BackgroundOperationIdempotencyRecords { get; set; }
    public DbSet<NhBackgroundOperationLease> BackgroundOperationLeases { get; set; }

    /// <summary>
    /// Whether the model includes the ASP.NET Core Identity passkey table
    /// (<c>AspNetUserPasskeys</c>), which two-factor passkeys need. Override it to return
    /// <see langword="true"/> and add a migration: it adds only the passkey table and leaves
    /// the existing Identity columns unchanged. A model that already maps passkeys, for
    /// example through Identity schema version 3, is left as it is.
    /// </summary>
    protected virtual bool IncludeIdentityPasskeys => false;

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        if (IncludeIdentityPasskeys && builder.Model.FindEntityType(typeof(IdentityUserPasskey<Guid>)) == null)
        {
            // Same mapping as Identity schema version 3, without its other column changes.
            builder.Entity<TUser>()
                .HasMany<IdentityUserPasskey<Guid>>()
                .WithOne()
                .HasForeignKey(passkey => passkey.UserId)
                .IsRequired();

            builder.Entity<IdentityUserPasskey<Guid>>(entity =>
            {
                entity.HasKey(passkey => passkey.CredentialId);
                entity.ToTable("AspNetUserPasskeys");
                entity.Property(passkey => passkey.CredentialId).HasMaxLength(1024);
                entity.OwnsOne(passkey => passkey.Data).ToJson();
            });
        }

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

        #region Auth
        builder.Entity<NhUserAuthRefreshToken>(entity =>
        {
            entity.HasKey(r => r.Id);
            entity.HasIndex(r => r.Token).IsUnique();

            entity.Property(nameof(NhUserNotification.UserId)).IsRequired();
            
            entity
                .HasOne<TUser>()
                .WithMany(x => x.AuthRefreshTokens)
                .HasForeignKey(nameof(NhUserNotification.UserId))
                .HasPrincipalKey("Id")
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired(true)
            ;
        });
        #endregion

        #region Log

        builder.Entity<TLog>()
            .HasIndex(x => x.Tag)
            ;

        builder.Entity<TLog>()
            .HasIndex(x => new { x.ObjectTypeFull, x.ObjectId })
            //.IncludeProperties(x => x.CreationDateTime)
        ;

        builder.Entity<TLog>()
            .HasIndex(x => new { x.ObjectType, x.ObjectId })
            //.IncludeProperties(x => x.CreationDateTime)
            ;

        builder.Entity<TLog>()
            .HasIndex(x => new { x.AdditionalDataProcessed, x.Version, x.CreationDateTime })
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

        builder.Entity<TLog>()
            .Property(e => e.AdditionalData)
            .HasConversion(
                v => v == null ? null : JsonConvert.SerializeObject(v, ConvertJsonSerializerSettings),
                v => string.IsNullOrWhiteSpace(v) ? null : JsonConvert.DeserializeObject<NhLogAdditionalData<
                TLogMessageArgument,
                TLogMessageTranslated,
                TLogFile
                >>(v, ConvertJsonSerializerSettings)!);

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

        // The module mapping is shared with applications that host notifications in their own
        // context; identity hosting keeps the context's schema and adds its foreign keys.
        builder.AddNewHeapNotifications(notifications => notifications.Schema = null);

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

        builder.Entity<NhUserNotification>(entity =>
        {
            entity
                .HasOne<TUser>()
                .WithMany(x => x.Notifications)
                .HasForeignKey(nameof(NhUserNotification.UserId))
                .HasPrincipalKey("Id")
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired(true)
            ;
        });

        #endregion

        #region Background Operations

        builder.AddNewHeapBackgroundOperations(operations => operations.Schema = null);

        builder.Entity<NhBackgroundOperation>(entity =>
        {
            // A user-owned operation is removed with its owner. Operations of other owner
            // kinds have no user and follow their own retention.
            entity.HasOne<TUser>()
                .WithMany()
                .HasForeignKey(x => x.OwnerUserId)
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired(false);

            entity.HasOne<TDivision>()
                .WithMany()
                .HasForeignKey(x => x.DivisionId)
                .OnDelete(DeleteBehavior.Restrict)
                .IsRequired(false);

            entity.HasOne<NhUserNotification>()
                .WithMany()
                .HasForeignKey(x => x.UserNotificationId)
                .OnDelete(DeleteBehavior.Restrict)
                .IsRequired(false);
        });

        #endregion
    }
}
