using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.Tests.Infrastructure;

/// <summary>
/// A NewHeap MCP exposure (<c>WithNewHeapPlatformAITools</c>) over stateless Streamable HTTP at
/// <c>/mcp</c> on loopback that is a protected resource of an <see cref="NhAiTestAuthorizationServer"/>
/// (<c>UseProtectedResource</c>), and that keeps the host's own bearer sign-in. It exposes one read tool,
/// <see cref="ToolName"/>, that only the person <see cref="Planner"/> may see and call; the tool answers
/// with the caller's subject and whether a caller token was available to pass on.
/// </summary>
internal sealed class ProtectedNewHeapMcpServer : IAsyncDisposable
{
    public const string ToolName = "planning_read_v1";
    public const string Planner = "planner";
    public const string Visitor = "visitor";
    public const string Scope = "mcp:read";
    public const string HostScheme = "host";
    public const string HostIssuer = "https://host.example.test";
    public const string PassedOn = "passed-on";
    public const string NotPassedOn = "none";

    private const string HostSigningCredential = "test-host-signing-key-of-the-protected-mcp-exposure";
    private const string ReadPolicy = "planning.read";
    private const string ReadCapability = "planning-read";

    private readonly WebApplication _app;

    private ProtectedNewHeapMcpServer(WebApplication app)
    {
        _app = app;
        Origin = LocalhostTestServer.Origin(app);
    }

    /// <summary>The origin, for example <c>http://localhost:5123</c>.</summary>
    public string Origin { get; }

    /// <summary>The MCP endpoint, which is also the resource.</summary>
    public string Url => Origin + "/mcp";

    public string MetadataAddress => Origin + "/.well-known/oauth-protected-resource/mcp";

    public static async Task<ProtectedNewHeapMcpServer> StartAsync(NhAiTestAuthorizationServer authorization)
    {
        var app = await LocalhostTestServer.StartAsync(port =>
        {
            var origin = $"http://localhost:{port}";
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls(origin);
            builder.Logging.ClearProviders();
            var services = builder.Services;

            // The host's own sign-in, which its own clients keep using at the MCP endpoint.
            services.AddAuthentication(HostScheme)
                .AddJwtBearer(HostScheme, options =>
                {
                    options.MapInboundClaims = false;
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidIssuer = HostIssuer,
                        ValidAudience = HostIssuer,
                        IssuerSigningKey = HostKey,
                        ClockSkew = TimeSpan.Zero
                    };
                });
            services.AddAuthorization(options => options.AddPolicy(
                ReadPolicy,
                policy => policy.RequireClaim(JwtRegisteredClaimNames.Sub, Planner)));
            services.AddSingleton<INhAiToolCatalog, PlanningCatalog>();
            services.AddScoped<INhAiToolDiscoveryPolicy>(_ => new NhAiTestDiscoveryPolicy(
                (_, context) => context.CapabilityGrants.Contains(ReadCapability)));
            services.AddScoped<INhAiBudgetManager>(_ => new NhAiTestBudgetManager());
            services.AddNewHeapPlatformAIAspNet(ai => ai
                .UseToolInvocationPurpose("tool-invocation")
                .UseAuthenticatedClaimsWithoutTenant(authorization.Issuer)
                .UseAuthenticatedClaimsWithoutTenant(HostIssuer)
                .AddCapabilityGrant(ReadCapability, ReadPolicy));
            services.AddMcpServer()
                .WithHttpTransport()
                .WithNewHeapPlatformAITools(exposure => exposure.UseProtectedResource(resource =>
                {
                    resource.Resource = origin + "/mcp";
                    resource.AuthorizationServers.Add(authorization.Issuer);
                    resource.ScopesSupported.Add(Scope);
                    resource.ResourceName = "Planning";
                    resource.AcceptHostSchemes.Add(HostScheme);
                }));

            var server = builder.Build();
            server.MapMcp("/mcp").RequireAuthorization(NhAiMcpProtectedResourceDefaults.AuthorizationPolicy);
            return server;
        });
        return new ProtectedNewHeapMcpServer(app);
    }

    /// <summary>A token of the host's own sign-in, as its own clients send it.</summary>
    public static string CreateHostToken(string subject)
    {
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            HostIssuer,
            HostIssuer,
            [new Claim(JwtRegisteredClaimNames.Sub, subject)],
            DateTime.UtcNow,
            DateTime.UtcNow.AddMinutes(5),
            new SigningCredentials(HostKey, SecurityAlgorithms.HmacSha256)));
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private static SymmetricSecurityKey HostKey => new(Encoding.UTF8.GetBytes(HostSigningCredential));

    /// <summary>
    /// One attested catalog with one read tool exported to MCP: it needs the read policy and answers
    /// with the subject and whether the caller's token could be passed on.
    /// </summary>
    private sealed class PlanningCatalog : INhAiAttestedToolCatalog
    {
        private readonly NhAiToolDescriptor _descriptor = new NhAiToolDescriptor(
            "planning.read",
            1,
            "Read the planning of the person who asks.",
            typeof(string),
            typeof(string),
            NhAiToolEffect.ReadOnly,
            NhAiToolExposure.Mcp,
            true,
            [ReadPolicy])
        {
            ExportName = ToolName,
            CatalogId = "planning",
            ContractHash = Hash("planning.read-contract")
        };

        public NhAiToolCatalogGovernance Governance => NhAiToolCatalogGovernance.SharedInvoker;

        public IReadOnlyList<NhAiToolDescriptor> Descriptors => [_descriptor];

        public string AttestationHash => Hash(_descriptor.Id + "@1:" + _descriptor.ContractHash);

        public NhAiToolCatalogManifest Manifest => new(
            "planning",
            1,
            AttestationHash,
            [
                new NhAiToolManifestEntry(_descriptor.Id, _descriptor.Version, _descriptor.SchemaHash, _descriptor.ContractHash)
                {
                    ExportName = _descriptor.ExportName
                }
            ]);

        public IReadOnlyList<AIFunction> CreateFunctions(IServiceProvider services)
        {
            var invoker = services.GetRequiredService<INhAiToolInvoker>();
            var credentials = services.GetRequiredService<INhAiCallerCredentialAccessor>();
            var descriptor = _descriptor;
            Func<string, CancellationToken, Task<TaskResult<string>>> handler =
                (input, cancellationToken) => invoker.InvokeAsync(
                    descriptor,
                    input,
                    async (context, token) =>
                    {
                        var callerToken = await credentials.GetBearerTokenAsync(token);
                        return TaskResult<string>.Succeeded(
                            context.Subject + "|" + (callerToken is null ? NotPassedOn : PassedOn));
                    },
                    cancellationToken);
            var function = AIFunctionFactory.Create(handler, new AIFunctionFactoryOptions
            {
                Name = descriptor.ExportName,
                Description = descriptor.Description
            });
            return [NhAiGovernedAIFunction.Create(descriptor, function)];
        }

        private static string Hash(string value)
        {
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        }
    }
}
