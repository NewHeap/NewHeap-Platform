using NewHeap.Platform.Common.Attributes;

namespace SampleProjectManagement.Core.Models.View;

public sealed class ProjectShortViewModel
{
    [Filterable]
    public Guid Id { get; set; }

    [Searchable, Orderable]
    public string Key { get; set; } = "";

    [Searchable, Orderable]
    public string Name { get; set; } = "";
}

public sealed class ProjectProjectionViewModel
{
    [Filterable]
    public Guid Id { get; set; }

    [Searchable, Orderable]
    public string Key { get; set; } = "";

    [Searchable, Orderable]
    public string Name { get; set; } = "";

    [Searchable]
    public string DisplayName { get; set; } = "";

    [Filterable, Orderable]
    public int OpenTaskCount { get; set; }
}

public sealed class ProjectCompositeViewModel
{
    public ProjectViewModel Project { get; set; } = new();

    public List<ProjectTaskViewModel> Tasks { get; set; } = [];
}

public sealed class ProjectBulkMutationResultViewModel
{
    public int Created { get; set; }

    public int Updated { get; set; }

    public int Deleted { get; set; }

    public int Failed { get; set; }
}

public sealed class CollectionExpressionSampleViewModel
{
    public string InputKey { get; set; } = "";

    public string ResolvedPath { get; set; } = "";

    public string GeneratedExpression { get; set; } = "";

    public long MatchCount { get; set; }

    public bool IsSupported { get; set; } = true;

    public string? Limitation { get; set; }
}

public sealed class MatchOneAuthorizationSampleViewModel
{
    public bool Allowed { get; set; }

    public string? MatchedRule { get; set; }

    public List<string> RequiredOneOf { get; set; } = [];
}
