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

    public DbSet<AssistantAgent> Agents => Set<AssistantAgent>();

    public DbSet<AssistantAgentMcpServer> AgentMcpServers => Set<AssistantAgentMcpServer>();

    public DbSet<AssistantApplicationContext> ApplicationContexts => Set<AssistantApplicationContext>();

    public DbSet<AssistantMcpServer> McpServers => Set<AssistantMcpServer>();

    public DbSet<AssistantMcpTool> McpTools => Set<AssistantMcpTool>();

    public DbSet<AssistantUserPreference> UserPreferences => Set<AssistantUserPreference>();

    public DbSet<AssistantConversationParticipant> Participants => Set<AssistantConversationParticipant>();

    public DbSet<AssistantPushSubscription> PushSubscriptions => Set<AssistantPushSubscription>();

    public DbSet<AssistantNotificationSetting> NotificationSettings => Set<AssistantNotificationSetting>();

    public DbSet<AssistantThread> Threads => Set<AssistantThread>();

    public DbSet<AssistantRun> Runs => Set<AssistantRun>();

    public DbSet<AssistantQueuedInput> QueuedInputs => Set<AssistantQueuedInput>();

    public DbSet<AssistantSubjectMember> SubjectMembers => Set<AssistantSubjectMember>();

    public DbSet<AssistantArtifact> Artifacts => Set<AssistantArtifact>();

    public DbSet<AssistantConsultation> Consultations => Set<AssistantConsultation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(_storageOptions.Schema);

        modelBuilder.Entity<AssistantConversation>(conversation =>
        {
            conversation.ToTable("AssistantConversation");
            conversation.HasKey(item => item.Id);
            conversation.Property(item => item.Id).ValueGeneratedNever();
            conversation.Property(item => item.Kind)
                .HasMaxLength(16)
                .IsRequired()
                .HasDefaultValue(NhAssistantConversationKinds.Chat);
            conversation.Property(item => item.OwnerActorId).HasMaxLength(256).IsRequired();
            conversation.Property(item => item.OwnerDisplayName).HasMaxLength(NhAssistantParticipantNames.MaxLength);
            conversation.Property(item => item.TenantId).HasMaxLength(256);
            conversation.Property(item => item.AgentId).HasMaxLength(128).IsRequired();
            conversation.Property(item => item.Title).HasMaxLength(200);
            conversation.Property(item => item.Status).HasMaxLength(32).IsRequired();
            conversation.Property(item => item.ActiveActorId).HasMaxLength(256);
            conversation.Property(item => item.ProtectedShareToken).HasMaxLength(1_000);
            conversation.Property(item => item.ConcurrencyStamp).IsConcurrencyToken();
            conversation.HasIndex(item => new { item.OwnerActorId, item.UpdatedAt });
            conversation.HasMany(item => item.Messages)
                .WithOne(item => item.Conversation)
                .HasForeignKey(item => item.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
            conversation.HasMany(item => item.Participants)
                .WithOne()
                .HasForeignKey(item => item.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AssistantMessage>(message =>
        {
            message.ToTable("AssistantMessage");
            message.HasKey(item => item.Id);
            message.Property(item => item.Id).ValueGeneratedNever();
            message.Property(item => item.Role).HasMaxLength(16).IsRequired();
            message.Property(item => item.AuthorActorId).HasMaxLength(256);
            message.Property(item => item.PartsJson).IsRequired();
            message.Property(item => item.ClientMessageId).HasMaxLength(128);
            message.Property(item => item.ClientContextJson).HasMaxLength(4000);
            message.HasIndex(item => new { item.ConversationId, item.CreatedAt });
        });

        modelBuilder.Entity<AssistantConversationParticipant>(participant =>
        {
            participant.ToTable("AssistantConversationParticipant");
            participant.HasKey(item => new { item.ConversationId, item.ActorId });
            participant.Property(item => item.ActorId).HasMaxLength(256);
            participant.Property(item => item.DisplayName).HasMaxLength(NhAssistantParticipantNames.MaxLength).IsRequired();
            participant.Property(item => item.JoinedVia).HasMaxLength(16).IsRequired();
            participant.Property(item => item.InvitedByActorId).HasMaxLength(256);
            participant.HasIndex(item => item.ActorId);
        });

        modelBuilder.Entity<AssistantPushSubscription>(subscription =>
        {
            subscription.ToTable("AssistantPushSubscription");
            subscription.HasKey(item => item.Id);
            subscription.Property(item => item.Id).ValueGeneratedNever();
            subscription.Property(item => item.ActorId).HasMaxLength(256).IsRequired();
            subscription.Property(item => item.EndpointHash).HasMaxLength(64).IsRequired();
            subscription.Property(item => item.Endpoint).HasMaxLength(NhAssistantPushLimits.MaxEndpointLength).IsRequired();
            subscription.Property(item => item.P256dh).HasMaxLength(128).IsRequired();
            subscription.Property(item => item.Auth).HasMaxLength(64).IsRequired();
            subscription.Property(item => item.Language).HasMaxLength(8).IsRequired();
            subscription.HasIndex(item => item.EndpointHash).IsUnique();
            subscription.HasIndex(item => item.ActorId);
        });

        modelBuilder.Entity<AssistantNotificationSetting>(setting =>
        {
            setting.ToTable("AssistantNotificationSetting");
            setting.HasKey(item => item.ActorId);
            setting.Property(item => item.ActorId).HasMaxLength(256);
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
            invocation.Property(item => item.GrantSource)
                .HasConversion<string>()
                .HasMaxLength(16)
                .HasDefaultValue(NhAiGrantSource.None);
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
            approval.Property(item => item.PresentationJson).HasMaxLength(4_000);
            approval.Property(item => item.ArgumentsPreview)
                .HasMaxLength(NhAssistantLimits.MaxPreviewCharacters)
                .IsRequired();
            approval.Property(item => item.TargetsJson).HasMaxLength(4_000).IsRequired();
            approval.Property(item => item.Status).HasMaxLength(32).IsRequired();
            approval.Property(item => item.DecidedByActorId).HasMaxLength(256);
            approval.Property(item => item.Reason).HasMaxLength(500);
            approval.Property(item => item.PolicyId).HasMaxLength(128);
            approval.Property(item => item.Mode)
                .HasMaxLength(16)
                .IsRequired()
                .HasDefaultValue(NhAssistantApprovalModes.Inline);
            approval.Property(item => item.PolicyJson).HasMaxLength(4_000);
            approval.Property(item => item.InitiatorActorId).HasMaxLength(256);
            approval.Property(item => item.SubjectVersion).HasMaxLength(128);
            approval.Property(item => item.GrantSource)
                .HasConversion<string>()
                .HasMaxLength(16)
                .HasDefaultValue(NhAiGrantSource.None);
            approval.Property(item => item.ExecutionResultCode).HasMaxLength(128);
            approval.HasIndex(item => item.ConsultationId);
            approval.Property(item => item.ConcurrencyStamp).IsConcurrencyToken();
            approval.HasIndex(item => item.ProposalHash);
            approval.HasIndex(item => item.ConversationId);
            approval.HasIndex(item => item.RunId);
            // Serves the maintenance job: pending and executing proposals by expiry.
            approval.HasIndex(item => new { item.Status, item.ExpiresAt });
            approval.HasIndex(item => new { item.Status, item.NextReminderAt });
            approval.HasOne<AssistantConversation>()
                .WithMany()
                .HasForeignKey(item => item.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AssistantThread>(thread =>
        {
            thread.ToTable("AssistantThread");
            thread.HasKey(item => item.ConversationId);
            thread.Property(item => item.ScopeKey).HasMaxLength(128).IsRequired();
            thread.Property(item => item.AgentId).HasMaxLength(128).IsRequired();
            thread.Property(item => item.SubjectType).HasMaxLength(NhAiSubjectRef.MaxTypeLength).IsRequired();
            thread.Property(item => item.SubjectId).HasMaxLength(NhAiSubjectRef.MaxIdLength).IsRequired();
            thread.Property(item => item.SubjectDisplayName).HasMaxLength(200);
            thread.Property(item => item.TenantId).HasMaxLength(256);
            thread.Property(item => item.SubjectVersion).HasMaxLength(128);
            thread.Property(item => item.Accountability).HasMaxLength(32).IsRequired();
            thread.Property(item => item.AccountableOwnerId).HasMaxLength(256).IsRequired();
            thread.Property(item => item.MemoryJson).HasMaxLength(NhAssistantLimits.MaxStoredJsonCharacters);
            thread.Property(item => item.MemoryConcurrencyStamp).IsConcurrencyToken();
            thread.HasIndex(item => new { item.ScopeKey, item.AgentId, item.SubjectType, item.SubjectId }).IsUnique();
            thread.HasIndex(item => new { item.SubjectType, item.SubjectId });
            thread.HasOne<AssistantConversation>()
                .WithOne()
                .HasForeignKey<AssistantThread>(item => item.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AssistantRun>(run =>
        {
            run.ToTable("AssistantRun");
            run.HasKey(item => item.Id);
            run.Property(item => item.Id).ValueGeneratedNever();
            run.Property(item => item.TriggerId).HasMaxLength(128).IsRequired();
            run.Property(item => item.TriggerEventKey).HasMaxLength(256);
            run.Property(item => item.InitiatorActorId).HasMaxLength(256);
            run.Property(item => item.AccountableOwnerId).HasMaxLength(256).IsRequired();
            run.Property(item => item.Status).HasMaxLength(32).IsRequired();
            run.Property(item => item.FailureClass).HasMaxLength(32);
            run.Property(item => item.FailureCode).HasMaxLength(128);
            run.Property(item => item.Summary).HasMaxLength(4_000);
            run.Property(item => item.CancelledByActorId).HasMaxLength(256);
            run.Property(item => item.ConcurrencyStamp).IsConcurrencyToken();
            run.HasIndex(item => new { item.ConversationId, item.CreatedAt });
            run.HasIndex(item => new { item.Status, item.CreatedAt });
            run.HasIndex(item => item.BackgroundOperationId);
            run.HasOne<AssistantConversation>()
                .WithMany()
                .HasForeignKey(item => item.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AssistantQueuedInput>(input =>
        {
            input.ToTable("AssistantQueuedInput");
            input.HasKey(item => item.Id);
            input.Property(item => item.Id).ValueGeneratedNever();
            input.Property(item => item.Kind).HasMaxLength(16).IsRequired();
            input.Property(item => item.AuthorActorId).HasMaxLength(256);
            input.Property(item => item.TriggerId).HasMaxLength(128);
            input.Property(item => item.Text).HasMaxLength(8_000).IsRequired();
            input.Property(item => item.ClientMessageId).HasMaxLength(128);
            input.HasIndex(item => new { item.ConversationId, item.ConsumedAt, item.CreatedAt });
            input.HasOne<AssistantConversation>()
                .WithMany()
                .HasForeignKey(item => item.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AssistantArtifact>(artifact =>
        {
            artifact.ToTable("AssistantArtifact");
            artifact.HasKey(item => item.Id);
            artifact.Property(item => item.Id).ValueGeneratedNever();
            artifact.Property(item => item.Name).HasMaxLength(200).IsRequired();
            artifact.Property(item => item.ContentType).HasMaxLength(100).IsRequired();
            artifact.Property(item => item.Sha256).HasMaxLength(64).IsRequired();
            artifact.Property(item => item.Content).IsRequired();
            artifact.HasIndex(item => new { item.ConversationId, item.Name, item.Version }).IsUnique();
            artifact.HasOne<AssistantConversation>()
                .WithMany()
                .HasForeignKey(item => item.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AssistantConsultation>(consultation =>
        {
            consultation.ToTable("AssistantConsultation");
            consultation.HasKey(item => item.Id);
            consultation.Property(item => item.Id).ValueGeneratedNever();
            consultation.Property(item => item.ScopeKey).HasMaxLength(128).IsRequired();
            consultation.Property(item => item.AgentId).HasMaxLength(128).IsRequired();
            consultation.Property(item => item.SubjectType).HasMaxLength(NhAiSubjectRef.MaxTypeLength).IsRequired();
            consultation.Property(item => item.SubjectId).HasMaxLength(NhAiSubjectRef.MaxIdLength).IsRequired();
            consultation.Property(item => item.AskerActorId).HasMaxLength(256).IsRequired();
            consultation.Property(item => item.AskingAgentId).HasMaxLength(128);
            consultation.Property(item => item.Status).HasMaxLength(32).IsRequired();
            consultation.Property(item => item.FailureCode).HasMaxLength(128);
            // The thread timeline, the consulted agent's daily budget and the asker's daily limit.
            consultation.HasIndex(item => new { item.ConversationId, item.CreatedAt });
            consultation.HasIndex(item => new { item.AgentId, item.ScopeKey, item.CreatedAt });
            consultation.HasIndex(item => new { item.AskerActorId, item.CreatedAt });
            consultation.HasOne<AssistantConversation>()
                .WithMany()
                .HasForeignKey(item => item.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AssistantSubjectMember>(member =>
        {
            member.ToTable("AssistantSubjectMember");
            member.HasKey(item => new { item.ScopeKey, item.SubjectType, item.SubjectId, item.ActorId });
            member.Property(item => item.ScopeKey).HasMaxLength(128);
            member.Property(item => item.SubjectType).HasMaxLength(NhAiSubjectRef.MaxTypeLength);
            member.Property(item => item.SubjectId).HasMaxLength(NhAiSubjectRef.MaxIdLength);
            member.Property(item => item.ActorId).HasMaxLength(256);
            member.Property(item => item.Role).HasConversion<int>();
            member.Property(item => item.NamedRolesJson).HasMaxLength(1_000);
            member.Property(item => item.DisplayName).HasMaxLength(200);
            // Serves the inbox: the subjects an actor may decide.
            member.HasIndex(item => new { item.ActorId, item.Role });
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

        modelBuilder.Entity<AssistantAgent>(agent =>
        {
            agent.ToTable("AssistantAgent");
            agent.HasKey(item => item.Id);
            agent.Property(item => item.Id).HasMaxLength(128);
            agent.Property(item => item.Source).HasMaxLength(16).IsRequired();
            agent.Property(item => item.DisplayName).HasMaxLength(256).IsRequired();
            agent.Property(item => item.Description).HasMaxLength(512).IsRequired();
            agent.Property(item => item.ProfileName).HasMaxLength(128).IsRequired();
            agent.Property(item => item.Instructions).IsRequired();
            agent.Property(item => item.InstructionsAssetId).HasMaxLength(128).IsRequired();
            agent.Property(item => item.InstructionsHash).HasMaxLength(64).IsRequired();
            agent.Property(item => item.ToolSelectorsJson).HasMaxLength(NhAssistantLimits.MaxStoredToolSelectorCharacters).IsRequired();
            agent.Property(item => item.RequiredPolicy).HasMaxLength(256);
            agent.Property(item => item.Autonomy).HasConversion<string>().HasMaxLength(16);
            agent.Property(item => item.CodeHash).HasMaxLength(64);
            agent.Property(item => item.UpdatedBy).HasMaxLength(256);
            agent.Property(item => item.Version).IsConcurrencyToken();
            agent.HasMany(item => item.McpServers)
                .WithOne()
                .HasForeignKey(item => item.AgentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AssistantMcpServer>(server =>
        {
            server.ToTable("AssistantMcpServer");
            server.HasKey(item => item.Id);
            server.Property(item => item.Id).HasMaxLength(40);
            server.Property(item => item.DisplayName).HasMaxLength(200).IsRequired();
            server.Property(item => item.Url).HasMaxLength(2_048).IsRequired();
            server.Property(item => item.AuthMode).HasMaxLength(32).IsRequired();
            server.Property(item => item.HeaderName).HasMaxLength(128);
            server.Property(item => item.ProtectedSecret).HasMaxLength(4_000);
            server.Property(item => item.RequiredPolicy).HasMaxLength(256);
            server.Property(item => item.LastSyncStatus).HasMaxLength(16);
            server.HasMany(item => item.Tools)
                .WithOne()
                .HasForeignKey(item => item.ServerId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AssistantAgentMcpServer>(assignment =>
        {
            assignment.ToTable("AssistantAgentMcpServer");
            assignment.HasKey(item => new { item.AgentId, item.McpServerId });
            assignment.Property(item => item.AgentId).HasMaxLength(128);
            assignment.Property(item => item.McpServerId).HasMaxLength(40);
            assignment.HasOne<AssistantMcpServer>()
                .WithMany()
                .HasForeignKey(item => item.McpServerId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AssistantMcpTool>(tool =>
        {
            tool.ToTable("AssistantMcpTool");
            tool.HasKey(item => new { item.ServerId, item.RemoteName });
            tool.Property(item => item.ServerId).HasMaxLength(40);
            tool.Property(item => item.RemoteName).HasMaxLength(128);
            tool.Property(item => item.LocalId).HasMaxLength(256).IsRequired();
            tool.Property(item => item.Description).HasMaxLength(1_000).IsRequired();
            tool.Property(item => item.InputSchemaHash).HasMaxLength(64).IsRequired();
            tool.Property(item => item.Effect).HasMaxLength(16).IsRequired();
            tool.Property(item => item.DescriptionOverride).HasMaxLength(1_000);
            tool.Property(item => item.Status).HasMaxLength(32).IsRequired();
        });

        modelBuilder.Entity<AssistantApplicationContext>(context =>
        {
            context.ToTable("AssistantApplicationContext");
            context.HasKey(item => new { item.Id, item.Version });
            context.Property(item => item.Id).HasMaxLength(64);
            context.Property(item => item.Text).HasMaxLength(NhAssistantApplicationContexts.MaxTextLength).IsRequired();
            context.Property(item => item.Hash).HasMaxLength(64).IsRequired();
            context.Property(item => item.UpdatedBy).HasMaxLength(256);
        });

        modelBuilder.Entity<AssistantUserPreference>(preference =>
        {
            preference.ToTable("AssistantUserPreference");
            preference.HasKey(item => item.ActorId);
            preference.Property(item => item.ActorId).HasMaxLength(256);
            preference.Property(item => item.Style).HasMaxLength(16).IsRequired();
            preference.Property(item => item.AddressForm).HasMaxLength(16).IsRequired();
            preference.Property(item => item.ResponseLength).HasMaxLength(16).IsRequired();
            preference.Property(item => item.CustomInstructions).HasMaxLength(1_000);
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
