using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AspNet.Common.DAL.Entities;

namespace NewHeap.Platform.AspNet.Common.Services;

/// <summary>
/// Used by platform modules hosted without NewHeap Identity: entity changes are not written to the
/// NewHeap database log, which lives in the identity model.
/// </summary>
internal sealed class NhNoOpDbLogService : INhDbLogService
{
    public Task LogAsync(
        string message,
        LogType type = LogType.Unknown,
        string? tag = null,
        string?[]? messageArguments = null,
        LogAction action = LogAction.Unknown,
        LogSource source = LogSource.Unknown,
        string? objectType = null,
        string? objectTypeFull = null,
        string? objectId = null,
        Guid? userId = null,
        (string name, Stream contentStream)[]? files = null,
        DateTimeOffset? overrideCreationDateTime = null,
        bool doSaveChanges = true,
        DbContext? dbContext = null,
        CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}
