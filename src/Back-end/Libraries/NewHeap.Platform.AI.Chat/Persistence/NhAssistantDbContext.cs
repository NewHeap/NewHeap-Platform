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

    public DbSet<AssistantAgentMetadata> AgentMetadata => Set<AssistantAgentMetadata>();

    public DbSet<AssistantAgentSkillState> AgentSkillStates => Set<AssistantAgentSkillState>();

    public DbSet<AssistantApplicationContext> ApplicationContexts => Set<AssistantApplicationContext>();

    public DbSet<AssistantMcpServer> McpServers => Set<AssistantMcpServer>();

    public DbSet<AssistantMcpTool> McpTools => Set<AssistantMcpTool>();

    public DbSet<AssistantMcpReader> McpReaders => Set<AssistantMcpReader>();

    public DbSet<AssistantMcpConnection> McpConnections => Set<AssistantMcpConnection>();

    public DbSet<AssistantMcpAuthorizationFlow> McpAuthorizationFlows => Set<AssistantMcpAuthorizationFlow>();

    public DbSet<AssistantMcpClientRegistration> McpClientRegistrations => Set<AssistantMcpClientRegistration>();

    public DbSet<AssistantMcpConnectionNotice> McpConnectionNotices => Set<AssistantMcpConnectionNotice>();

    public DbSet<AssistantUserPreference> UserPreferences => Set<AssistantUserPreference>();

    public DbSet<AssistantConversationParticipant> Participants => Set<AssistantConversationParticipant>();

    public DbSet<AssistantPushSubscription> PushSubscriptions => Set<AssistantPushSubscription>();

    public DbSet<AssistantNotificationSetting> NotificationSettings => Set<AssistantNotificationSetting>();

    public DbSet<AssistantThread> Threads => Set<AssistantThread>();

    public DbSet<AssistantRun> Runs => Set<AssistantRun>();

    public DbSet<AssistantQueuedInput> QueuedInputs => Set<AssistantQueuedInput>();

    public DbSet<AssistantSubjectMember> SubjectMembers => Set<AssistantSubjectMember>();

    public DbSet<AssistantArtifact> Artifacts => Set<AssistantArtifact>();

    public DbSet<AssistantFileReaderText> FileReaderTexts => Set<AssistantFileReaderText>();

    public DbSet<AssistantConsultation> Consultations => Set<AssistantConsultation>();

    public DbSet<AssistantHandOff> HandOffs => Set<AssistantHandOff>();

    public DbSet<AssistantAgentReplay> AgentReplays => Set<AssistantAgentReplay>();

    public DbSet<AssistantProposalFeedback> ProposalFeedback => Set<AssistantProposalFeedback>();

    public DbSet<AssistantApprovalKey> ApprovalKeys => Set<AssistantApprovalKey>();

    public DbSet<AssistantAgentSkillDefinition> AgentSkillDefinitions => Set<AssistantAgentSkillDefinition>();

    public DbSet<AssistantAgentTriggerDefinition> AgentTriggerDefinitions => Set<AssistantAgentTriggerDefinition>();

    public DbSet<AssistantRunConfigurationStamp> RunConfigurationStamps => Set<AssistantRunConfigurationStamp>();

    public DbSet<AssistantAgentRunConfiguration> AgentRunConfigurations => Set<AssistantAgentRunConfiguration>();

    public DbSet<AssistantAgentConsultationDefinition> AgentConsultationDefinitions => Set<AssistantAgentConsultationDefinition>();

    public DbSet<AssistantAgentGroupMember> AgentGroupMembers => Set<AssistantAgentGroupMember>();

    public DbSet<AssistantBoardVisit> BoardVisits => Set<AssistantBoardVisit>();

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
            invocation.Property(item => item.ReadForActorId).HasMaxLength(256);
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
            approval.Property(item => item.PresentationsJson).HasMaxLength(Runtime.NhAssistantToolPresentationResolver.MaxLocalizedSerializedLength);
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
            approval.Property(item => item.ParentSubjectType).HasMaxLength(NhAiSubjectRef.MaxTypeLength);
            approval.Property(item => item.ParentSubjectId).HasMaxLength(NhAiSubjectRef.MaxIdLength);
            approval.Property(item => item.ParentDisplayName).HasMaxLength(NhAiSubjectParent.MaxDisplayNameLength);
            approval.Property(item => item.GrantSource)
                .HasConversion<string>()
                .HasMaxLength(16)
                .HasDefaultValue(NhAiGrantSource.None);
            approval.Property(item => item.ExecutionResultCode).HasMaxLength(128);
            approval.Property(item => item.StaleKind).HasMaxLength(32);
            approval.Property(item => item.StaleExplanationsJson).HasMaxLength(NhAssistantLimits.MaxStaleExplanationsSerializedLength);
            approval.Property(item => item.StaleByActorId).HasMaxLength(256);
            approval.Property(item => item.ArgumentsHash).HasMaxLength(64);
            approval.Property(item => item.RationalesJson).HasMaxLength(NhAssistantLimits.MaxStaleExplanationsSerializedLength);
            // Finds an open or recently rejected proposal with the same identity.
            approval.HasIndex(item => new { item.ToolId, item.ArgumentsHash });
            approval.HasIndex(item => item.ConsultationId);
            approval.Property(item => item.ConcurrencyStamp).IsConcurrencyToken();
            approval.HasIndex(item => item.ProposalHash);
            approval.HasIndex(item => item.ConversationId);
            approval.HasIndex(item => item.RunId);
            // Serves the maintenance job: pending and executing proposals by expiry.
            approval.HasIndex(item => new { item.Status, item.ExpiresAt });
            approval.HasIndex(item => new { item.Status, item.NextReminderAt });
            // Serves the maintenance job: proposals that wait for the application's report by deadline.
            approval.HasIndex(item => new { item.Status, item.ExecutionReportDueAt });
            approval.HasOne<AssistantConversation>()
                .WithMany()
                .HasForeignKey(item => item.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AssistantApprovalKey>(key =>
        {
            key.ToTable("AssistantApprovalKey");
            key.HasKey(item => new { item.ApprovalId, item.KeyType, item.KeyId });
            key.Property(item => item.ScopeKey).HasMaxLength(128).IsRequired();
            key.Property(item => item.KeyType).HasMaxLength(NhAiProposalKey.MaxTypeLength).IsRequired();
            key.Property(item => item.KeyId).HasMaxLength(NhAiProposalKey.MaxIdLength).IsRequired();
            // Revalidation by object finds the proposals that change it within one scope.
            key.HasIndex(item => new { item.ScopeKey, item.KeyType, item.KeyId });
            key.HasOne<AssistantApproval>()
                .WithMany()
                .HasForeignKey(item => item.ApprovalId)
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
            thread.HasIndex(item => new { item.ScopeKey, item.AgentId, item.SubjectType, item.SubjectId, item.Shadow }).IsUnique();
            thread.HasIndex(item => new { item.SubjectType, item.SubjectId });
            // The runs of one agent, for its runs view and people's run history.
            thread.HasIndex(item => new { item.AgentId, item.SubjectType });
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
            run.Property(item => item.TriggerKind).HasMaxLength(16);
            run.Property(item => item.InitiatorActorId).HasMaxLength(256);
            run.Property(item => item.AccountableOwnerId).HasMaxLength(256).IsRequired();
            run.Property(item => item.Status).HasMaxLength(32).IsRequired();
            run.Property(item => item.FailureClass).HasMaxLength(32);
            run.Property(item => item.FailureCode).HasMaxLength(128);
            run.Property(item => item.Summary).HasMaxLength(4_000);
            run.Property(item => item.Result).HasMaxLength(NhAssistantLimits.MaxRunResultCharacters);
            // At most eight cultures of at most 20,000 characters each, bounded before it is stored.
            run.Property(item => item.ResultsJson);
            run.Property(item => item.McpConnectionSkips).HasMaxLength(NewHeap.Platform.AI.Chat.Runs.NhAssistantRunMcpSkips.MaxLength);
            run.Property(item => item.Kind)
                .HasMaxLength(24)
                .IsRequired()
                .HasDefaultValue(NhAssistantRunKinds.Model);
            run.Property(item => item.CancelledByActorId).HasMaxLength(256);
            run.Property(item => item.SkillId).HasMaxLength(64);
            run.Property(item => item.SkillChosenBy).HasMaxLength(16);
            run.Property(item => item.RequestedByAgentId).HasMaxLength(128);
            // The evaluation pairs a replayed run with the run it replays.
            run.HasIndex(item => item.ReplayOfRunId);
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
            input.Property(item => item.PayloadJson).HasMaxLength(NhAssistantLimits.MaxInputPayloadCharacters);
            input.Property(item => item.EventKey).HasMaxLength(256);
            input.Property(item => item.InitiatorActorId).HasMaxLength(256);
            input.Property(item => item.SkillId).HasMaxLength(64);
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

        modelBuilder.Entity<AssistantFileReaderText>(text =>
        {
            text.ToTable("AssistantFileReaderText");
            text.HasKey(item => item.Id);
            text.Property(item => item.Id).ValueGeneratedNever();
            text.Property(item => item.FileHash).HasMaxLength(80).IsRequired();
            text.Property(item => item.ReaderId).HasMaxLength(128).IsRequired();
            text.Property(item => item.ProtectedText).IsRequired();
            text.HasIndex(item => new { item.ConversationId, item.FileHash, item.ReaderId }).IsUnique();
            text.HasIndex(item => item.ExpiresAt);
            text.HasOne<AssistantConversation>()
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

        modelBuilder.Entity<AssistantAgentReplay>(replay =>
        {
            replay.ToTable("AssistantAgentReplay");
            replay.HasKey(item => item.Id);
            replay.Property(item => item.Id).ValueGeneratedNever();
            replay.Property(item => item.VariantAgentId).HasMaxLength(128).IsRequired();
            replay.Property(item => item.SourceAgentId).HasMaxLength(128).IsRequired();
            replay.Property(item => item.Status).HasMaxLength(16).IsRequired();
            replay.Property(item => item.StopReason).HasMaxLength(128);
            replay.Property(item => item.RequestedByActorId).HasMaxLength(256).IsRequired();
            replay.Property(item => item.ConcurrencyStamp).IsConcurrencyToken();
            // The active replay of a variant and its latest replays.
            replay.HasIndex(item => new { item.VariantAgentId, item.Status });
            replay.HasIndex(item => new { item.VariantAgentId, item.CreatedAt });
        });

        modelBuilder.Entity<AssistantHandOff>(handOff =>
        {
            handOff.ToTable("AssistantHandOff");
            handOff.HasKey(item => item.Id);
            handOff.Property(item => item.Id).ValueGeneratedNever();
            handOff.Property(item => item.ScopeKey).HasMaxLength(128).IsRequired();
            handOff.Property(item => item.SourceAgentId).HasMaxLength(128).IsRequired();
            handOff.Property(item => item.SourceSkillId).HasMaxLength(64);
            handOff.Property(item => item.TargetAgentId).HasMaxLength(128).IsRequired();
            handOff.Property(item => item.SubjectType).HasMaxLength(NhAiSubjectRef.MaxTypeLength).IsRequired();
            handOff.Property(item => item.SubjectId).HasMaxLength(NhAiSubjectRef.MaxIdLength).IsRequired();
            handOff.Property(item => item.Outcome).HasMaxLength(16).IsRequired();
            handOff.Property(item => item.Code).HasMaxLength(128);
            // One hand-off per source run, target and subject; the daily limit per pair and subject;
            // the run that claims the input; the chain and the follow-ups of a target run.
            handOff.HasIndex(item => new { item.SourceRunId, item.TargetAgentId, item.SubjectType, item.SubjectId }).IsUnique();
            handOff.HasIndex(item => new { item.SourceAgentId, item.TargetAgentId, item.SubjectType, item.SubjectId, item.CreatedAt });
            handOff.HasIndex(item => item.InputId);
            handOff.HasIndex(item => item.TargetRunId);
            handOff.HasOne<AssistantConversation>()
                .WithMany()
                .HasForeignKey(item => item.SourceConversationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AssistantProposalFeedback>(feedback =>
        {
            feedback.ToTable("AssistantProposalFeedback");
            feedback.HasKey(item => item.Id);
            feedback.Property(item => item.Id).ValueGeneratedNever();
            feedback.Property(item => item.TargetKind).HasMaxLength(32).IsRequired();
            feedback.Property(item => item.ScopeKey).HasMaxLength(128).IsRequired();
            feedback.Property(item => item.AgentId).HasMaxLength(128).IsRequired();
            feedback.Property(item => item.ToolId).HasMaxLength(256).IsRequired();
            feedback.Property(item => item.ToolContractHash).HasMaxLength(128).IsRequired();
            feedback.Property(item => item.SubjectType).HasMaxLength(NhAiSubjectRef.MaxTypeLength).IsRequired();
            feedback.Property(item => item.SubjectId).HasMaxLength(NhAiSubjectRef.MaxIdLength).IsRequired();
            feedback.Property(item => item.ApprovalStatus).HasMaxLength(32).IsRequired();
            feedback.Property(item => item.Category).HasMaxLength(32).IsRequired();
            feedback.Property(item => item.Text).HasMaxLength(NhAssistantLimits.MaxFeedbackCharacters);
            feedback.Property(item => item.AuthorActorId).HasMaxLength(256).IsRequired();
            // One record per person and proposal; the report reads by agent, period and tool.
            feedback.HasIndex(item => new { item.ApprovalId, item.AuthorActorId }).IsUnique();
            feedback.HasIndex(item => new { item.ScopeKey, item.AgentId, item.CreatedAt });
            feedback.HasIndex(item => new { item.AgentId, item.ToolId });
            // The proposal cascades from its conversation, so the feedback leaves with the thread.
            feedback.HasOne<AssistantApproval>()
                .WithMany()
                .HasForeignKey(item => item.ApprovalId)
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
            agent.Property(item => item.DataClassification).HasMaxLength(16);
            agent.Property(item => item.Instructions).IsRequired();
            agent.Property(item => item.InstructionsAssetId).HasMaxLength(128).IsRequired();
            agent.Property(item => item.InstructionsHash).HasMaxLength(64).IsRequired();
            agent.Property(item => item.ToolSelectorsJson).HasMaxLength(NhAssistantLimits.MaxStoredToolSelectorCharacters).IsRequired();
            agent.Property(item => item.RequiredPolicy).HasMaxLength(256);
            agent.Property(item => item.Autonomy).HasConversion<string>().HasMaxLength(16);
            agent.Property(item => item.CodeHash).HasMaxLength(64);
            agent.Property(item => item.UpdatedBy).HasMaxLength(256);
            agent.Property(item => item.RunState).HasMaxLength(16).IsRequired().HasDefaultValue(NhAssistantAgentRunStates.Active);
            agent.Property(item => item.StateChangedBy).HasMaxLength(256);
            agent.Property(item => item.StateReason).HasMaxLength(NhAssistantLimits.MaxStateReasonCharacters);
            agent.Property(item => item.ShadowChangedBy).HasMaxLength(256);
            agent.Property(item => item.ShadowReason).HasMaxLength(NhAssistantLimits.MaxStateReasonCharacters);
            agent.Property(item => item.ServiceAccountActorId).HasMaxLength(256);
            agent.Property(item => item.ServiceAccountChangedBy).HasMaxLength(256);
            agent.Property(item => item.ServiceAccountReason).HasMaxLength(NhAssistantLimits.MaxStateReasonCharacters);
            agent.Property(item => item.ConsultRoutesChangedBy).HasMaxLength(256);
            agent.Property(item => item.ConsultRoutesReason).HasMaxLength(NhAssistantLimits.MaxStateReasonCharacters);
            agent.Property(item => item.WorksOnSubjectTypesJson)
                .HasMaxLength(NhAssistantAgentWorksOn.MaxStoredSubjectTypeCharacters)
                .IsRequired()
                .HasDefaultValue("[]");
            // Agents that existed before they said what they work on work on every object type; the
            // sentinel lets EF send false and leave true to the default.
            agent.Property(item => item.WorksOnAllSubjects).HasDefaultValue(true).HasSentinel(true);
            agent.Property(item => item.RunPolicy).HasMaxLength(NhAssistantAgentWorksOn.MaxPolicyLength);
            agent.Property(item => item.ViewPolicy).HasMaxLength(NhAssistantAgentWorksOn.MaxPolicyLength);
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
            server.Property(item => item.SecretReference).HasMaxLength(NhAssistantMcpSecretReferences.MaxLength);
            server.Property(item => item.UserName).HasMaxLength(256);
            server.Property(item => item.ClientCertificateReference).HasMaxLength(128);
            server.Property(item => item.Transport)
                .HasMaxLength(32)
                .IsRequired()
                .HasDefaultValue(NhAssistantMcpTransports.StreamableHttp);
            server.Property(item => item.RequiredPolicy).HasMaxLength(256);
            server.Property(item => item.HeadlessUse)
                .HasMaxLength(32)
                .IsRequired()
                .HasDefaultValue(NhAssistantMcpHeadlessUses.Never);
            server.Property(item => item.LastSyncStatus).HasMaxLength(16);
            server.Property(item => item.LastConnectCode).HasMaxLength(64);
            server.Property(item => item.ConnectionScope)
                .HasMaxLength(16)
                .IsRequired()
                .HasDefaultValue(NhAssistantMcpConnectionScopes.Organization);
            server.Property(item => item.ResourceUri).HasMaxLength(2_048);
            server.Property(item => item.AuthorizationServerIssuer).HasMaxLength(2_048);
            server.Property(item => item.OAuthScopes).HasMaxLength(1_024);
            server.Property(item => item.OAuthClientId).HasMaxLength(512);
            server.Property(item => item.ProtectedOAuthClientSecret).HasMaxLength(4_000);
            server.Property(item => item.OAuthClientSecretReference).HasMaxLength(NhAssistantMcpSecretReferences.MaxLength);
            server.Property(item => item.OrganizationAccountLabel).HasMaxLength(200);
            server.Property(item => item.AuthorizationConfirmedBy).HasMaxLength(128);
            server.HasMany(item => item.Tools)
                .WithOne()
                .HasForeignKey(item => item.ServerId)
                .OnDelete(DeleteBehavior.Cascade);
            server.HasMany(item => item.Readers)
                .WithOne()
                .HasForeignKey(item => item.ServerId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AssistantMcpReader>(reader =>
        {
            reader.ToTable("AssistantMcpReader");
            reader.HasKey(item => new { item.ServerId, item.ToolName });
            reader.Property(item => item.ServerId).HasMaxLength(40);
            reader.Property(item => item.ToolName).HasMaxLength(NhAssistantMcpReaders.MaxToolNameLength);
            reader.Property(item => item.MediaTypes).HasMaxLength(NhAssistantMcpReaders.MaxMediaTypesLength).IsRequired();
            reader.Property(item => item.MaxClassification).HasMaxLength(16).IsRequired();
        });

        modelBuilder.Entity<AssistantAgentSkillDefinition>(definition =>
        {
            definition.ToTable("AssistantAgentSkillDefinition");
            definition.HasKey(item => item.Id);
            definition.Property(item => item.AgentId).HasMaxLength(128).IsRequired();
            definition.Property(item => item.SkillId).HasMaxLength(64).IsRequired();
            definition.Property(item => item.Status).HasMaxLength(16).IsRequired();
            definition.Property(item => item.Description).HasMaxLength(NhAssistantSkillBuilder.MaxDescriptionLength).IsRequired();
            definition.Property(item => item.Instructions).HasMaxLength(NhAssistantAgentConfigurationLimits.MaxSkillInstructionsLength);
            definition.Property(item => item.InstructionsHash).HasMaxLength(64);
            definition.Property(item => item.InstructionsAssetId).HasMaxLength(NhAssistantInstructionAssetReference.MaxAssetIdLength);
            definition.Property(item => item.ToolSelectorsJson).HasMaxLength(NhAssistantLimits.MaxStoredToolSelectorCharacters).IsRequired();
            definition.Property(item => item.ProfileName).HasMaxLength(128);
            definition.Property(item => item.DataClassification).HasMaxLength(16);
            definition.Property(item => item.RuleHandlerName).HasMaxLength(NhAssistantBlockNames.MaxLength);
            definition.Property(item => item.ApprovalPolicyId).HasMaxLength(128);
            definition.Property(item => item.CreatedBy).HasMaxLength(256).IsRequired();
            definition.Property(item => item.Reason).HasMaxLength(NhAssistantLimits.MaxStateReasonCharacters);
            definition.HasIndex(item => new { item.AgentId, item.SkillId, item.Version }).IsUnique();
            definition.HasIndex(item => new { item.AgentId, item.Status });
            definition.HasOne<AssistantAgent>()
                .WithMany()
                .HasForeignKey(item => item.AgentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AssistantAgentTriggerDefinition>(trigger =>
        {
            trigger.ToTable("AssistantAgentTriggerDefinition");
            trigger.HasKey(item => item.Id);
            trigger.Property(item => item.Id).HasMaxLength(64);
            trigger.Property(item => item.AgentId).HasMaxLength(128).IsRequired();
            trigger.Property(item => item.SkillId).HasMaxLength(64);
            trigger.Property(item => item.Source).HasMaxLength(16).IsRequired();
            trigger.Property(item => item.EventName).HasMaxLength(NhAssistantBlockNames.MaxLength);
            trigger.Property(item => item.FiltersJson).HasMaxLength(NhAssistantDataTriggerFilters.MaxJsonLength);
            trigger.Property(item => item.Cron).HasMaxLength(128);
            trigger.Property(item => item.TimeZone).HasMaxLength(64);
            trigger.Property(item => item.SubjectSetName).HasMaxLength(NhAssistantBlockNames.MaxLength);
            trigger.Property(item => item.Message).HasMaxLength(NhAssistantAgentConfigurationLimits.MaxTriggerMessageLength);
            trigger.Property(item => item.Conflict).HasMaxLength(16).IsRequired();
            trigger.Property(item => item.CreatedBy).HasMaxLength(256).IsRequired();
            trigger.Property(item => item.UpdatedBy).HasMaxLength(256).IsRequired();
            trigger.Property(item => item.Reason).HasMaxLength(NhAssistantLimits.MaxStateReasonCharacters);
            trigger.Property(item => item.ConcurrencyStamp).IsConcurrencyToken();
            trigger.HasIndex(item => item.AgentId);
            trigger.HasOne<AssistantAgent>()
                .WithMany()
                .HasForeignKey(item => item.AgentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AssistantAgentRunConfiguration>(settings =>
        {
            settings.ToTable("AssistantAgentRunConfiguration");
            settings.HasKey(item => item.AgentId);
            settings.Property(item => item.AgentId).HasMaxLength(128);
            settings.Property(item => item.Accountability).HasMaxLength(32).IsRequired();
            settings.Property(item => item.ServiceAccountActorId).HasMaxLength(256);
            settings.Property(item => item.ElevatedReads).HasMaxLength(16).IsRequired();
            settings.Property(item => item.ApprovalPolicyId).HasMaxLength(128);
            settings.Property(item => item.TriageProfileName).HasMaxLength(128);
            settings.Property(item => item.RuleHandlerName).HasMaxLength(NhAssistantBlockNames.MaxLength);
            settings.Property(item => item.CreatedBy).HasMaxLength(256).IsRequired();
            settings.Property(item => item.UpdatedBy).HasMaxLength(256).IsRequired();
            settings.Property(item => item.Reason).HasMaxLength(NhAssistantLimits.MaxStateReasonCharacters);
            settings.Property(item => item.ConcurrencyStamp).IsConcurrencyToken();
            settings.HasOne<AssistantAgent>()
                .WithOne()
                .HasForeignKey<AssistantAgentRunConfiguration>(item => item.AgentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AssistantAgentConsultationDefinition>(consultation =>
        {
            consultation.ToTable("AssistantAgentConsultationDefinition");
            consultation.HasKey(item => new { item.AgentId, item.SubjectType });
            consultation.Property(item => item.AgentId).HasMaxLength(128);
            consultation.Property(item => item.SubjectType).HasMaxLength(NhAiSubjectRef.MaxTypeLength);
            consultation.Property(item => item.Description).HasMaxLength(NhAssistantAgentConfigurationLimits.MaxConsultationDescriptionLength).IsRequired();
            consultation.Property(item => item.Instructions).HasMaxLength(NhAssistantAgentConfigurationLimits.MaxConsultationInstructionsLength);
            consultation.Property(item => item.InstructionsHash).HasMaxLength(64);
            consultation.Property(item => item.MinimumRole).HasMaxLength(16).IsRequired();
            consultation.Property(item => item.AskerPolicy).HasMaxLength(NhAssistantAgentWorksOn.MaxPolicyLength);
            consultation.Property(item => item.RoutesJson).HasMaxLength(200).IsRequired();
            consultation.Property(item => item.ProposalRole).HasMaxLength(16).IsRequired();
            consultation.Property(item => item.ProposalPolicyId).HasMaxLength(128);
            consultation.Property(item => item.ToolSelectorsJson).HasMaxLength(NhAssistantLimits.MaxStoredToolSelectorCharacters);
            consultation.Property(item => item.DataClassification).HasMaxLength(16);
            consultation.Property(item => item.CreatedBy).HasMaxLength(256).IsRequired();
            consultation.Property(item => item.UpdatedBy).HasMaxLength(256).IsRequired();
            consultation.Property(item => item.Reason).HasMaxLength(NhAssistantLimits.MaxStateReasonCharacters);
            consultation.Property(item => item.ConcurrencyStamp).IsConcurrencyToken();
            consultation.HasOne<AssistantAgent>()
                .WithMany()
                .HasForeignKey(item => item.AgentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AssistantRunConfigurationStamp>(stamp =>
        {
            stamp.ToTable("AssistantRunConfigurationStamp");
            stamp.HasKey(item => item.Id);
            stamp.Property(item => item.Id).ValueGeneratedNever();
        });

        modelBuilder.Entity<AssistantAgentSkillState>(skill =>
        {
            skill.ToTable("AssistantAgentSkillState");
            skill.HasKey(item => new { item.AgentId, item.SkillId });
            skill.Property(item => item.AgentId).HasMaxLength(128);
            skill.Property(item => item.SkillId).HasMaxLength(64);
            skill.Property(item => item.RunState).HasMaxLength(16).IsRequired();
            skill.Property(item => item.StateChangedBy).HasMaxLength(256);
            skill.Property(item => item.StateReason).HasMaxLength(NhAssistantLimits.MaxStateReasonCharacters);
            skill.Property(item => item.ShadowChangedBy).HasMaxLength(256);
            skill.Property(item => item.ShadowReason).HasMaxLength(NhAssistantLimits.MaxStateReasonCharacters);
            skill.HasOne<AssistantAgent>()
                .WithMany()
                .HasForeignKey(item => item.AgentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AssistantAgentGroupMember>(member =>
        {
            member.ToTable("AssistantAgentGroupMember");
            member.HasKey(item => new { item.AgentId, item.GroupId });
            member.Property(item => item.AgentId).HasMaxLength(128);
            member.Property(item => item.GroupId).HasMaxLength(NhAssistantAgentGroupDefinition.MaxIdLength);
            member.Property(item => item.ChangedBy).HasMaxLength(256);
            member.HasIndex(item => item.GroupId);
            member.HasOne<AssistantAgent>()
                .WithMany()
                .HasForeignKey(item => item.AgentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AssistantBoardVisit>(visit =>
        {
            visit.ToTable("AssistantBoardVisit");
            visit.HasKey(item => new { item.ActorId, item.GroupId, item.ScopeKey });
            visit.Property(item => item.ActorId).HasMaxLength(256);
            visit.Property(item => item.GroupId).HasMaxLength(NhAssistantAgentGroupDefinition.MaxIdLength);
            visit.Property(item => item.ScopeKey).HasMaxLength(128);
        });

        modelBuilder.Entity<AssistantAgentMetadata>(metadata =>
        {
            metadata.ToTable("AssistantAgentMetadata");
            metadata.HasKey(item => new { item.AgentId, item.Name });
            metadata.Property(item => item.AgentId).HasMaxLength(128);
            metadata.Property(item => item.Name).HasMaxLength(NhAssistantAgentMetadataLimits.MaxNameLength);
            metadata.Property(item => item.ValueJson).IsRequired();
            metadata.Property(item => item.IndexValue).HasMaxLength(NhAssistantAgentMetadataLimits.MaxIndexValueLength);
            metadata.Property(item => item.ChangedBy).HasMaxLength(256).IsRequired();
            metadata.Property(item => item.Version).IsConcurrencyToken();
            // ListAgentsAsync finds agents by an entry's searchable value.
            metadata.HasIndex(item => new { item.Name, item.IndexValue });
            metadata.HasOne<AssistantAgent>()
                .WithMany()
                .HasForeignKey(item => item.AgentId)
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

        modelBuilder.Entity<AssistantMcpConnection>(connection =>
        {
            connection.ToTable("AssistantMcpConnection");
            connection.HasKey(item => item.Id);
            connection.Property(item => item.ServerId).HasMaxLength(40).IsRequired();
            connection.Property(item => item.OwnerKind).HasMaxLength(16).IsRequired();
            connection.Property(item => item.OwnerActorId).HasMaxLength(128).IsRequired();
            connection.Property(item => item.TenantKey).HasMaxLength(128).IsRequired();
            connection.Property(item => item.ConsentHash).HasMaxLength(64);
            connection.Property(item => item.Status).HasMaxLength(32);
            connection.Property(item => item.Issuer).HasMaxLength(2_048);
            connection.Property(item => item.ClientId).HasMaxLength(512);
            connection.Property(item => item.Resource).HasMaxLength(2_048);
            connection.Property(item => item.GrantedScopes).HasMaxLength(1_024);
            connection.Property(item => item.RequiredScopes).HasMaxLength(1_024);
            connection.Property(item => item.ConnectedBy).HasMaxLength(128);
            connection.Property(item => item.LastFailureCode).HasMaxLength(64);
            connection.Property(item => item.RefreshLeaseOwner).HasMaxLength(64);
            connection.Property(item => item.Version).IsConcurrencyToken();
            connection.HasIndex(item => new { item.ServerId, item.OwnerKind, item.OwnerActorId, item.TenantKey }).IsUnique();
            connection.HasIndex(item => new { item.OwnerActorId, item.OwnerKind });
            connection.HasOne<AssistantMcpServer>()
                .WithMany()
                .HasForeignKey(item => item.ServerId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AssistantMcpConnectionNotice>(notice =>
        {
            notice.ToTable("AssistantMcpConnectionNotice");
            notice.HasKey(item => new { item.ServerId, item.PersonActorId });
            notice.Property(item => item.ServerId).HasMaxLength(40);
            notice.Property(item => item.PersonActorId).HasMaxLength(256);
            notice.Property(item => item.Code).HasMaxLength(64).IsRequired();
            notice.HasIndex(item => item.NotifiedAt);
            notice.HasOne<AssistantMcpServer>()
                .WithMany()
                .HasForeignKey(item => item.ServerId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AssistantMcpAuthorizationFlow>(flow =>
        {
            flow.ToTable("AssistantMcpAuthorizationFlow");
            flow.HasKey(item => item.Id);
            flow.Property(item => item.ServerId).HasMaxLength(40).IsRequired();
            flow.Property(item => item.OwnerKind).HasMaxLength(16).IsRequired();
            flow.Property(item => item.OwnerActorId).HasMaxLength(128).IsRequired();
            flow.Property(item => item.TenantKey).HasMaxLength(128).IsRequired();
            flow.Property(item => item.StartedBy).HasMaxLength(128).IsRequired();
            flow.Property(item => item.StateHash).HasMaxLength(64).IsRequired();
            flow.Property(item => item.ProtectedCodeVerifier).IsRequired();
            flow.Property(item => item.Issuer).HasMaxLength(2_048).IsRequired();
            flow.Property(item => item.Resource).HasMaxLength(2_048).IsRequired();
            flow.Property(item => item.ClientId).HasMaxLength(2_048).IsRequired();
            flow.Property(item => item.RedirectUri).HasMaxLength(2_048).IsRequired();
            flow.Property(item => item.RequestedScopes).HasMaxLength(1_024);
            flow.Property(item => item.ReturnUrl).HasMaxLength(2_048);
            flow.Property(item => item.CompletionCodeHash).HasMaxLength(64);
            flow.Property(item => item.Status).HasMaxLength(16).IsRequired();
            flow.Property(item => item.FailureCode).HasMaxLength(64);
            flow.Property(item => item.Version).IsConcurrencyToken();
            flow.HasIndex(item => item.StateHash).IsUnique();
            flow.HasIndex(item => new { item.StartedBy, item.Status });
            flow.HasIndex(item => item.ExpiresAt);
            flow.HasOne<AssistantMcpServer>()
                .WithMany()
                .HasForeignKey(item => item.ServerId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AssistantMcpClientRegistration>(registration =>
        {
            registration.ToTable("AssistantMcpClientRegistration");
            registration.HasKey(item => new { item.ServerId, item.IssuerHash });
            registration.Property(item => item.ServerId).HasMaxLength(40);
            registration.Property(item => item.IssuerHash).HasMaxLength(64);
            registration.Property(item => item.Issuer).HasMaxLength(2_048).IsRequired();
            registration.Property(item => item.Method).HasMaxLength(16).IsRequired();
            registration.Property(item => item.ClientId).HasMaxLength(2_048).IsRequired();
            registration.Property(item => item.TokenEndpointAuthMethod).HasMaxLength(32).IsRequired();
            registration.HasOne<AssistantMcpServer>()
                .WithMany()
                .HasForeignKey(item => item.ServerId)
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
