using System.Text.RegularExpressions;
using NewHeap.Platform.AI.Chat;

namespace SampleProjectManagement.Api.Composition;

/// <summary>
/// SPM-313: the departments an agent works for, more of the application's own data about its agents.
/// An agent works for several departments, so the entry declares every department code as a
/// searchable value with <c>IndexByEach</c>: the application finds the agents of a department by any of
/// its codes, whether the agent sets them in code or was given them at runtime. NewHeap stores the codes
/// in a table of their own, at most 50 per agent, and never interprets them.
/// </summary>
public static partial class SampleAgentDepartmentsComposition
{
    /// <summary>
    /// The entry's name.
    /// </summary>
    public const string Entry = "departments";

    /// <summary>
    /// The project planner's departments in code.
    /// </summary>
    public static readonly SampleAgentDepartments PlannerDepartments = new(["planning", "operations"]);

    /// <summary>
    /// Registers the departments entry: lowercase dash-case codes (<c>value.codes: unknown-department</c>
    /// otherwise), each a searchable value, and editable on agents in code. Register it once per host.
    /// </summary>
    public static NhAssistantBuilder AddSampleAgentDepartments(this NhAssistantBuilder assistant)
    {
        ArgumentNullException.ThrowIfNull(assistant);
        return assistant.AddAgentMetadata<SampleAgentDepartments>(Entry, entry => entry
            .MaxBytes(1_024)
            .Validate((value, errors) =>
            {
                if (value.Codes is null || value.Codes.Any(code => code is null || !DepartmentCode().IsMatch(code)))
                {
                    errors.Add("codes", "unknown-department");
                }
            })
            .IndexByEach(value => value.Codes)
            .Editable());
    }

    [GeneratedRegex("^[a-z][a-z0-9]*(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex DepartmentCode();
}

/// <summary>
/// The departments an agent works for, by their codes, such as <c>planning</c>.
/// </summary>
public sealed record SampleAgentDepartments(IReadOnlyList<string> Codes);
