using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace NewHeap.Platform.AspNet.Proxy;

/// <summary>Host-owned settings. Rule documents must never contain these credentials.</summary>
public sealed class NhProxyOptions
{
    public const string ConfigurationSectionName = "NewHeapProxy";
    public const string AdministrationPath = "/newheap-proxy";
    public const string AuthenticationScheme = "NewHeapProxy";
    public const string AdministrationPolicy = "NewHeapProxy.Administrator";

    public NhProxyAdministratorOptions Administrator { get; set; } = new();
    public NhProxyIpAllowlistOptions IpAllowlist { get; set; } = new();
    public NhProxyLoginAuditOptions LoginAudit { get; set; } = new();
    public NhProxyLimits Limits { get; set; } = new();

    /// <summary>
    /// Optional host-code callback for the native YARP builder during service registration,
    /// after NewHeap's base setup. Not bound from configuration, persisted, or run on rule reload.
    /// AddNewHeapProxy invokes this callback once per registration.
    /// </summary>
    [JsonIgnore]
    public Action<IReverseProxyBuilder>? YarpConfiguration { get; private set; }

    /// <summary>Sets the host callback for YARP registration, replacing any previously configured callback.</summary>
    public void ConfigureYarp(Action<IReverseProxyBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        YarpConfiguration = configure;
    }

    /// <summary>Empty permits configured HTTP(S) destinations; entries restrict their hosts.</summary>
    public string[] AllowedDestinationHosts { get; set; } = [];
}

public sealed class NhProxyAdministratorOptions
{
    public string UserName { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>Change when rotating credentials to invalidate existing sessions.</summary>
    public string CredentialVersion { get; set; } = string.Empty;

    public TimeSpan SessionDuration { get; set; } = TimeSpan.FromHours(8);
    public int LoginAttemptLimit { get; set; } = 5;
    public TimeSpan LoginAttemptWindow { get; set; } = TimeSpan.FromMinutes(1);
}

public sealed class NhProxyIpAllowlistOptions
{
    public bool Enabled { get; set; }

    /// <summary>IPv4/IPv6 addresses or CIDRs. Enabled with no entries denies all access.</summary>
    public string[] Entries { get; set; } = [];
}

public sealed class NhProxyLoginAuditOptions
{
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(90);
    public int CleanupBatchSize { get; set; } = 1000;
    public int MaximumPageSize { get; set; } = 100;
}

public sealed class NhProxyLimits
{
    public int MaximumRulesPerEngine { get; set; } = 1000;
    public int MaximumTestRequestBytes { get; set; } = 65536;
    public TimeSpan TestTimeout { get; set; } = TimeSpan.FromSeconds(5);
}
