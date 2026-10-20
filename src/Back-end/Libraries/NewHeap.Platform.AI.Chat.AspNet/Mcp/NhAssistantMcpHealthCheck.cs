using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.Chat.AspNet.Mcp;
using NewHeap.Platform.AI.Chat.Persistence;

namespace NewHeap.Platform.AI.Chat.AspNet;

/// <summary>Registration of the opt-in MCP health check.</summary>
public static class NhAssistantMcpHealthCheckExtensions
{
    /// <summary>
    /// Adds a health check that reports <paramref name="failureStatus"/> while an enabled MCP server's
    /// last connection failed or OAuth connections wait for someone to act: an organization or
    /// personal connection that needs a new authorization, or a server whose authorization server
    /// changed. The data holds counts and server ids only. It is opt-in, because an external server
    /// that is down is not a reason to take this host out of rotation.
    /// </summary>
    public static IHealthChecksBuilder AddNewHeapAssistantMcpHealthCheck(
        this IHealthChecksBuilder builder,
        string name = "nh-assistant-mcp",
        HealthStatus failureStatus = HealthStatus.Degraded,
        IEnumerable<string>? tags = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return builder.Add(new HealthCheckRegistration(
            name,
            provider => new NhAssistantMcpHealthCheck(
                provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<ILogger<NhAssistantMcpHealthCheck>>()),
            failureStatus,
            tags));
    }
}

internal sealed partial class NhAssistantMcpHealthCheck(
    IServiceScopeFactory scopeFactory,
    ILogger<NhAssistantMcpHealthCheck> logger) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var servers = await services.GetRequiredService<NhAssistantAdminStore>().ListMcpServersAsync(cancellationToken);
            var failing = servers
                .Where(item => item.Server.IsEnabled && item.Server.LastConnectCode is not null)
                .Select(item => item.Server.Id)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var unusable = await services.GetRequiredService<NhAssistantMcpOAuthStore>().CountUnusableConnectionsAsync(cancellationToken);
            var organization = unusable.Where(item => item.OwnerKind == NhAssistantMcpConnectionOwners.Organization).Sum(item => item.Count);
            var personal = unusable.Where(item => item.OwnerKind == NhAssistantMcpConnectionOwners.User).Sum(item => item.Count);
            var data = new Dictionary<string, object>
            {
                ["failingServers"] = failing,
                ["organizationConnectionsNeedingAction"] = organization,
                ["personalConnectionsNeedingAction"] = personal,
                ["issuerChanged"] = unusable.Where(item => item.Status == NhAssistantMcpConnectionStatuses.IssuerChanged).Sum(item => item.Count)
            };
            return failing.Length > 0 || organization > 0 || personal > 0
                ? new HealthCheckResult(context.Registration.FailureStatus, "MCP servers or connections need attention.", data: data)
                : HealthCheckResult.Healthy("MCP servers connect.", data);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            // The log holds the exception; the health response names nothing a message could carry.
            LogCheckFailed(logger, exception);
            return new HealthCheckResult(context.Registration.FailureStatus, "The MCP state could not be read.");
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The assistant MCP health check could not read the state of the MCP servers.")]
    private static partial void LogCheckFailed(ILogger logger, Exception exception);
}
