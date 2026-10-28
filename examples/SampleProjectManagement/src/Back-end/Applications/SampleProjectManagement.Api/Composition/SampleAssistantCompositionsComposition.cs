using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AI;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Chat.AspNet;
using SampleProjectManagement.DAL;
using SampleProjectManagement.DAL.Entities;

namespace SampleProjectManagement.Api.Composition;

/// <summary>
/// SPM-371: the application owns the switch of compositions and the script tool. A setting of its own,
/// stored per environment and agent in <see cref="AssistantCompositionSetting"/> and changed on the
/// portal's assistant settings page, decides at the start of every turn, run step and MCP request whether
/// the assistant offers them; a change applies from the next turn without a restart. Code stays the
/// ceiling: the project assistant allows both in code, agents created in the administration may get the
/// script tool only (<c>UseCompositionsAsData</c>), and the MCP exposure allows the script tool and, for the
/// project overview (SPM-372), compositions. Call it after <c>AddSampleAssistant</c>.
/// </summary>
public static class SampleAssistantCompositionsComposition
{
    public static IServiceCollection AddSampleAssistantCompositions(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<SampleAssistantCompositionSettingsCache>();
        services.AddScoped<SampleAssistantCompositionSettings>();
        services.AddNewHeapAssistant(assistant => assistant
            // The switch reads the application's own table instead of NewHeap:AI:Assistant:Compositions
            // and :Scripts.
            .UseCompositionSwitch<SampleAssistantCompositionSwitch>()
            // The project assistant may use both where the setting has them on.
            .ConfigureAgentContext(SampleAssistantComposition.AgentId, context => context
                .AllowCompositions()
                .AllowScripts())
            // Agents created in the administration may be switched on for the script tool, never for
            // compositions; each such agent says so in its own definition.
            .UseCompositionsAsData(data => data.AllowScripts()));
        return services;
    }
}

/// <summary>
/// The application's switch: the stored setting of the environment (the assistant's tenant, empty for
/// the sample, which has none) and the agent, through a short cache. An MCP request, which no agent
/// makes, follows the setting of the MCP exposure, stored under <see cref="McpAgentId"/>.
/// </summary>
public sealed class SampleAssistantCompositionSwitch(SampleAssistantCompositionSettings settings) : INhAssistantCompositionSwitch
{
    /// <summary>The agent id under which the setting of the MCP exposure is stored.</summary>
    public const string McpAgentId = "mcp";

    public async ValueTask<bool> IsOnAsync(NhAssistantCompositionSwitchContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var setting = await settings.FindAsync(context.TenantId ?? string.Empty, context.AgentId ?? McpAgentId, cancellationToken);
        if (setting is null)
        {
            return false;
        }

        return context.Feature == NhAiCompositionFeature.Scripts ? setting.Scripts : setting.Compositions;
    }
}

/// <summary>
/// One environment's setting of one agent, as the switch reads it.
/// </summary>
public sealed record SampleAssistantCompositionSetting(string Environment, string AgentId, bool Compositions, bool Scripts);

/// <summary>
/// Reads and saves the application's settings of compositions and scripts. Every turn asks the switch,
/// so reads go through <see cref="SampleAssistantCompositionSettingsCache"/>; a save clears the cached
/// value, so the next turn on this host reads it at once.
/// </summary>
public sealed class SampleAssistantCompositionSettings(
    SampleProjectManagementDbContext database,
    SampleAssistantCompositionSettingsCache cache)
{
    /// <summary>The settings of one environment, ordered by agent.</summary>
    public async Task<IReadOnlyList<SampleAssistantCompositionSetting>> ListAsync(string environment, CancellationToken cancellationToken)
    {
        return await database.AssistantCompositionSettings
            .AsNoTracking()
            .Where(setting => setting.Environment == environment)
            .OrderBy(setting => setting.AgentId)
            .Select(setting => new SampleAssistantCompositionSetting(setting.Environment, setting.AgentId, setting.Compositions, setting.Scripts))
            .ToArrayAsync(cancellationToken);
    }

    /// <summary>The setting of one environment and agent, or <see langword="null"/> when none is stored.</summary>
    public async ValueTask<SampleAssistantCompositionSetting?> FindAsync(string environment, string agentId, CancellationToken cancellationToken)
    {
        if (cache.TryGet(environment, agentId, out var cached))
        {
            return cached;
        }

        var stored = await database.AssistantCompositionSettings
            .AsNoTracking()
            .Where(setting => setting.Environment == environment && setting.AgentId == agentId)
            .Select(setting => new SampleAssistantCompositionSetting(setting.Environment, setting.AgentId, setting.Compositions, setting.Scripts))
            .SingleOrDefaultAsync(cancellationToken);
        cache.Set(environment, agentId, stored);
        return stored;
    }

    /// <summary>Stores the setting of one environment and agent, creating it the first time.</summary>
    public async Task<SampleAssistantCompositionSetting> SaveAsync(
        string environment,
        string agentId,
        bool compositions,
        bool scripts,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var row = await database.AssistantCompositionSettings
            .SingleOrDefaultAsync(setting => setting.Environment == environment && setting.AgentId == agentId, cancellationToken);
        if (row is null)
        {
            row = new AssistantCompositionSetting { Environment = environment, AgentId = agentId };
            database.AssistantCompositionSettings.Add(row);
        }

        row.Compositions = compositions;
        row.Scripts = scripts;
        row.UpdatedByUserId = userId;
        row.LastModifiedDateTime = DateTimeOffset.UtcNow;
        await database.SaveChangesAsync(cancellationToken);
        cache.Remove(environment, agentId);
        return new SampleAssistantCompositionSetting(environment, agentId, compositions, scripts);
    }
}

/// <summary>
/// Keeps each read setting for <see cref="Lifetime"/>, so a turn's question costs no query; another host
/// sees a change once its cached value expired.
/// </summary>
public sealed class SampleAssistantCompositionSettingsCache(TimeProvider? clock = null)
{
    /// <summary>How long a read setting is kept.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<(string Environment, string AgentId), (SampleAssistantCompositionSetting? Setting, DateTimeOffset Expires)> _entries = new();

    public bool TryGet(string environment, string agentId, out SampleAssistantCompositionSetting? setting)
    {
        if (_entries.TryGetValue((environment, agentId), out var entry) && entry.Expires > _clock.GetUtcNow())
        {
            setting = entry.Setting;
            return true;
        }

        setting = null;
        return false;
    }

    public void Set(string environment, string agentId, SampleAssistantCompositionSetting? setting)
    {
        _entries[(environment, agentId)] = (setting, _clock.GetUtcNow().Add(Lifetime));
    }

    public void Remove(string environment, string agentId)
    {
        _entries.TryRemove((environment, agentId), out _);
    }
}
