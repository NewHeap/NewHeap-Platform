using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using Newtonsoft.Json;

namespace NewHeap.Platform.AspNet.Common.DAL;

/// <summary>How a platform module's tables are placed in an EF model.</summary>
public sealed class NhPlatformModuleModelOptions
{
    /// <summary>
    /// The schema of the module's tables. <see langword="null"/> keeps the context's default schema,
    /// which is what <see cref="NhIdentityDbContext"/> uses.
    /// </summary>
    public string? Schema { get; set; }
}

/// <summary>
/// Maps platform modules into any EF context, so an application without
/// <see cref="NhIdentityDbContext"/> can host them next to its own tables. The mapping has no
/// foreign keys to the application's users, divisions or other modules; an application that
/// wants them adds them after this call. Table names are the same as in identity hosting.
/// </summary>
public static class NhPlatformModuleModelBuilderExtensions
{
    /// <summary>The default schema of background-operation tables in a context of the application.</summary>
    public const string BackgroundOperationsDefaultSchema = "nhops";

    /// <summary>The default schema of notification tables in a context of the application.</summary>
    public const string NotificationsDefaultSchema = "nhnotify";

    private static readonly JsonSerializerSettings ConvertJsonSerializerSettings =
        new() { NullValueHandling = NullValueHandling.Ignore };

    /// <summary>
    /// Maps the background-operation tables, by default in schema <c>nhops</c>.
    /// </summary>
    public static ModelBuilder AddNewHeapBackgroundOperations(
        this ModelBuilder builder,
        Action<NhPlatformModuleModelOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new NhPlatformModuleModelOptions { Schema = BackgroundOperationsDefaultSchema };
        configure?.Invoke(options);
        var schema = options.Schema;

        builder.Entity<NhBackgroundOperation>(entity =>
        {
            MapTable(entity, "BackgroundOperations", schema);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.Property(x => x.ProgressCurrent).HasPrecision(18, 4);
            entity.Property(x => x.ProgressTotal).HasPrecision(18, 4);
            entity.Property(x => x.ProgressPercentage).HasPrecision(18, 4);
            entity.HasIndex(x => new { x.ProcessorKey, x.Status, x.NextDispatchAt, x.Priority, x.CreationDateTime });
            entity.HasIndex(x => new { x.OwnerUserId, x.Status, x.LastModifiedDateTime });
            entity.HasIndex(x => new { x.DivisionId, x.Status, x.LastModifiedDateTime });
            // Serves the administration list: root operations of every owner per division
            // scope, filtered by status and ordered by recency, without visiting the
            // far more numerous fan-out children.
            entity.HasIndex(x => new { x.DivisionId, x.ParentOperationId, x.Status, x.LastModifiedDateTime });
            entity.HasIndex(x => new { x.Status, x.HeartbeatAt });
            entity.HasIndex(x => new { x.Status, x.CompletedAt });
            entity.HasIndex(x => x.SchedulerJobId);
            entity.HasIndex(x => x.ParentOperationId);
            entity.HasIndex(x => x.RootOperationId);
            entity.HasIndex(x => new { x.ParentOperationId, x.FanOutKey, x.FanOutItemKey }).IsUnique();
            // Agent-owned operations are listed and coalesced by owner key and domain object.
            entity.HasIndex(x => new { x.OwnerKind, x.OwnerKey, x.Status, x.LastModifiedDateTime });
            entity.HasIndex(x => new { x.DomainObjectType, x.DomainObjectId, x.Status });
            // Admission looks up active operations per concurrency key.
            entity.HasIndex(x => new { x.ConcurrencyKey, x.Status });

            entity.HasOne(x => x.ParentOperation)
                .WithMany(x => x.ChildOperations)
                .HasForeignKey(x => x.ParentOperationId)
                .OnDelete(DeleteBehavior.Restrict)
                .IsRequired(false);
        });

        builder.Entity<NhBackgroundOperationAttempt>(entity =>
        {
            MapTable(entity, "BackgroundOperationAttempts", schema);
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
            MapTable(entity, "BackgroundOperationSteps", schema);
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
            MapTable(entity, "BackgroundOperationEvents", schema);
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
            MapTable(entity, "BackgroundOperationCheckpoints", schema);
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
            MapTable(entity, "BackgroundOperationIdempotencyRecords", schema);
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
            MapTable(entity, "BackgroundOperationLeases", schema);
            entity.HasKey(x => new { x.ResourceKey, x.Slot });
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => x.ExpiresAt);
            entity.HasOne(x => x.Operation)
                .WithMany()
                .HasForeignKey(x => x.OperationId)
                .OnDelete(DeleteBehavior.SetNull)
                .IsRequired(false);
        });

        return builder;
    }

    /// <summary>
    /// Maps the tables of outgoing notifications, their deliveries and in-app user notifications,
    /// by default in schema <c>nhnotify</c>.
    /// </summary>
    public static ModelBuilder AddNewHeapNotifications(
        this ModelBuilder builder,
        Action<NhPlatformModuleModelOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new NhPlatformModuleModelOptions { Schema = NotificationsDefaultSchema };
        configure?.Invoke(options);
        var schema = options.Schema;

        builder.Entity<NhNotification>(entity =>
        {
            MapTable(entity, "Notifications", schema);
            entity.HasIndex(x => new { x.ProcessorKey, x.Priority });
        });

        builder.Entity<NhNotificationDelivery>(entity =>
        {
            MapTable(entity, "NotificationDeliveries", schema);
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

        builder.Entity<NhUserNotification>(entity =>
        {
            MapTable(entity, "UserNotifications", schema);
            entity.Property(nameof(NhUserNotification.UserId)).IsRequired();
            entity.HasIndex(x => new { x.UserId, x.GroupKey });

            entity
                .Property(e => e.Data)
                .HasConversion(
                    v => v == null ? "{}" : JsonConvert.SerializeObject(v, ConvertJsonSerializerSettings),
                    v => string.IsNullOrWhiteSpace(v) ? new NhUserNotficationData() : JsonConvert.DeserializeObject<NhUserNotficationData>(v, ConvertJsonSerializerSettings)!);
        });

        builder.Entity<NhUserNotificationMessage>(entity =>
        {
            MapTable(entity, "UserNotificationMessages", schema);
            entity
                .HasOne(x => x.UserNotification)
                .WithMany(x => x.Messages)
                .HasForeignKey(x => x.UserNotificationId)
                .IsRequired(true)
                .OnDelete(DeleteBehavior.Cascade)
            ;
        });

        return builder;
    }

    private static void MapTable<TEntity>(EntityTypeBuilder<TEntity> entity, string table, string? schema)
        where TEntity : class
    {
        if (schema is null)
        {
            entity.ToTable(table);
        }
        else
        {
            entity.ToTable(table, schema);
        }
    }
}
