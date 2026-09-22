using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using NewHeap.Platform.Common.Attributes;

namespace NewHeap.Platform.AspNet.Proxy;

public sealed class NhProxyRedirectEditorModel
{
    [Filterable] public Guid Id { get; set; }
    [Range(0, long.MaxValue)] public long Revision { get; set; }
    public bool IsNew { get; set; }
    [Required, StringLength(200)] public string Name { get; set; } = "";
    [Required, StringLength(2048)] public string Path { get; set; } = "";
    public bool IsRegex { get; set; }
    [Required, StringLength(4096)] public string Target { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public int Priority { get; set; }
    public NhProxyRedirectStatus Status { get; set; } = NhProxyRedirectStatus.Found;
    public NhProxyRedirectQueryMode QueryMode { get; set; }
    [StringLength(2048)] public string? Hosts { get; set; }
    [StringLength(512)] public string? Methods { get; set; }
    [StringLength(4096)] public string? TestUrl { get; set; }
    [StringLength(32)] public string TestMethod { get; set; } = "GET";
    [BindNever] public string? TestResult { get; set; }

    internal NhProxyRedirectRule ToRule() => new()
    {
        Id = Id, Name = Name, Enabled = Enabled, Priority = Priority, Status = Status, QueryMode = QueryMode, Target = Target,
        Match = new NhProxyRedirectMatch
        {
            Path = Path, PathMode = IsRegex ? NhProxyRedirectPathMatchMode.Regex : NhProxyRedirectPathMatchMode.Exact,
            Hosts = Split(Hosts), Methods = Split(Methods)
        }
    };

    private static ImmutableArray<string> Split(string? value) => (value ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToImmutableArray();
}

public sealed record NhProxyRedirectListModel(NhProxyRedirectConfiguration Configuration, NhProxyEngineStatus Status, string? Search);
public sealed record NhProxyRedirectDeleteModel(NhProxyRedirectRule Rule, long Revision);

