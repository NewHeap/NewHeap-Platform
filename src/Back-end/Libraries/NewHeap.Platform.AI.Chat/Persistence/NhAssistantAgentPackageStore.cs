using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AI.Chat.Entities;

namespace NewHeap.Platform.AI.Chat.Persistence;

/// <summary>
/// Persistence of agent packages (PK11): the packages this host imported and the link of every part
/// to its package. An import writes through <see cref="ImportAsync"/>, one transaction over every
/// store with one change of the run configuration stamp. Every other call uses its own short-lived
/// context.
/// </summary>
internal sealed class NhAssistantAgentPackageStore(NhAssistantDbContextFactory contextFactory)
{
    public async Task<AssistantAgentPackage?> FindPackageAsync(string packageId, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.AgentPackages
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == packageId, cancellationToken);
    }

    /// <summary>
    /// The package the agent belongs to: the one whose agents name it, otherwise the one its links name;
    /// <see langword="null"/> for an agent that belongs to none.
    /// </summary>
    public async Task<AssistantAgentPackage?> FindPackageOfAgentAsync(string agentId, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var quoted = JsonSerializer.Serialize(agentId);
        var listed = await context.AgentPackages
            .AsNoTracking()
            .Where(item => item.AgentIdsJson.Contains(quoted))
            .OrderBy(item => item.Id)
            .ToListAsync(cancellationToken);
        // The text match finds candidates; the array decides.
        var package = listed.FirstOrDefault(item => AgentIdsOf(item).Contains(agentId, StringComparer.Ordinal));
        if (package is not null)
        {
            return package;
        }

        var linkedTo = await context.AgentPackageLinks
            .AsNoTracking()
            .Where(item => item.AgentId == agentId)
            .Select(item => item.PackageId)
            .FirstOrDefaultAsync(cancellationToken);
        return linkedTo is null
            ? null
            : await context.AgentPackages.AsNoTracking().SingleOrDefaultAsync(item => item.Id == linkedTo, cancellationToken);
    }

    public async Task<IReadOnlyList<AssistantAgentPackageLink>> ListLinksAsync(string agentId, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.AgentPackageLinks
            .AsNoTracking()
            .Where(item => item.AgentId == agentId)
            .OrderBy(item => item.PartKind)
            .ThenBy(item => item.PartKey)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// The links of several agents in one read.
    /// </summary>
    public async Task<IReadOnlyList<AssistantAgentPackageLink>> ListLinksAsync(
        IReadOnlyCollection<string> agentIds,
        CancellationToken cancellationToken)
    {
        if (agentIds.Count == 0)
        {
            return [];
        }

        var ids = agentIds.Distinct(StringComparer.Ordinal).ToArray();
        await using var context = contextFactory.CreateDbContext();
        return await context.AgentPackageLinks
            .AsNoTracking()
            .Where(item => ids.Contains(item.AgentId))
            .OrderBy(item => item.AgentId)
            .ThenBy(item => item.PartKind)
            .ThenBy(item => item.PartKey)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Every link that names the package, of every agent, by agent id; the column is indexed.
    /// </summary>
    public async Task<IReadOnlyList<AssistantAgentPackageLink>> ListPackageLinksAsync(
        string packageId,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var links = await context.AgentPackageLinks
            .AsNoTracking()
            .Where(item => item.PackageId == packageId)
            .ToListAsync(cancellationToken);

        // Ordinal, so both providers list the same order whatever their collation.
        return links
            .OrderBy(item => item.AgentId, StringComparer.Ordinal)
            .ThenBy(item => item.PartKind, StringComparer.Ordinal)
            .ThenBy(item => item.PartKey, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<AssistantAgentPackageLink?> FindLinkAsync(
        string agentId,
        string partKind,
        string partKey,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.AgentPackageLinks
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.AgentId == agentId && item.PartKind == partKind && item.PartKey == partKey,
                cancellationToken);
    }

    /// <summary>
    /// Whether the agent belongs to a package or has a linked part.
    /// </summary>
    public async Task<bool> IsLinkedAsync(string agentId, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        if (await context.AgentPackageLinks.AnyAsync(item => item.AgentId == agentId, cancellationToken))
        {
            return true;
        }

        return await FindPackageOfAgentAsync(agentId, cancellationToken) is not null;
    }

    /// <summary>
    /// Unlinks one part of an agent, or every part and the agent itself from its package's agents when
    /// <paramref name="partKind"/> is <see langword="null"/>. Returns the links it removed and the
    /// package, or <see cref="NhAssistantStoreOutcome.NotFound"/> when there was nothing to unlink.
    /// </summary>
    public async Task<NhAssistantStoreResult<(string PackageId, int PackageVersion, IReadOnlyList<AssistantAgentPackageLink> Links)>> UnlinkAsync(
        string agentId,
        string? partKind,
        string? partKey,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var links = await context.AgentPackageLinks
            .Where(item => item.AgentId == agentId)
            .ToListAsync(cancellationToken);
        var removed = partKind is null
            ? links
            : links.Where(item => item.PartKind == partKind && item.PartKey == (partKey ?? string.Empty)).ToList();
        AssistantAgentPackage? package = null;
        if (partKind is null)
        {
            var quoted = JsonSerializer.Serialize(agentId);
            package = (await context.AgentPackages
                    .Where(item => item.AgentIdsJson.Contains(quoted))
                    .ToListAsync(cancellationToken))
                .FirstOrDefault(item => AgentIdsOf(item).Contains(agentId, StringComparer.Ordinal));
        }

        if (removed.Count == 0 && package is null)
        {
            return NhAssistantStoreResult<(string, int, IReadOnlyList<AssistantAgentPackageLink>)>.Failed(NhAssistantStoreOutcome.NotFound);
        }

        var packageId = package?.Id ?? removed[0].PackageId;
        var packageVersion = package?.Version ?? removed[0].PackageVersion;
        context.AgentPackageLinks.RemoveRange(removed);
        if (package is not null)
        {
            package.AgentIdsJson = WriteAgentIds(AgentIdsOf(package).Where(id => !string.Equals(id, agentId, StringComparison.Ordinal)));
            package.ConcurrencyStamp = Guid.NewGuid();
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return NhAssistantStoreResult<(string, int, IReadOnlyList<AssistantAgentPackageLink>)>.Failed(NhAssistantStoreOutcome.Conflict);
        }

        await transaction.CommitAsync(cancellationToken);
        return NhAssistantStoreResult<(string, int, IReadOnlyList<AssistantAgentPackageLink>)>.Ok((packageId, packageVersion, removed));
    }

    /// <summary>
    /// Runs every write of an import in one transaction of one context: <paramref name="writes"/>
    /// writes the agents and their parts through the stores' transactional paths and returns
    /// <see langword="false"/> to roll everything back, after which the links and the package are
    /// replaced and the run configuration stamp changes once. Nothing is committed unless all of it
    /// succeeds.
    /// </summary>
    public async Task<NhAssistantStoreOutcome> ImportAsync(
        Func<NhAssistantDbContext, CancellationToken, Task<bool>> writes,
        AssistantAgentPackage package,
        IReadOnlyCollection<string> agentIds,
        IReadOnlyList<AssistantAgentPackageLink> links,
        IReadOnlyCollection<(string AgentId, string PartKind, string PartKey)> unlinked,
        int? expectedVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writes);
        await using var context = contextFactory.CreateDbContext();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var existing = await context.AgentPackages.SingleOrDefaultAsync(item => item.Id == package.Id, cancellationToken);
        if (existing?.Version != expectedVersion)
        {
            return NhAssistantStoreOutcome.Conflict;
        }

        try
        {
            if (!await writes(context, cancellationToken))
            {
                return NhAssistantStoreOutcome.Conflict;
            }

            // The links of the package's agents are replaced; parts left behind lose theirs.
            var ids = agentIds.ToArray();
            var current = await context.AgentPackageLinks
                .Where(item => ids.Contains(item.AgentId) || item.PackageId == package.Id)
                .ToListAsync(cancellationToken);
            foreach (var link in current)
            {
                var replaced = links.Any(item => item.AgentId == link.AgentId && item.PartKind == link.PartKind && item.PartKey == link.PartKey);
                var dropped = unlinked.Contains((link.AgentId, link.PartKind, link.PartKey));
                if (replaced || dropped)
                {
                    context.AgentPackageLinks.Remove(link);
                }
            }

            await context.SaveChangesAsync(cancellationToken);
            context.AgentPackageLinks.AddRange(links);
            package.AgentIdsJson = WriteAgentIds(agentIds);
            package.ConcurrencyStamp = Guid.NewGuid();
            if (existing is null)
            {
                context.AgentPackages.Add(package);
            }
            else
            {
                existing.Version = package.Version;
                existing.FormatVersion = package.FormatVersion;
                existing.Hash = package.Hash;
                existing.AgentIdsJson = package.AgentIdsJson;
                existing.ImportedAt = package.ImportedAt;
                existing.ImportedBy = package.ImportedBy;
                existing.ConcurrencyStamp = package.ConcurrencyStamp;
            }

            await context.SaveChangesAsync(cancellationToken);
            if (!await NhAssistantRunConfigurationStore.ChangeStampAsync(context, cancellationToken))
            {
                return NhAssistantStoreOutcome.Conflict;
            }
        }
        catch (DbUpdateException)
        {
            // A concurrent change took a version or an id first; nothing of the package is kept.
            return NhAssistantStoreOutcome.Conflict;
        }

        await transaction.CommitAsync(cancellationToken);
        return NhAssistantStoreOutcome.Succeeded;
    }

    /// <summary>
    /// The first writes of a promotion of a package-linked shadow variant as its package's version
    /// (FB11), in the transaction of <see cref="ImportAsync"/>: deletes the variant at
    /// <paramref name="variantVersion"/> with everything that belongs to it, its links included, and
    /// takes the live agent out of every other package's agents. Returns <see langword="false"/> when
    /// the variant changed or is gone, which rolls everything back.
    /// </summary>
    public static async Task<bool> PromoteAsync(
        NhAssistantDbContext context,
        string variantId,
        int variantVersion,
        string liveAgentId,
        string packageId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var variant = await context.Agents.SingleOrDefaultAsync(item => item.Id == variantId, cancellationToken);
        if (variant is null || variant.Version != variantVersion)
        {
            return false;
        }

        context.Agents.Remove(variant);
        await context.SaveChangesAsync(cancellationToken);

        // The text match finds candidates; the array decides.
        var quoted = JsonSerializer.Serialize(liveAgentId);
        var others = (await context.AgentPackages
                .Where(item => item.Id != packageId && item.AgentIdsJson.Contains(quoted))
                .ToListAsync(cancellationToken))
            .Where(item => AgentIdsOf(item).Contains(liveAgentId, StringComparer.Ordinal))
            .ToArray();
        foreach (var other in others)
        {
            other.AgentIdsJson = WriteAgentIds(AgentIdsOf(other).Where(id => !string.Equals(id, liveAgentId, StringComparison.Ordinal)));
            other.ConcurrencyStamp = Guid.NewGuid();
        }

        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// The agent ids a package row holds, ordered by id; empty for a value that does not read.
    /// </summary>
    public static IReadOnlyList<string> AgentIdsOf(AssistantAgentPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        try
        {
            return JsonSerializer.Deserialize<string[]>(package.AgentIdsJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// The locked fields a link holds; empty without locks.
    /// </summary>
    public static IReadOnlyList<string> LockedFieldsOf(AssistantAgentPackageLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        if (link.LockedFieldsJson is null)
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(link.LockedFieldsJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// The package's canonical value of every locked field a link holds, by field.
    /// </summary>
    public static IReadOnlyDictionary<string, string> LockedValuesOf(AssistantAgentPackageLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        if (link.LockedValuesJson is null)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(link.LockedValuesJson)
                   ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static string WriteAgentIds(IEnumerable<string> agentIds)
    {
        return JsonSerializer.Serialize(agentIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
    }
}
