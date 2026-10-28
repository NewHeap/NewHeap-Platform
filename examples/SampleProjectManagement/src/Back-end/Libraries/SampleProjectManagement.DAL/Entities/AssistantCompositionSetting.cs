using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SampleProjectManagement.DAL.Entities;

/// <summary>
/// SPM-371: the application's own setting whether the assistant's agent uses compositions and the
/// script tool, per environment and agent. The sample's switch of the assistant reads it at the start of
/// every turn, so a person switches the features on and off in their environment without a restart. Code
/// still sets the ceiling: what an agent's code or the application's ceiling for agents created in the
/// administration does not allow stays off, whatever is stored here.
/// </summary>
public class AssistantCompositionSetting : IdDbEntity
{
    /// <summary>The longest <see cref="Environment"/>, as the assistant's tenant ids are.</summary>
    public const int MaxEnvironmentLength = 256;

    /// <summary>The longest <see cref="AgentId"/>, as the assistant's agent ids are.</summary>
    public const int MaxAgentIdLength = 128;

    public AssistantCompositionSetting()
    {
        CreationDateTime = DateTimeOffset.UtcNow;
        LastModifiedDateTime = DateTimeOffset.UtcNow;
    }

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    public DateTimeOffset CreationDateTime { get; set; }

    public DateTimeOffset LastModifiedDateTime { get; set; }

    /// <summary>
    /// The environment the setting applies to: the assistant's tenant, or empty for an installation
    /// without tenants, such as the sample, which serves its divisions without them.
    /// </summary>
    [MaxLength(MaxEnvironmentLength)]
    public string Environment { get; set; } = string.Empty;

    /// <summary>The agent the setting applies to.</summary>
    [MaxLength(MaxAgentIdLength)]
    public string AgentId { get; set; } = string.Empty;

    /// <summary>The agent uses compositions in this environment.</summary>
    public bool Compositions { get; set; }

    /// <summary>The agent uses the script tool in this environment.</summary>
    public bool Scripts { get; set; }

    /// <summary>The person who changed the setting last.</summary>
    public Guid? UpdatedByUserId { get; set; }
}
