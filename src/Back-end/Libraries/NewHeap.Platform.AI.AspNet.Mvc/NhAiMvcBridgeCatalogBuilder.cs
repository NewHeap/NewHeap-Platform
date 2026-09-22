using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace NewHeap.Platform.AI.AspNet.Mvc;

/// <summary>
/// The immutable result of reading the MVC actions: descriptors ordered by id and the action
/// each descriptor executes.
/// </summary>
internal sealed record NhAiMvcBridgeCatalogModel(
    IReadOnlyList<NhAiToolDescriptor> Descriptors,
    IReadOnlyDictionary<string, NhAiBridgeActionInfo> Actions,
    NhAiToolCatalogManifest Manifest,
    string AttestationHash,
    NhAiMvcBridgeGatewayModel? Gateway = null);

/// <summary>
/// Reads controller actions from ApiExplorer, applies the bridge filters and creates one
/// governed tool descriptor per published action.
/// </summary>
internal sealed class NhAiMvcBridgeCatalogBuilder(
    NhAiMvcBridgeOptions options,
    NhAiMvcBridgeRuntimeSettings settings,
    INhAiBridgeConventions conventions,
    NhAiBridgeXmlDocumentation xmlDocumentation)
{
    public const string OutputSchemaJson =
        "{\"type\":\"object\",\"properties\":{"
        + "\"status\":{\"type\":\"integer\"},"
        + "\"contentType\":{\"type\":[\"string\",\"null\"]},"
        + "\"body\":{},"
        + "\"truncated\":{\"type\":\"boolean\"},"
        + "\"bodyBytes\":{\"type\":\"integer\"},"
        + "\"bodyText\":{\"type\":\"string\"},"
        + "\"hint\":{\"type\":\"string\"}},"
        + "\"required\":[\"status\",\"truncated\",\"bodyBytes\"]}";

    private static readonly string[] SupportedMethods = ["GET", "POST", "PUT", "PATCH", "DELETE"];

    public NhAiMvcBridgeCatalogModel Build(IApiDescriptionGroupCollectionProvider apiDescriptions)
    {
        ArgumentNullException.ThrowIfNull(apiDescriptions);
        ValidateOptions();

        // NewHeap:AI:Bridge:Enabled=false keeps the registration but publishes no tools.
        var actions = settings.Enabled ? ReadActions(apiDescriptions) : [];
        var toolSetId = options.ToolSetId!;
        var descriptors = new List<NhAiToolDescriptor>(actions.Count);
        var actionsById = new Dictionary<string, NhAiBridgeActionInfo>(StringComparer.Ordinal);
        var exportNames = new Dictionary<string, NhAiBridgeActionInfo>(StringComparer.Ordinal);
        foreach (var action in actions)
        {
            var toolId = conventions.GetToolId(action);
            if (string.IsNullOrWhiteSpace(toolId)
                || !toolId.Split('.').All(NhAiMvcBridgeNames.IsSegment))
            {
                throw new InvalidOperationException(
                    $"API bridge conventions returned tool id '{toolId}' for '{Describe(action)}'; tool ids use dot-separated lowercase dash-case segments.");
            }

            var id = toolSetId + "." + toolId;
            if (actionsById.TryGetValue(id, out var existing))
            {
                throw new InvalidOperationException(
                    $"API bridge tool id '{id}' is produced by both '{Describe(existing)}' and '{Describe(action)}'. Exclude one action or rename it.");
            }

            var exportName = NhAiMvcBridgeNames.ToBoundedExportName(
                toolSetId + "_" + toolId.Replace('.', '_') + "_v"
                + options.ContractVersion.ToString(CultureInfo.InvariantCulture));
            if (exportNames.TryGetValue(exportName, out var exportConflict))
            {
                throw new InvalidOperationException(
                    $"API bridge export name '{exportName}' is produced by both '{Describe(exportConflict)}' and '{Describe(action)}'. Exclude one action or rename it.");
            }
            actionsById.Add(id, action);
            exportNames.Add(exportName, action);
            descriptors.Add(CreateDescriptor(action, id, exportName));
        }

        NhAiMvcBridgeGatewayModel? gateway = null;
        if (options.Gateway is not null)
        {
            var exposure = NhAiToolExposure.Local | NhAiToolExposure.Agent;
            if (options.McpExposureEnabled)
            {
                exposure |= NhAiToolExposure.Mcp;
            }
            gateway = NhAiMvcBridgeGatewayBuilderLogic.Build(options, descriptors, actionsById, conventions, exposure);
            foreach (var descriptor in gateway.Descriptors)
            {
                if (actionsById.ContainsKey(descriptor.Id) || exportNames.ContainsKey(descriptor.ExportName))
                {
                    throw new InvalidOperationException(
                        $"API bridge gateway tool '{descriptor.Id}' conflicts with a bridge tool. Use another gateway tool set id.");
                }
            }
            descriptors.AddRange(gateway.Descriptors);
        }

        var ordered = descriptors
            .OrderBy(descriptor => descriptor.Id, StringComparer.Ordinal)
            .ToArray();
        var attestationHash = ComputeHash(string.Join(
            "\n",
            ordered.Select(descriptor => descriptor.Id + "@"
                + descriptor.Version.ToString(CultureInfo.InvariantCulture) + ":" + descriptor.ContractHash)));
        var manifest = new NhAiToolCatalogManifest(
            toolSetId,
            options.ContractVersion,
            attestationHash,
            ordered
                .Select(descriptor => new NhAiToolManifestEntry(
                    descriptor.Id,
                    descriptor.Version,
                    descriptor.SchemaHash,
                    descriptor.ContractHash)
                {
                    ExportName = descriptor.ExportName
                })
                .ToArray());
        return new NhAiMvcBridgeCatalogModel(ordered, actionsById, manifest, attestationHash, gateway);
    }

    private void ValidateOptions()
    {
        if (options.IncludeDeleteActions)
        {
            throw new InvalidOperationException(
                "The API bridge does not support DELETE actions in v1: destructive tools require a verifier. Remove IncludeDeleteActions(true) and expose deletions through a curated tool.");
        }
        if (!NhAiMvcBridgeNames.IsSegment(options.ToolSetId))
        {
            throw new InvalidOperationException(
                "The API bridge requires UseToolSetId with a lowercase dash-case tool set id.");
        }
        if (settings.Enabled
            && (!Uri.TryCreate(settings.SelfBaseUrl, UriKind.Absolute, out var selfBaseUri)
                || (selfBaseUri.Scheme != Uri.UriSchemeHttp && selfBaseUri.Scheme != Uri.UriSchemeHttps)))
        {
            throw new InvalidOperationException(
                $"The API bridge requires UseSelfBaseUrl or '{NhAiMvcBridgeRuntimeSettings.SelfBaseUrlKey}' with an absolute http or https URL.");
        }
        if (options.IncludeControllerPatterns.Count == 0)
        {
            throw new InvalidOperationException(
                "The API bridge requires IncludeControllers with at least one controller pattern; use \"*\" to include every controller.");
        }

        var defaults = options.ToolDefaults;
        if (defaults.MaxResultBytes < 1
            || defaults.MaxInputBytes < 1
            || defaults.TimeoutSeconds < 1
            || defaults.MaxConcurrency < 1)
        {
            throw new InvalidOperationException(
                "The API bridge tool defaults must be greater than zero.");
        }
    }

    private List<NhAiBridgeActionInfo> ReadActions(IApiDescriptionGroupCollectionProvider apiDescriptions)
    {
        var candidates = new List<(ApiDescription Description, ControllerActionDescriptor Action, string Method, IReadOnlyList<string> Policies)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var description in apiDescriptions.ApiDescriptionGroups.Items.SelectMany(group => group.Items))
        {
            if (description.ActionDescriptor is not ControllerActionDescriptor action
                || description.HttpMethod is null)
            {
                continue;
            }

            var method = description.HttpMethod.ToUpperInvariant();
            if (!SupportedMethods.Contains(method, StringComparer.Ordinal)
                || !seen.Add(action.Id + " " + method)
                || !IsIncludedController(action.ControllerName)
                || IsExcludedAction(action))
            {
                continue;
            }

            var bridgeTool = action.MethodInfo.GetCustomAttribute<NhAiBridgeToolAttribute>(inherit: true);
            if (bridgeTool?.Exclude == true
                || string.Equals(method, "DELETE", StringComparison.Ordinal)
                || AllowsAnonymous(action)
                || (!options.IncludeFileUploads && HasFileUpload(description, action)))
            {
                continue;
            }

            var policies = ReadPolicies(action);
            if (options.RequireExplicitPolicy && policies.Count == 0)
            {
                continue;
            }
            candidates.Add((description, action, method, policies));
        }

        var collidingNames = candidates
            .GroupBy(
                candidate => candidate.Action.ControllerName + "." + candidate.Action.ActionName,
                StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return candidates
            .Select(candidate => new NhAiBridgeActionInfo
            {
                ControllerName = candidate.Action.ControllerName,
                ActionName = candidate.Action.ActionName,
                HttpMethod = candidate.Method,
                RouteTemplate = (candidate.Description.RelativePath ?? string.Empty).Trim('/'),
                Parameters = ReadParameters(candidate.Description, candidate.Action),
                ResponseType = candidate.Description.SupportedResponseTypes
                    .Where(response => response.StatusCode is >= 200 and < 300)
                    .Select(response => response.Type)
                    .FirstOrDefault(type => type is not null && type != typeof(void)),
                AuthorizationPolicies = candidate.Policies,
                ActionNameCollides = collidingNames.Contains(
                    candidate.Action.ControllerName + "." + candidate.Action.ActionName),
                BridgeTool = candidate.Action.MethodInfo.GetCustomAttribute<NhAiBridgeToolAttribute>(inherit: true),
                MethodInfo = candidate.Action.MethodInfo,
                ActionDescriptor = candidate.Action,
                ApiDescription = candidate.Description
            })
            .OrderBy(action => action.ControllerName, StringComparer.Ordinal)
            .ThenBy(action => action.ActionName, StringComparer.Ordinal)
            .ThenBy(action => action.RouteTemplate, StringComparer.Ordinal)
            .ToList();
    }

    private NhAiToolDescriptor CreateDescriptor(NhAiBridgeActionInfo action, string id, string exportName)
    {
        var bridgeTool = action.BridgeTool;
        var effect = GetEffect(action);
        if (bridgeTool?.EffectOverride is { } effectOverride)
        {
            if (effectOverride < effect)
            {
                throw new InvalidOperationException(
                    $"[NhAiBridgeTool(Effect = {effectOverride})] on '{Describe(action)}' is less cautious than its '{effect}' HTTP method effect.");
            }
            effect = effectOverride;
        }
        if (effect == NhAiToolEffect.Destructive)
        {
            throw new InvalidOperationException(
                $"'{Describe(action)}' would be a destructive bridge tool; destructive tools require a verifier and are not supported by the API bridge in v1.");
        }
        if (bridgeTool?.RequireApproval == true && effect != NhAiToolEffect.ReadOnly)
        {
            throw new InvalidOperationException(
                $"[NhAiBridgeTool(RequireApproval = true)] on '{Describe(action)}' is only valid on reads; every other effect already requires approval.");
        }
        if (bridgeTool is { MaxResultBytes: < 0 } or { TimeoutSeconds: < 0 })
        {
            throw new InvalidOperationException(
                $"[NhAiBridgeTool] on '{Describe(action)}' declares a negative limit.");
        }

        var defaults = options.ToolDefaults;
        var maxResultBytes = bridgeTool is { MaxResultBytes: > 0 }
            ? Math.Min(bridgeTool.MaxResultBytes, defaults.MaxResultBytes)
            : defaults.MaxResultBytes;
        var timeoutSeconds = bridgeTool is { TimeoutSeconds: > 0 }
            ? Math.Min(bridgeTool.TimeoutSeconds, defaults.TimeoutSeconds)
            : defaults.TimeoutSeconds;

        var inputSchema = conventions.BuildInputSchema(action);
        if (inputSchema.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                $"API bridge conventions returned a non-object input schema for '{Describe(action)}'.");
        }
        var inputSchemaJson = inputSchema.GetRawText();
        var policies = action.AuthorizationPolicies;
        var contractHash = ComputeHash(string.Join(
            "\n",
            action.HttpMethod,
            action.RouteTemplate,
            inputSchemaJson,
            string.Join(",", policies)));
        var exposure = NhAiToolExposure.Local | NhAiToolExposure.Agent;
        if (options.McpExposureEnabled)
        {
            exposure |= NhAiToolExposure.Mcp;
        }

        var readOnly = effect == NhAiToolEffect.ReadOnly;
        return new NhAiToolDescriptor(
            id,
            options.ContractVersion,
            GetDescription(action),
            typeof(JsonElement),
            typeof(NhAiBridgeResponse),
            effect,
            exposure,
            true,
            policies)
        {
            ExportName = exportName,
            CatalogId = options.ToolSetId!,
            CatalogVersion = options.ContractVersion,
            DeclaringAssembly = action.MethodInfo.Module.Assembly.GetName().Name ?? string.Empty,
            InputSchemaJson = inputSchemaJson,
            OutputSchemaJson = OutputSchemaJson,
            SchemaHash = ComputeHash(inputSchemaJson + "\n" + OutputSchemaJson),
            ContractHash = contractHash,
            Approval = readOnly && bridgeTool?.RequireApproval != true
                ? NhAiApprovalRequirement.PolicyControlled
                : NhAiApprovalRequirement.Required,
            Idempotency = readOnly ? NhAiIdempotencySupport.None : NhAiIdempotencySupport.Required,
            Timeout = TimeSpan.FromSeconds(timeoutSeconds),
            MaxConcurrency = defaults.MaxConcurrency,
            MaxInputBytes = defaults.MaxInputBytes,
            MaxResultBytes = maxResultBytes
        };
    }

    private string GetDescription(NhAiBridgeActionInfo action)
    {
        if (!string.IsNullOrWhiteSpace(action.BridgeTool?.Description))
        {
            return action.BridgeTool.Description.Trim();
        }

        var summary = xmlDocumentation.GetSummary(action.MethodInfo);
        if (!string.IsNullOrWhiteSpace(summary))
        {
            return summary;
        }
        return conventions.GetDescription(action);
    }

    private static NhAiToolEffect GetEffect(NhAiBridgeActionInfo action)
    {
        return action.HttpMethod switch
        {
            "GET" => NhAiToolEffect.ReadOnly,
            "PUT" or "PATCH" => NhAiToolEffect.IdempotentMutation,
            "POST" => NhAiToolEffect.Mutation,
            _ => NhAiToolEffect.Destructive
        };
    }

    private bool IsIncludedController(string controllerName)
    {
        return options.IncludeControllerPatterns.Any(pattern => NhAiMvcBridgeNames.MatchesPattern(controllerName, pattern))
            && !options.ExcludeControllerPatterns.Any(pattern => NhAiMvcBridgeNames.MatchesPattern(controllerName, pattern));
    }

    private bool IsExcludedAction(ControllerActionDescriptor action)
    {
        var name = action.ControllerName + "." + action.ActionName;
        return options.ExcludeActionPatterns.Any(pattern => NhAiMvcBridgeNames.MatchesPattern(name, pattern));
    }

    private static bool AllowsAnonymous(ControllerActionDescriptor action)
    {
        return action.EndpointMetadata.OfType<IAllowAnonymous>().Any()
            || action.FilterDescriptors.Any(filter => filter.Filter is AllowAnonymousFilter);
    }

    private static IReadOnlyList<string> ReadPolicies(ControllerActionDescriptor action)
    {
        var authorizeData = action.EndpointMetadata.OfType<IAuthorizeData>()
            .Concat(action.FilterDescriptors
                .Select(filter => filter.Filter)
                .OfType<AuthorizeFilter>()
                .SelectMany(filter => filter.AuthorizeData ?? []));
        return authorizeData
            .Select(data => data.Policy)
            .Where(policy => !string.IsNullOrWhiteSpace(policy))
            .Select(policy => policy!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(policy => policy, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool HasFileUpload(ApiDescription description, ControllerActionDescriptor action)
    {
        return description.ParameterDescriptions.Any(parameter => parameter.Source == BindingSource.FormFile)
            || action.Parameters.Any(parameter => IsFileType(parameter.ParameterType));
    }

    private static bool IsFileType(Type type)
    {
        return typeof(IFormFile).IsAssignableFrom(type)
            || typeof(IFormFileCollection).IsAssignableFrom(type)
            || typeof(IEnumerable<IFormFile>).IsAssignableFrom(type);
    }

    private static IReadOnlyList<NhAiBridgeParameterInfo> ReadParameters(
        ApiDescription description,
        ControllerActionDescriptor action)
    {
        var result = new List<NhAiBridgeParameterInfo>();
        var handledTopLevel = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in description.ParameterDescriptions)
        {
            var topLevel = parameter.ParameterDescriptor;
            var topLevelType = topLevel?.ParameterType ?? parameter.Type ?? typeof(object);
            var parameterInfo = (topLevel as ControllerParameterDescriptor)?.ParameterInfo;

            if (topLevel is not null
                && parameter.Source == BindingSource.Query
                && NhAiMvcBridgeDefaultConventions.IsCollectionRequestType(topLevelType))
            {
                if (handledTopLevel.Add(topLevel.Name))
                {
                    result.Add(new NhAiBridgeParameterInfo
                    {
                        Name = topLevel.Name,
                        InputName = "page",
                        Source = NhAiBridgeParameterSource.Query,
                        ParameterType = topLevelType,
                        IsCollectionRequest = true
                    });
                }
                if (!NhAiMvcBridgeDefaultConventions.IsCollectionPropertyName(parameter.Name)
                    && !parameter.Name.Contains('.', StringComparison.Ordinal)
                    && !parameter.Name.Contains('[', StringComparison.Ordinal)
                    && parameter.Type is not null
                    && IsSimpleOrSimpleList(parameter.Type))
                {
                    result.Add(CreateValueParameter(parameter, NhAiBridgeParameterSource.Query, parameterInfo, forceOptional: true));
                }
                continue;
            }

            if (parameter.Source == BindingSource.Path)
            {
                result.Add(new NhAiBridgeParameterInfo
                {
                    Name = parameter.Name,
                    InputName = ToCamelCase(parameter.Name),
                    Source = NhAiBridgeParameterSource.Route,
                    ParameterType = parameter.Type ?? typeof(string),
                    IsRequired = !(parameter.RouteInfo?.IsOptional ?? false)
                });
            }
            else if (parameter.Source == BindingSource.Body)
            {
                var bodyType = parameter.Type ?? topLevelType;
                result.Add(new NhAiBridgeParameterInfo
                {
                    Name = parameter.Name,
                    InputName = "body",
                    Source = NhAiBridgeParameterSource.Body,
                    ParameterType = bodyType,
                    IsRequired = parameterInfo is null || (!IsNullable(parameterInfo) && !parameterInfo.HasDefaultValue),
                    IsFreeForm = IsFreeForm(bodyType)
                });
            }
            else if (parameter.Source == BindingSource.FormFile)
            {
                result.Add(new NhAiBridgeParameterInfo
                {
                    Name = parameter.Name,
                    InputName = ToCamelCase(parameter.Name),
                    Source = NhAiBridgeParameterSource.FormFile,
                    ParameterType = parameter.Type ?? typeof(IFormFile),
                    IsRequired = parameter.IsRequired
                });
            }
            else if (parameter.Source == BindingSource.Form
                && parameter.Type is not null
                && IsSimpleOrSimpleList(parameter.Type))
            {
                result.Add(CreateValueParameter(parameter, NhAiBridgeParameterSource.Form, parameterInfo, forceOptional: false));
            }
            else if ((parameter.Source == BindingSource.Query
                    || parameter.Source == BindingSource.ModelBinding
                    || parameter.Source == BindingSource.Custom)
                && parameter.Type is not null
                && IsSimpleOrSimpleList(parameter.Type)
                && !parameter.Name.Contains('[', StringComparison.Ordinal))
            {
                result.Add(CreateValueParameter(parameter, NhAiBridgeParameterSource.Query, parameterInfo, forceOptional: false));
            }
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in result)
        {
            var inputNames = parameter.IsCollectionRequest
                ? ["page", "itemsPerPage", "search", "orderBy", "filter"]
                : new[] { parameter.InputName };
            foreach (var inputName in inputNames)
            {
                if (!names.Add(inputName))
                {
                    throw new InvalidOperationException(
                        $"API bridge action '{action.ControllerName}.{action.ActionName}' binds input '{inputName}' more than once.");
                }
            }
        }
        return result;
    }

    private static NhAiBridgeParameterInfo CreateValueParameter(
        ApiParameterDescription parameter,
        NhAiBridgeParameterSource source,
        ParameterInfo? parameterInfo,
        bool forceOptional)
    {
        // A top-level action parameter (not a flattened model property) is required when it
        // has no default value and is not nullable, matching MVC's implicit required rules.
        var ownParameter = parameterInfo is not null
            && string.Equals(parameterInfo.Name, parameter.Name, StringComparison.OrdinalIgnoreCase)
            ? parameterInfo
            : null;
        var hasDefault = parameter.DefaultValue is not null || (ownParameter?.HasDefaultValue ?? false);
        var implicitlyRequired = ownParameter is not null && !IsNullable(ownParameter);
        return new NhAiBridgeParameterInfo
        {
            Name = parameter.Name,
            InputName = ToCamelCase(parameter.Name),
            Source = source,
            ParameterType = parameter.Type!,
            IsRequired = !forceOptional && !hasDefault && (parameter.IsRequired || implicitlyRequired)
        };
    }

    private static bool IsSimpleOrSimpleList(Type type)
    {
        if (IsSimple(type))
        {
            return true;
        }
        if (type.IsArray)
        {
            return IsSimple(type.GetElementType()!);
        }
        var enumerable = type.GetInterfaces()
            .Append(type)
            .FirstOrDefault(candidate => candidate.IsGenericType
                && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        return enumerable is not null && IsSimple(enumerable.GetGenericArguments()[0]);
    }

    private static bool IsSimple(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        return underlying.IsPrimitive
            || underlying.IsEnum
            || underlying == typeof(string)
            || underlying == typeof(decimal)
            || underlying == typeof(Guid)
            || underlying == typeof(DateTime)
            || underlying == typeof(DateTimeOffset)
            || underlying == typeof(DateOnly)
            || underlying == typeof(TimeOnly)
            || underlying == typeof(TimeSpan);
    }

    private static bool IsFreeForm(Type type)
    {
        return type == typeof(object)
            || type == typeof(JsonElement)
            || type == typeof(JsonDocument)
            || typeof(System.Text.Json.Nodes.JsonNode).IsAssignableFrom(type)
            || string.Equals(type.Namespace, "Newtonsoft.Json.Linq", StringComparison.Ordinal);
    }

    private static bool IsNullable(ParameterInfo parameter)
    {
        if (Nullable.GetUnderlyingType(parameter.ParameterType) is not null)
        {
            return true;
        }
        if (parameter.ParameterType.IsValueType)
        {
            return false;
        }
        return new NullabilityInfoContext().Create(parameter).WriteState == NullabilityState.Nullable;
    }

    private static string ToCamelCase(string name)
    {
        return JsonNamingPolicy.CamelCase.ConvertName(name);
    }

    private static string Describe(NhAiBridgeActionInfo action)
    {
        return action.HttpMethod + " /" + action.RouteTemplate;
    }

    private static string ComputeHash(string value)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}
