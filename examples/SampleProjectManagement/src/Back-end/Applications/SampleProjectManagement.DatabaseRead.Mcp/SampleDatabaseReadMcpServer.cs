using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Server;
using NewHeap.Platform.AI;
using NewHeap.Platform.AI.Mcp;
using NewHeap.Platform.Common.Models;

namespace SampleProjectManagement.DatabaseRead.Mcp;

public static class SampleDatabaseReadMcpServer
{
    public const string QueryToolName = "sample_database_query_v1";
    public const string SchemaToolName = "sample_database_schema_v1";
    public const string IndexesToolName = "sample_database_indexes_v1";

    public static IReadOnlySet<string> ToolNames { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        QueryToolName,
        SchemaToolName,
        IndexesToolName
    };

    public static async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        var context = SampleDatabaseReadMcpContext.FromArguments(arguments);
        var services = new ServiceCollection();
        services.AddSampleDatabaseReadMcp(context);
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var tools = await CreateToolsAsync(scope.ServiceProvider, context, cancellationToken);

        await using var server = McpServer.Create(
            new StreamServerTransport(Console.OpenStandardInput(), Console.OpenStandardOutput()),
            new McpServerOptions
            {
                ScopeRequests = false,
                ToolCollection = [.. tools]
            },
            serviceProvider: scope.ServiceProvider);
        await server.RunAsync(cancellationToken);
        return 0;
    }

    public static async ValueTask<IReadOnlyList<McpServerTool>> CreateToolsAsync(
        IServiceProvider services,
        SampleDatabaseReadMcpContext context,
        CancellationToken cancellationToken = default)
    {
        var tools = await services
            .GetRequiredService<INhAiMcpToolAdapter>()
            .CreateToolsAsync(services, context.InvocationContext, cancellationToken);
        if (tools.Count != ToolNames.Count
            || !ToolNames.SetEquals(tools.Select(tool => tool.ProtocolTool.Name)))
        {
            throw new InvalidOperationException(
                "The governed sample database MCP tool catalog is invalid.");
        }

        return tools;
    }
}

public static class SampleDatabaseReadMcpServiceCollectionExtensions
{
    public static IServiceCollection AddSampleDatabaseReadMcp(
        this IServiceCollection services,
        SampleDatabaseReadMcpContext context)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(context);
        services.AddSingleton(context);
        services.TryAddSingleton<ISampleDatabaseReadExecutor, NewHeapSampleDatabaseReadExecutor>();
        services.AddScoped<SampleDatabaseReadAiTools>();
        services.AddNewHeapPlatformAI(ai =>
        {
            ai.UseInvocationGate<SampleDatabaseReadMcpInvocationGate>();
            ai.UseDiscoveryPolicy<SampleDatabaseReadMcpDiscoveryPolicy>();
            ai.UseBudgetManager<SampleDatabaseReadMcpBudgetManager>(ServiceLifetime.Singleton);
            ai.AddAuditSink<SampleDatabaseReadMcpAuditSink>();
            ai.AddGeneratedToolCatalog<SampleDatabaseReadAiToolsNhAiCatalog>();
        });
        services.AddNewHeapPlatformAIMcp();
        return services;
    }
}

public sealed record SampleDatabaseReadMcpContext(
    string ProfileCatalogPath,
    string Profile,
    NhAiInvocationContext InvocationContext)
{
    public const string Capability = "sample-database-read";

    public static SampleDatabaseReadMcpContext FromArguments(IReadOnlyList<string> arguments)
    {
        var profileCatalogPath = RequiredArgument(arguments, "--profiles");
        if (!Path.IsPathFullyQualified(profileCatalogPath))
        {
            profileCatalogPath = Path.GetFullPath(profileCatalogPath);
        }

        var profile = RequiredArgument(arguments, "--profile");
        ValidateProfile(profile);
        var owner = Environment.GetEnvironmentVariable("SAMPLE_DATABASE_MCP_OWNER")?.Trim();
        if (string.IsNullOrWhiteSpace(owner))
        {
            owner = "sample-developer";
        }

        var runId = Environment.GetEnvironmentVariable("SAMPLE_DATABASE_MCP_RUN_ID")?.Trim();
        if (string.IsNullOrWhiteSpace(runId))
        {
            runId = Guid.NewGuid().ToString("N");
        }

        return Create(profileCatalogPath, profile, owner, runId);
    }

    public static SampleDatabaseReadMcpContext Create(
        string profileCatalogPath,
        string profile,
        string accountableOwnerId,
        string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileCatalogPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountableOwnerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ValidateProfile(profile);
        var fullCatalogPath = Path.GetFullPath(profileCatalogPath);
        var invocationContext = new NhAiInvocationContext(
            "sample-database-diagnostics",
            "sample-project-management",
            new Dictionary<string, string>
            {
                ["database-profile"] = profile
            })
        {
            ActorKind = NhAiActorKind.Agent,
            AccountableOwnerId = accountableOwnerId,
            RunId = runId,
            CorrelationId = runId,
            CapabilityGrants = new HashSet<string>(StringComparer.Ordinal) { Capability },
            Deadline = DateTimeOffset.UtcNow.AddMinutes(10),
            RemainingBudget = new NhAiModelBudget(
                0,
                0,
                SampleDatabaseReadLimits.ToolCallBudget,
                0m)
        };
        return new SampleDatabaseReadMcpContext(fullCatalogPath, profile, invocationContext);
    }

    private static string RequiredArgument(IReadOnlyList<string> arguments, string name)
    {
        for (var index = 0; index < arguments.Count - 1; index++)
        {
            if (string.Equals(arguments[index], name, StringComparison.Ordinal))
            {
                var value = arguments[index + 1].Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        throw new InvalidOperationException($"The required MCP argument '{name}' is missing.");
    }

    private static void ValidateProfile(string profile)
    {
        if (string.IsNullOrWhiteSpace(profile)
            || profile.Length > 64
            || profile[0] is not (>= 'a' and <= 'z' or >= '0' and <= '9')
            || profile[^1] is not (>= 'a' and <= 'z' or >= '0' and <= '9')
            || profile.Contains("--", StringComparison.Ordinal)
            || profile.Any(character =>
                character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-')))
        {
            throw new InvalidOperationException("The sample database profile is invalid.");
        }
    }
}

public sealed class SampleDatabaseReadMcpInvocationGate(
    SampleDatabaseReadMcpContext serverContext) : INhAiToolInvocationGate
{
    public ValueTask<TaskResult<NhAiInvocationContext>> AuthorizeAsync(
        NhAiToolDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var allowed = IsSampleDatabaseTool(descriptor.Id)
            && descriptor.Effect == NhAiToolEffect.ReadOnly
            && descriptor.Exposure.HasFlag(NhAiToolExposure.Mcp)
            && descriptor.AuthorizationPolicies.SequenceEqual(["sample.database-diagnostics.read"]);
        return ValueTask.FromResult(allowed
            ? TaskResult<NhAiInvocationContext>.Succeeded(serverContext.InvocationContext)
            : TaskResult<NhAiInvocationContext>.Failed(
                "sample-database-tool-denied",
                "The requested sample database MCP tool is not authorized."));
    }

    internal static bool IsSampleDatabaseTool(string id) =>
        id is "sample-database.query" or "sample-database.schema" or "sample-database.indexes";
}

public sealed class SampleDatabaseReadMcpDiscoveryPolicy : INhAiToolDiscoveryPolicy
{
    public ValueTask<bool> CanDiscoverAsync(
        NhAiToolDescriptor descriptor,
        NhAiInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            SampleDatabaseReadMcpInvocationGate.IsSampleDatabaseTool(descriptor.Id)
            && descriptor.Effect == NhAiToolEffect.ReadOnly
            && descriptor.Exposure.HasFlag(NhAiToolExposure.Mcp)
            && context.ActorKind == NhAiActorKind.Agent
            && !string.IsNullOrWhiteSpace(context.AccountableOwnerId)
            && context.CapabilityGrants.Contains(SampleDatabaseReadMcpContext.Capability));
    }
}

public sealed class SampleDatabaseReadMcpBudgetManager : INhAiBudgetManager
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, int> _calls = new();

    public ValueTask<TaskResult<NhAiBudgetReservation>> ReserveAsync(
        NhAiBudgetRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var count = _calls.AddOrUpdate(request.InvocationId, 1, (_, current) => current + 1);
        return ValueTask.FromResult(count <= SampleDatabaseReadLimits.ToolCallBudget
            ? TaskResult<NhAiBudgetReservation>.Succeeded(new NhAiBudgetReservation(
                $"{request.InvocationId:N}:{count}",
                new NhAiModelBudget(
                    0,
                    0,
                    SampleDatabaseReadLimits.ToolCallBudget - count,
                    0m),
                DateTimeOffset.UtcNow.AddMinutes(10)))
            : TaskResult<NhAiBudgetReservation>.Failed(
                "sample-database-budget-exhausted",
                "The sample database tool budget is exhausted."));
    }
}

public sealed class SampleDatabaseReadMcpAuditSink : INhAiAuditSink
{
    public ValueTask WriteAsync(
        NhAiAuditRecord record,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Console.Error.WriteLine(JsonSerializer.Serialize(new
        {
            eventName = "sample.database-tool",
            record.InvocationId,
            record.ToolId,
            record.ToolVersion,
            record.ActorId,
            record.RunId,
            outcome = record.Outcome.ToString(),
            record.Timestamp
        }));
        return ValueTask.CompletedTask;
    }
}
