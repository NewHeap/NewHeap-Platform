using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

public sealed class AssistantSubjectMemberSnapshotTests(AssistantDatabaseFixture database)
    : IClassFixture<AssistantDatabaseFixture>
{
    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Concurrent_refreshes_keep_one_whole_subject_member_snapshot(AssistantTestProvider provider)
    {
        await using var services = await database.CreateStorageAsync(provider);
        var contexts = services.GetRequiredService<NhAssistantDbContextFactory>();
        var store = new NhAssistantRunStore(contexts);
        var subject = new NhAiSubjectRef("project", Guid.NewGuid().ToString("N"));
        var snapshotAt = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        await store.ReplaceMembersAsync("north", subject,
            [new("owner", NhAiSubjectRole.Approver), new("old-reader", NhAiSubjectRole.Reader)],
            snapshotAt, CancellationToken.None);

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshes = Enumerable.Range(0, 8).Select(RefreshAsync).ToArray();
        start.SetResult();
        await Task.WhenAll(refreshes);

        var members = await store.GetMembersAsync("north", subject, CancellationToken.None);
        Assert.Equal(3, members.Count);
        Assert.Equal(NhAiSubjectRole.Approver, Assert.Single(members, member => member.ActorId == "owner").Role);
        var reader = Assert.Single(members, member => member.Role == NhAiSubjectRole.Reader);
        var suffix = reader.ActorId["reader-".Length..];
        Assert.Contains(members, member => member.ActorId == "approver-" + suffix && member.Role == NhAiSubjectRole.Approver);
        Assert.All(members, member => Assert.Equal(snapshotAt, member.SnapshotAt));

        async Task RefreshAsync(int index)
        {
            await start.Task;
            await store.ReplaceMembersAsync("north", subject,
                [
                    new("owner", NhAiSubjectRole.Approver),
                    new($"reader-{index}", NhAiSubjectRole.Reader),
                    new($"approver-{index}", NhAiSubjectRole.Approver)
                ],
                snapshotAt, CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_busy_member_snapshot_stays_unchanged_and_does_not_block_other_subjects_or_scopes(
        AssistantTestProvider provider)
    {
        await using var services = await database.CreateStorageAsync(provider);
        var contexts = services.GetRequiredService<NhAssistantDbContextFactory>();
        var store = new NhAssistantRunStore(contexts) { MemberLockTimeout = TimeSpan.Zero };
        var subject = new NhAiSubjectRef("project", Guid.NewGuid().ToString("N"));
        var snapshotAt = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        await store.ReplaceMembersAsync("north", subject, [new("original", NhAiSubjectRole.Reader)],
            snapshotAt, CancellationToken.None);

        await using (var holder = contexts.CreateDbContext())
        {
            await using var transaction = await holder.Database.BeginTransactionAsync();
            Assert.True(await contexts.TransactionLocks.TryAcquireAsync(
                holder, NhAssistantRunStore.MemberLockResource(holder.Model.GetDefaultSchema(), "north", subject),
                TimeSpan.Zero, CancellationToken.None));

            await Assert.ThrowsAsync<TimeoutException>(() => store.ReplaceMembersAsync(
                "north", subject, [new("replacement", NhAiSubjectRole.Approver)], snapshotAt, CancellationToken.None));
            Assert.Equal("original", Assert.Single(await store.GetMembersAsync("north", subject, CancellationToken.None)).ActorId);

            var anotherSubject = new NhAiSubjectRef("project", Guid.NewGuid().ToString("N"));
            var anotherType = new NhAiSubjectRef("task", subject.Id);
            await store.ReplaceMembersAsync("south", subject, [new("south-reader", NhAiSubjectRole.Reader)],
                snapshotAt, CancellationToken.None);
            await store.ReplaceMembersAsync("north", anotherSubject, [new("other-project-reader", NhAiSubjectRole.Reader)],
                snapshotAt, CancellationToken.None);
            await store.ReplaceMembersAsync("north", anotherType, [new("task-reader", NhAiSubjectRole.Reader)],
                snapshotAt, CancellationToken.None);

            Assert.Equal("south-reader", Assert.Single(await store.GetMembersAsync("south", subject, CancellationToken.None)).ActorId);
            Assert.Equal("other-project-reader", Assert.Single(await store.GetMembersAsync("north", anotherSubject, CancellationToken.None)).ActorId);
            Assert.Equal("task-reader", Assert.Single(await store.GetMembersAsync("north", anotherType, CancellationToken.None)).ActorId);
            await transaction.CommitAsync();
        }

        await store.ReplaceMembersAsync("north", subject, [new("replacement", NhAiSubjectRole.Approver)],
            snapshotAt, CancellationToken.None);
        Assert.Equal("replacement", Assert.Single(await store.GetMembersAsync("north", subject, CancellationToken.None)).ActorId);
    }
}
