using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using NewHeap.Platform.AI.Chat.Entities;

namespace NewHeap.Platform.AI.Chat.Persistence;

/// <summary>
/// Library-owned, provider-neutral assistant store. The SQL Server and PostgreSQL packages
/// own the provider registration and migrations.
/// </summary>
public sealed class NhAssistantDbContext : DbContext
{
    private readonly NhAssistantDbContextOptions _storageOptions;

    public NhAssistantDbContext(
        DbContextOptions<NhAssistantDbContext> options,
        NhAssistantDbContextOptions storageOptions)
        : base(options)
    {
        ArgumentNullException.ThrowIfNull(storageOptions);
        _storageOptions = storageOptions;
    }

    internal string Schema => _storageOptions.Schema;

    public DbSet<AssistantConversation> Conversations => Set<AssistantConversation>();

    public DbSet<AssistantMessage> Messages => Set<AssistantMessage>();

    public DbSet<AssistantToolInvocation> ToolInvocations => Set<AssistantToolInvocation>();

    public DbSet<AssistantApproval> Approvals => Set<AssistantApproval>();

    public DbSet<AssistantBudgetLedger> BudgetLedgers => Set<AssistantBudgetLedger>();

    public DbSet<AssistantIdempotencyLease> IdempotencyLeases => Set<AssistantIdempotencyLease>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(_storageOptions.Schema);

        modelBuilder.Entity<AssistantConversation>(conversation =>
        {
            conversation.ToTable("AssistantConversation");
            conversation.HasKey(item => item.Id);
            conversation.Property(item => item.Id).ValueGeneratedNever();
            conversation.Property(item => item.OwnerActorId).HasMaxLength(256).IsRequired();
            conversation.Property(item => item.TenantId).HasMaxLength(256);
            conversation.Property(item => item.AgentId).HasMaxLength(128).IsRequired();
            conversation.Property(item => item.Title).HasMaxLength(200);
            conversation.Property(item => item.Status).HasMaxLength(32).IsRequired();
            conversation.Property(item => item.ConcurrencyStamp).IsConcurrencyToken();
            conversation.HasIndex(item => new { item.OwnerActorId, item.UpdatedAt });
            conversation.HasMany(item => item.Messages)
                .WithOne(item => item.Conversation)
                .HasForeignKey(item => item.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AssistantMessage>(message =>
        {
            message.ToTable("AssistantMessage");
            message.HasKey(item => item.Id);
            message.Property(item => item.Id).ValueGeneratedNever();
            message.Property(item => item.Role).HasMaxLength(16).IsRequired();
            message.Property(item => item.PartsJson).IsRequired();
            message.Property(item => item.ClientMessageId).HasMaxLength(128);
            message.HasIndex(item => new { item.ConversationId, item.CreatedAt });
        });

        modelBuilder.Entity<AssistantToolInvocation>(invocation =>
        {
            invocation.ToTable("AssistantToolInvocation");
            invocation.HasKey(item => item.Id);
            invocation.Property(item => item.Id).ValueGeneratedNever();
            invocation.Property(item => item.CallId).HasMaxLength(128).IsRequired();
            invocation.Property(item => item.FunctionName).HasMaxLength(256).IsRequired();
            invocation.Property(item => item.ToolId).HasMaxLength(256).IsRequired();
            invocation.Property(item => item.ContractHash).HasMaxLength(128).IsRequired();
            invocation.Property(item => item.DisplayName).HasMaxLength(256).IsRequired();
            invocation.Property(item => item.Status).HasMaxLength(32).IsRequired();
            invocation.Property(item => item.ResultCode).HasMaxLength(128);
            invocation.Property(item => item.DataClassification).HasConversion<string>().HasMaxLength(32);
            invocation.Property(item => item.RetentionCategory).HasConversion<string>().HasMaxLength(64);
            invocation.Property(item => item.IdempotencyKey).HasMaxLength(256);
            invocation.HasIndex(item => new { item.ConversationId, item.StartedAt });
            invocation.HasOne<AssistantConversation>()
                .WithMany()
                .HasForeignKey(item => item.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AssistantApproval>(approval =>
        {
            approval.ToTable("AssistantApproval");
            approval.HasKey(item => item.Id);
            approval.Property(item => item.Id).ValueGeneratedNever();
            approval.Property(item => item.ProposalHash).HasMaxLength(128).IsRequired();
            approval.Property(item => item.ProposalJson).IsRequired();
            approval.Property(item => item.ToolId).HasMaxLength(256).IsRequired();
            approval.Property(item => item.Summary).HasMaxLength(512).IsRequired();
            approval.Property(item => item.ArgumentsPreview)
                .HasMaxLength(NhAssistantLimits.MaxPreviewCharacters)
                .IsRequired();
            approval.Property(item => item.TargetsJson).HasMaxLength(4_000).IsRequired();
            approval.Property(item => item.Status).HasMaxLength(32).IsRequired();
            approval.Property(item => item.DecidedByActorId).HasMaxLength(256);
            approval.Property(item => item.Reason).HasMaxLength(500);
            approval.Property(item => item.ConcurrencyStamp).IsConcurrencyToken();
            approval.HasIndex(item => item.ProposalHash);
            approval.HasIndex(item => item.ConversationId);
            approval.HasOne<AssistantConversation>()
                .WithMany()
                .HasForeignKey(item => item.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AssistantBudgetLedger>(ledger =>
        {
            ledger.ToTable("AssistantBudgetLedger");
            ledger.HasKey(item => new { item.ActorId, item.Day });
            ledger.Property(item => item.ActorId).HasMaxLength(256);
            ledger.Property(item => item.EstimatedCost).HasPrecision(18, 6);
        });

        modelBuilder.Entity<AssistantIdempotencyLease>(lease =>
        {
            lease.ToTable("AssistantIdempotencyLease");
            lease.HasKey(item => item.Id);
            lease.Property(item => item.Id).ValueGeneratedNever();
            lease.Property(item => item.KeyHash).HasMaxLength(64).IsRequired();
            lease.Property(item => item.ActorId).HasMaxLength(256).IsRequired();
            lease.Property(item => item.ToolId).HasMaxLength(256).IsRequired();
            lease.Property(item => item.IdempotencyKey).HasMaxLength(256).IsRequired();
            lease.Property(item => item.ArgumentHash).HasMaxLength(128).IsRequired();
            lease.Property(item => item.FencingToken).HasMaxLength(256);
            lease.Property(item => item.LeaseId).HasMaxLength(64).IsRequired();
            lease.Property(item => item.Status).HasMaxLength(32).IsRequired();
            lease.Property(item => item.Outcome).HasMaxLength(64);
            lease.HasIndex(item => item.KeyHash).IsUnique();
        });
    }
}

/// <summary>
/// Keys the compiled model by schema so hosts with different assistant schemas never share a model.
/// </summary>
internal sealed class NhAssistantModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime)
    {
        var schema = context is NhAssistantDbContext assistant
            ? assistant.Schema
            : NhAssistantDbContextOptions.DefaultSchema;
        return (context.GetType(), schema, designTime);
    }
}
