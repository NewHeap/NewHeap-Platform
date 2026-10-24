using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using NewHeap.Platform.AI.Chat.Entities;

namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// The lockable fields of a stored part (<see cref="NhAssistantPackageLockFields"/>), each as the
/// canonical JSON of its value, so a save compares what it would store with what a package locks.
/// Instructions are named by their hash or their asset, never by their text.
/// </summary>
internal static class NhAssistantPackageFields
{
    /// <summary>
    /// The fields of an agent's definition; an agent in code has only the fields an administrator may
    /// override on it.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Of(AssistantAgent row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["displayName"] = Text(row.DisplayName),
            ["description"] = Text(row.Description),
            ["instructions"] = Instructions(row.InstructionsFromAsset ? row.InstructionsAssetId : null, row.InstructionsAssetVersion, row.InstructionsHash),
            ["toolSelectors"] = Array(row.ToolSelectorsJson),
            ["requiredPolicy"] = Text(row.RequiredPolicy),
            ["autonomy"] = Text(row.Autonomy.ToString().ToLowerInvariant())
        };
        if (row.Source != NhAssistantAgentSources.Admin)
        {
            return fields;
        }

        fields["offeredInChat"] = Flag(!row.HiddenFromChat);
        fields["worksOn"] = Object(new JsonObject
        {
            ["subjectTypes"] = Node(Array(row.WorksOnSubjectTypesJson)),
            ["allSubjects"] = row.WorksOnAllSubjects,
            ["application"] = row.WorksOnApplication,
            ["perDivision"] = row.PerDivision,
            ["runPolicy"] = row.RunPolicy,
            ["viewPolicy"] = row.ViewPolicy
        });
        fields["profile"] = Text(row.ProfileName);
        fields["dataClassification"] = Text(row.DataClassification);
        return fields;
    }

    /// <summary>
    /// The fields of the run settings of an agent created in the administration, without the
    /// technical account.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Of(AssistantAgentRunConfiguration row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["accountability"] = Text(row.Accountability),
            ["delegateFromInitiator"] = Flag(row.DelegateFromInitiator),
            ["allowGatewayReads"] = Flag(row.AllowGatewayReads),
            ["elevatedReads"] = Text(row.ElevatedReads),
            ["approvalPolicy"] = Text(row.ApprovalPolicyId),
            ["retryCount"] = Number(row.RetryCount),
            ["dailyBudget"] = Budget(row.DailyToolCalls, row.DailyTokens),
            ["maxConcurrentRuns"] = Number(row.MaxConcurrentRuns),
            ["maxRunsPerSubjectPerDay"] = Number(row.MaxRunsPerSubjectPerDay),
            ["maxTokensPerRun"] = Number(row.MaxTokensPerRun),
            ["triageProfile"] = Text(row.TriageProfileName),
            ["ruleHandler"] = Text(row.RuleHandlerName),
            ["files"] = Files(row.AcceptsFiles, row.MaxFileBytes, row.MaxFilePages, row.MaxFilesPerTurn, row.MaxFileTokensPerTurn)
        };
    }

    /// <summary>
    /// The fields of a version of a data skill.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Of(AssistantAgentSkillDefinition row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["description"] = Text(row.Description),
            ["instructions"] = Instructions(row.InstructionsAssetId, row.InstructionsAssetVersion ?? 0, row.InstructionsHash),
            ["tools"] = Array(row.ToolSelectorsJson),
            ["profile"] = Text(row.ProfileName),
            ["ruleHandler"] = Text(row.RuleHandlerName),
            ["maxTokensPerRun"] = Number(row.MaxTokensPerRun),
            ["dailyBudget"] = Budget(row.DailyToolCalls, row.DailyTokens),
            ["approvalPolicy"] = Text(row.ApprovalPolicyId),
            ["dataClassification"] = Text(row.DataClassification),
            ["files"] = Files(row.AcceptsFiles, row.MaxFileBytes, row.MaxFilePages, row.MaxFilesPerTurn, row.MaxFileTokensPerTurn)
        };
    }

    /// <summary>
    /// The fields of a version of a data trigger.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Of(AssistantAgentTriggerDefinition row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["skill"] = Text(row.SkillId),
            ["source"] = Text(row.Source),
            ["event"] = Text(row.EventName),
            ["filters"] = row.FiltersJson is null ? "[]" : Array(row.FiltersJson),
            ["cron"] = Text(row.Cron),
            ["timeZone"] = Text(row.TimeZone),
            ["subjectSet"] = Text(row.SubjectSetName),
            ["message"] = Text(row.Message),
            ["conflict"] = Text(row.Conflict),
            ["debounceSeconds"] = Number(row.DebounceSeconds)
        };
    }

    /// <summary>
    /// The fields of a consultation defined as data.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Of(AssistantAgentConsultationDefinition row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["description"] = Text(row.Description),
            ["instructions"] = Instructions(null, 0, row.InstructionsHash),
            ["minimumRole"] = Text(row.MinimumRole),
            ["askerPolicy"] = Text(row.AskerPolicy),
            ["routes"] = Array(row.RoutesJson),
            ["proposalRole"] = Text(row.ProposalRole),
            ["proposalPolicy"] = Text(row.ProposalPolicyId),
            ["toolSelectors"] = row.ToolSelectorsJson is null ? "null" : Array(row.ToolSelectorsJson),
            ["limits"] = Object(new JsonObject
            {
                ["maxToolCalls"] = row.MaxToolCalls,
                ["timeoutSeconds"] = row.TimeoutSeconds,
                ["maxAnswerChars"] = row.MaxAnswerChars,
                ["maxHistoryMessages"] = row.MaxHistoryMessages
            }),
            ["allowRequests"] = Flag(row.AllowRequests),
            ["exposeToMcp"] = Flag(row.ExposeToMcp),
            ["dataClassification"] = Text(row.DataClassification)
        };
    }

    /// <summary>
    /// The fields of a hand-off defined as data.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Of(AssistantAgentHandOffDefinition row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["sources"] = Array(row.SourcesJson),
            ["description"] = Text(row.Description),
            ["notifySource"] = Flag(row.NotifySource),
            ["maxPerSubjectPerDay"] = Number(row.MaxPerSubjectPerDay)
        };
    }

    /// <summary>
    /// The field of an agent's value of an entry of agent metadata: the value's canonical JSON, or
    /// <c>null</c> without a value.
    /// </summary>
    public static IReadOnlyDictionary<string, string> OfMetadata(string? valueJson)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["value"] = valueJson is null ? "null" : Canonical(valueJson)
        };
    }

    /// <summary>
    /// The canonical JSON of a JSON text: object properties ordered by name, no white space; the text
    /// itself when it does not parse.
    /// </summary>
    public static string Canonical(string json)
    {
        try
        {
            return Sorted(JsonNode.Parse(json))?.ToJsonString() ?? "null";
        }
        catch (JsonException)
        {
            return JsonSerializer.Serialize(json);
        }
    }

    private static JsonNode? Sorted(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                var sorted = new JsonObject();
                foreach (var (name, value) in obj.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray())
                {
                    sorted[name] = Sorted(value?.DeepClone());
                }

                return sorted;
            case JsonArray array:
                var copy = new JsonArray();
                foreach (var item in array)
                {
                    copy.Add(Sorted(item?.DeepClone()));
                }

                return copy;
            default:
                return node?.DeepClone();
        }
    }

    private static string Text(string? value)
    {
        return value is null ? "null" : JsonSerializer.Serialize(value);
    }

    private static string Flag(bool value)
    {
        return value ? "true" : "false";
    }

    private static string Number(long? value)
    {
        return value is { } number ? number.ToString(CultureInfo.InvariantCulture) : "null";
    }

    private static string Array(string? json)
    {
        return json is null ? "[]" : Canonical(json);
    }

    private static string Object(JsonObject value)
    {
        return Sorted(value)!.ToJsonString();
    }

    private static JsonNode? Node(string json)
    {
        return JsonNode.Parse(json);
    }

    private static string Budget(int? toolCalls, long? tokens)
    {
        return toolCalls is null && tokens is null
            ? "null"
            : Object(new JsonObject { ["toolCalls"] = toolCalls, ["tokens"] = tokens });
    }

    private static string Files(bool acceptsFiles, int? maxFileBytes, int? maxFilePages, int? maxFilesPerTurn, int? maxFileTokensPerTurn)
    {
        return Object(new JsonObject
        {
            ["acceptsFiles"] = acceptsFiles,
            ["maxFileBytes"] = maxFileBytes,
            ["maxFilePages"] = maxFilePages,
            ["maxFilesPerTurn"] = maxFilesPerTurn,
            ["maxFileTokensPerTurn"] = maxFileTokensPerTurn
        });
    }

    private static string Instructions(string? assetId, int assetVersion, string? hash)
    {
        return assetId is null
            ? Object(new JsonObject { ["hash"] = hash })
            : Object(new JsonObject { ["assetId"] = assetId, ["version"] = assetVersion, ["hash"] = hash });
    }
}
