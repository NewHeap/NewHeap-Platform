using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AspNet.Proxy;

/// <summary>Validates the configured administrator and audits attempts before issuing a session.</summary>
public sealed class NhProxyAdministrationService : INhProxyAdministrationService, IDisposable
{
    private readonly NhProxyOptions _options;
    private readonly INhProxyLoginAuditStore _audit;
    private readonly FixedWindowRateLimiter _limiter;
    private readonly System.Net.IPNetwork[] _networks;
    private readonly PasswordHasher<string> _hasher = new();

    public NhProxyAdministrationService(IOptions<NhProxyOptions> options, INhProxyLoginAuditStore audit)
    {
        _options = options.Value;
        _audit = audit;
        var administrator = _options.Administrator;
        if (!string.IsNullOrEmpty(administrator.Password))
        {
            if (!string.IsNullOrEmpty(administrator.PasswordHash))
            {
                throw new ArgumentException("Configure either Administrator.Password or Administrator.PasswordHash, not both.", nameof(options));
            }

            if (string.IsNullOrWhiteSpace(administrator.Password) || administrator.Password.Length > 1024)
            {
                throw new ArgumentException("Administrator.Password must contain a non-whitespace character and be at most 1024 characters.", nameof(options));
            }

            administrator.PasswordHash = _hasher.HashPassword(administrator.UserName, administrator.Password);
            administrator.Password = string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(administrator.PasswordHash))
        {
            try
            {
                _hasher.VerifyHashedPassword(administrator.UserName, administrator.PasswordHash, "configuration-validation");
            }
            catch (FormatException exception)
            {
                throw new ArgumentException("The administrator password hash must use the ASP.NET Identity format.", nameof(options), exception);
            }
        }

        if (administrator.SessionDuration <= TimeSpan.Zero || administrator.LoginAttemptLimit <= 0
            || administrator.LoginAttemptWindow <= TimeSpan.Zero || _options.LoginAudit.Retention <= TimeSpan.Zero
            || _options.LoginAudit.CleanupBatchSize <= 0 || _options.LoginAudit.MaximumPageSize <= 0)
        {
            throw new ArgumentException("Proxy session, rate limit and audit limits must be positive.");
        }

        _networks = _options.IpAllowlist.Entries.Select(entry =>
        {
            if (IPAddress.TryParse(entry, out var address))
            {
                address = Normalize(address);
                return new System.Net.IPNetwork(address, address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128);
            }

            return System.Net.IPNetwork.Parse(entry);
        }).ToArray();
        // ponytail: a single account uses one bounded, process-wide attempt budget; use partitioned limits if multiple accounts are introduced.
        _limiter = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        {
            PermitLimit = administrator.LoginAttemptLimit, Window = administrator.LoginAttemptWindow,
            QueueLimit = 0, AutoReplenishment = true
        });
    }

    internal static string CredentialStamp(NhProxyAdministratorOptions options) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{options.UserName}\0{options.PasswordHash}\0{options.CredentialVersion}")));

    internal static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    public Task<TaskResult> CheckIpAccessAsync(HttpContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var address = context.Connection.RemoteIpAddress;
        var allowed = !_options.IpAllowlist.Enabled || (address is not null && _networks.Any(network => network.Contains(Normalize(address))));
        return Task.FromResult(allowed ? TaskResult.Succeeded() : TaskResult.Failed(NhProxyErrorCodes.AccessDenied, NhProxyErrorCodes.AccessDenied));
    }

    public async Task<TaskResult> SignInAsync(HttpContext context, NhProxyLoginRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var access = await CheckIpAccessAsync(context, cancellationToken);
        using var lease = _limiter.AttemptAcquire();
        var administrator = _options.Administrator;
        var outcome = !access.Success ? NhProxyLoginOutcome.IpDenied
            : !lease.IsAcquired ? NhProxyLoginOutcome.Throttled : NhProxyLoginOutcome.InvalidCredentials;
        if (access.Success && lease.IsAcquired && !string.IsNullOrWhiteSpace(administrator.UserName)
            && !string.IsNullOrWhiteSpace(administrator.PasswordHash) && request.Password.Length <= 1024)
        {
            var verified = _hasher.VerifyHashedPassword(administrator.UserName, administrator.PasswordHash, request.Password);
            if (verified != PasswordVerificationResult.Failed && string.Equals(request.UserName, administrator.UserName, StringComparison.Ordinal))
            {
                outcome = NhProxyLoginOutcome.Success;
            }
        }

        await _audit.AppendAsync(new NhProxyLoginAuditEvent
        {
            Id = Guid.NewGuid(), OccurredAtUtc = DateTimeOffset.UtcNow, Outcome = outcome,
            ClientIp = context.Connection.RemoteIpAddress is { } ip ? Normalize(ip).ToString() : null,
            CorrelationId = context.TraceIdentifier,
            AuthenticatedUserName = outcome == NhProxyLoginOutcome.Success ? administrator.UserName : null
        }, cancellationToken);
        await _audit.DeleteExpiredAsync(DateTimeOffset.UtcNow - _options.LoginAudit.Retention, _options.LoginAudit.CleanupBatchSize, cancellationToken);
        if (outcome != NhProxyLoginOutcome.Success)
        {
            var code = outcome switch
            {
                NhProxyLoginOutcome.IpDenied => NhProxyErrorCodes.AccessDenied,
                NhProxyLoginOutcome.Throttled => NhProxyErrorCodes.Throttled,
                _ => NhProxyErrorCodes.InvalidCredentials
            };
            return TaskResult.Failed(code, code);
        }

        var identity = new ClaimsIdentity([
            new Claim(ClaimTypes.Name, administrator.UserName),
            new Claim("newheap-proxy.credential-stamp", CredentialStamp(administrator))
        ], NhProxyOptions.AuthenticationScheme);
        await context.SignInAsync(NhProxyOptions.AuthenticationScheme, new ClaimsPrincipal(identity), new AuthenticationProperties
        {
            IsPersistent = false, ExpiresUtc = DateTimeOffset.UtcNow + administrator.SessionDuration
        });
        return TaskResult.Succeeded();
    }

    public Task SignOutAsync(HttpContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return context.SignOutAsync(NhProxyOptions.AuthenticationScheme);
    }

    public void Dispose() => _limiter.Dispose();
}

// Adding the isolated panel scheme must not change ASP.NET's implicit default for the host application.
internal sealed class NhProxyAuthenticationSchemeProvider : AuthenticationSchemeProvider
{
    private readonly AuthenticationOptions _options;

    public NhProxyAuthenticationSchemeProvider(IOptions<AuthenticationOptions> options) : base(options)
    {
        _options = options.Value;
    }

    private async Task<AuthenticationScheme?> HostDefaultAsync(string? name)
    {
        if (name is not null)
        {
            return await GetSchemeAsync(name);
        }

        if (AppContext.TryGetSwitch("Microsoft.AspNetCore.Authentication.SuppressAutoDefaultScheme", out var suppress) && suppress)
        {
            return null;
        }

        var candidates = (await GetAllSchemesAsync()).Where(scheme => scheme.Name != NhProxyOptions.AuthenticationScheme).Take(2).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    public override Task<AuthenticationScheme?> GetDefaultAuthenticateSchemeAsync() => HostDefaultAsync(_options.DefaultAuthenticateScheme ?? _options.DefaultScheme);
    public override Task<AuthenticationScheme?> GetDefaultChallengeSchemeAsync() => HostDefaultAsync(_options.DefaultChallengeScheme ?? _options.DefaultScheme);
    public override Task<AuthenticationScheme?> GetDefaultForbidSchemeAsync() => HostDefaultAsync(_options.DefaultForbidScheme ?? _options.DefaultChallengeScheme ?? _options.DefaultScheme);
    public override Task<AuthenticationScheme?> GetDefaultSignInSchemeAsync() => HostDefaultAsync(_options.DefaultSignInScheme ?? _options.DefaultScheme);
    public override Task<AuthenticationScheme?> GetDefaultSignOutSchemeAsync() => HostDefaultAsync(_options.DefaultSignOutScheme ?? _options.DefaultSignInScheme ?? _options.DefaultScheme);
}
