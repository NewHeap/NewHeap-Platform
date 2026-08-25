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

        builder.Entity<NhNotification>(entity =>
        {
            entity.HasIndex(x => new { x.ProcessorKey, x.Priority });

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
            entity.Property(nameof(NhUserNotification.UserId)).IsRequired();
            entity
                .HasOne<TUser>()
                .WithMany(x => x.Notifications)
                .HasForeignKey(nameof(NhUserNotification.UserId))
                .HasPrincipalKey("Id")
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired(true)
            ;

            entity
            .Property(e => e.Data)
            .HasConversion(
                v => v == null ? "{}" : JsonConvert.SerializeObject(v, ConvertJsonSerializerSettings),
                v => string.IsNullOrWhiteSpace(v) ? new NhUserNotficationData() : JsonConvert.DeserializeObject<NhUserNotficationData>(v, ConvertJsonSerializerSettings)!);

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
        });

        #endregion

        #region Background Operations

        builder.Entity<NhBackgroundOperation>(entity =>
        {
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.Property(x => x.ProgressCurrent).HasPrecision(18, 4);
            entity.Property(x => x.ProgressTotal).HasPrecision(18, 4);
            entity.Property(x => x.ProgressPercentage).HasPrecision(18, 4);
            entity.HasIndex(x => new { x.ProcessorKey, x.Status, x.NextDispatchAt, x.Priority, x.CreationDateTime });
            entity.HasIndex(x => new { x.OwnerUserId, x.Status, x.LastModifiedDateTime });
            entity.HasIndex(x => new { x.DivisionId, x.Status, x.LastModifiedDateTime });
            entity.HasIndex(x => new { x.Status, x.HeartbeatAt });
            entity.HasIndex(x => new { x.Status, x.CompletedAt });
            entity.HasIndex(x => x.SchedulerJobId);
            entity.HasIndex(x => x.ParentOperationId);
            entity.HasIndex(x => x.RootOperationId);
            entity.HasIndex(x => new { x.ParentOperationId, x.FanOutKey, x.FanOutItemKey }).IsUnique();

            entity.HasOne<TUser>()
                .WithMany()
                .HasForeignKey(x => x.OwnerUserId)
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();

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

            entity.HasOne(x => x.ParentOperation)
                .WithMany(x => x.ChildOperations)
                .HasForeignKey(x => x.ParentOperationId)
                .OnDelete(DeleteBehavior.Restrict)
                .IsRequired(false);

        });

        builder.Entity<NhBackgroundOperationAttempt>(entity =>
        {
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => new { x.OperationId, x.AttemptNumber }).IsUnique();
            entity.HasIndex(x => new { x.OperationId, x.StartedAt });
            entity.HasOne(x => x.Operation)
                .WithMany(x => x.Attempts)
                .HasForeignKey(x => x.OperationId)
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();
        });

        builder.Entity<NhBackgroundOperationStep>(entity =>
        {
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.Property(x => x.Weight).HasPrecision(18, 4);
            entity.Property(x => x.Current).HasPrecision(18, 4);
            entity.Property(x => x.Total).HasPrecision(18, 4);
            entity.Property(x => x.Percentage).HasPrecision(18, 4);
            entity.HasIndex(x => new { x.OperationId, x.ParentStepId, x.StepKey }).IsUnique();
            entity.HasIndex(x => new { x.OperationId, x.DisplayOrder, x.Status });
            entity.HasOne(x => x.Operation)
                .WithMany(x => x.Steps)
                .HasForeignKey(x => x.OperationId)
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();
            entity.HasOne(x => x.ParentStep)
                .WithMany(x => x.Children)
                .HasForeignKey(x => x.ParentStepId)
                .OnDelete(DeleteBehavior.Restrict)
                .IsRequired(false);
        });

        builder.Entity<NhBackgroundOperationEvent>(entity =>
        {
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.HasIndex(x => new { x.OperationId, x.Sequence }).IsUnique();
            entity.HasIndex(x => new { x.OperationId, x.CreationDateTime });
            entity.HasOne(x => x.Operation)
                .WithMany(x => x.Events)
                .HasForeignKey(x => x.OperationId)
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();
        });

        builder.Entity<NhBackgroundOperationCheckpoint>(entity =>
        {
            entity.HasKey(x => new { x.OperationId, x.CheckpointKey });
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasOne(x => x.Operation)
                .WithMany(x => x.Checkpoints)
                .HasForeignKey(x => x.OperationId)
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();
        });

        builder.Entity<NhBackgroundOperationIdempotencyRecord>(entity =>
        {
            entity.HasKey(x => new { x.Scope, x.KeyHash });
            entity.HasIndex(x => x.ExpiresAt);
            entity.HasOne(x => x.Operation)
                .WithMany()
                .HasForeignKey(x => x.OperationId)
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();
        });

        builder.Entity<NhBackgroundOperationLease>(entity =>
        {
            entity.HasKey(x => new { x.ResourceKey, x.Slot });
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => x.ExpiresAt);
            entity.HasOne(x => x.Operation)
                .WithMany()
                .HasForeignKey(x => x.OperationId)
                .OnDelete(DeleteBehavior.SetNull)
                .IsRequired(false);
        });

        #endregion
    }
}
