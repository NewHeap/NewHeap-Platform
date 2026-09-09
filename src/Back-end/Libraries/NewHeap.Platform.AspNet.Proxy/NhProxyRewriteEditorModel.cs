using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using NewHeap.Platform.Common.Attributes;

namespace NewHeap.Platform.AspNet.Proxy;

public sealed class NhProxyRewriteEditorModel
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };

    [Filterable] public Guid Id { get; set; }
    public bool IsNew { get; set; }
    [Range(0, long.MaxValue)] public long Revision { get; set; }
    [Range(0, long.MaxValue)] public long RedirectRevision { get; set; }
    [Required, StringLength(200)] public string Name { get; set; } = "";
    [StringLength(2048)] public string? Path { get; set; } = "/{**rest}";
    [StringLength(2048)] public string? Hosts { get; set; }
    [StringLength(512)] public string? Methods { get; set; }
    public int Priority { get; set; }
    public bool Enabled { get; set; } = true;
    public Guid ClusterId { get; set; }
    public Guid? SelectedClusterId { get; set; }
    [Required, StringLength(200)] public string ClusterName { get; set; } = "Backend";
    [Required, StringLength(4096)] public string DestinationAddress { get; set; } = "";
    public NhProxyPathTransformKind? PathOperation { get; set; }
    [StringLength(2048)] public string? PathValue { get; set; }
    [Required, StringLength(32768)] public string AdvancedRuleJson { get; set; } = "{}";
    [Required, StringLength(8192)] public string AdvancedClusterJson { get; set; } = "{}";
    [StringLength(4096)] public string? TestUrl { get; set; }
    [Required, StringLength(32)] public string TestMethod { get; set; } = "GET";
    [Required, StringLength(8192)] public string TestHeadersJson { get; set; } = "{}";
    public bool SimulateEnabled { get; set; }
    [BindNever] public NhProxyRuleTestResult? TestResult { get; set; }
    [BindNever] public ImmutableArray<NhProxyCluster> AvailableClusters { get; set; } = [];

    internal NhProxyRewriteRule ToRule()
    {
        var advanced = JsonSerializer.Deserialize<NhProxyRewriteRule>(AdvancedRuleJson, JsonOptions)
            ?? throw new JsonException("A rule object is required.");
        if (advanced.Match is null || advanced.Transforms.IsDefault)
        {
            throw new JsonException("Match and transforms are required.");
        }
        var transforms = advanced.Transforms;
        if (PathOperation is { } operation)
        {
            transforms = transforms.Insert(0, new NhProxyPathTransform(operation, PathValue ?? ""));
        }

        return advanced with
        {
            Id = Id, Name = Name, Enabled = Enabled, Priority = Priority, ClusterId = ClusterId,
            Match = advanced.Match with { Path = Path, Hosts = Split(Hosts), Methods = Split(Methods) }, Transforms = transforms
        };
    }

    internal NhProxyCluster ToCluster()
    {
        var advanced = JsonSerializer.Deserialize<NhProxyCluster>(AdvancedClusterJson, JsonOptions)
            ?? throw new JsonException("A cluster object is required.");
        if (advanced.Destination is null)
        {
            throw new JsonException("A destination is required.");
        }
        return advanced with
        {
            Id = ClusterId, Name = ClusterName,
            Destination = new(advanced.Destination.Name, new Uri(DestinationAddress, UriKind.Absolute))
        };
    }

    internal static NhProxyRewriteEditorModel FromRule(NhProxyRewriteRule rule, NhProxyCluster cluster, long revision, long redirectRevision)
    {
        var first = rule.Transforms.FirstOrDefault() as NhProxyPathTransform;
        return new()
        {
            Id = rule.Id, Revision = revision, RedirectRevision = redirectRevision, Name = rule.Name,
            Path = rule.Match.Path, Hosts = string.Join(", ", rule.Match.Hosts), Methods = string.Join(", ", rule.Match.Methods),
            Priority = rule.Priority, Enabled = rule.Enabled, ClusterId = cluster.Id, SelectedClusterId = cluster.Id, ClusterName = cluster.Name,
            DestinationAddress = cluster.Destination.Address.AbsoluteUri, PathOperation = first?.Operation, PathValue = first?.Value,
            AdvancedRuleJson = JsonSerializer.Serialize(first is null ? rule : rule with { Transforms = rule.Transforms.RemoveAt(0) }, JsonOptions),
            AdvancedClusterJson = JsonSerializer.Serialize(cluster, JsonOptions)
        };
    }

    private static ImmutableArray<string> Split(string? value) => (value ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToImmutableArray();
}

public sealed record NhProxyRewriteListModel(NhProxyRewriteConfiguration Configuration, NhProxyEngineStatus Status, string? Search);
public sealed record NhProxyRewriteDeleteModel(NhProxyRewriteRule Rule, long Revision);
