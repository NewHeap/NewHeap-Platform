using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace NewHeap.Platform.AI.Generators;

[Generator(LanguageNames.CSharp)]
public sealed class NhAiToolGenerator : IIncrementalGenerator
{
    private const string ToolSetAttributeName = "NewHeap.Platform.AI.NhAiToolSetAttribute";
    private const string ToolAttributeName = "NewHeap.Platform.AI.NhAiToolAttribute";
    private const string ToolExportNameAttributeName = "NewHeap.Platform.AI.NhAiToolExportNameAttribute";
    private const string InvocationContextName = "NewHeap.Platform.AI.NhAiInvocationContext";
    private const string CancellationTokenName = "System.Threading.CancellationToken";
    private const string DescriptionAttributeName = "System.ComponentModel.DescriptionAttribute";
    private const string AuthorizeAttributeName = "Microsoft.AspNetCore.Authorization.AuthorizeAttribute";

    private static readonly DiagnosticDescriptor MissingToolSet = new(
        "NHAI001",
        "AI tool set is missing",
        "Method '{0}' has NhAiTool but its containing type has no NhAiToolSet attribute",
        "NewHeap.AI",
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor InvalidIdentifier = new(
        "NHAI002",
        "AI tool identifier is unstable",
        "AI tool identifier '{0}' must use lowercase dash-case",
        "NewHeap.AI",
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor UnsupportedSignature = new(
        "NHAI003",
        "AI tool signature is unsupported",
        "Method '{0}' must be a public instance method with parameters (input, NhAiInvocationContext, CancellationToken) and return Task<TaskResult<T>>",
        "NewHeap.AI",
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor MissingDescription = new(
        "NHAI004",
        "AI tool description is missing",
        "Method '{0}' must declare DescriptionAttribute",
        "NewHeap.AI",
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor DuplicateTool = new(
        "NHAI005",
        "AI tool identifier is duplicated",
        "AI tool identifier '{0}' and version '{1}' are declared more than once",
        "NewHeap.AI",
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor InvalidVersion = new(
        "NHAI006",
        "AI tool contract version is invalid",
        "AI tool '{0}' must declare a contract version greater than zero",
        "NewHeap.AI",
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor RemoteExposureRequiresAuthorization = new(
        "NHAI007",
        "Remote AI tool exposure requires authorization",
        "AI tool '{0}' enables remote exposure but declares no AuthorizeAttribute boundary",
        "NewHeap.AI",
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor UnsafeMutationContract = new(
        "NHAI008",
        "AI mutation contract lacks required safeguards",
        "AI tool '{0}' with effect '{1}' must declare the required approval, idempotency, and verification safeguards",
        "NewHeap.AI",
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor InvalidExecutionBounds = new(
        "NHAI009",
        "AI tool execution bounds are invalid",
        "AI tool '{0}' must declare positive timeout, concurrency, and result-size limits",
        "NewHeap.AI",
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor InvalidExportName = new(
        "NHAI010",
        "AI tool export name is invalid",
        "AI tool export name '{0}' must be a bounded lowercase MCP name and must not encode a contract version",
        "NewHeap.AI",
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor DuplicateExportName = new(
        "NHAI011",
        "AI tool export name is duplicated",
        "AI tool export name '{0}' is declared more than once",
        "NewHeap.AI",
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor FlatExportRequiresObjectInput = new(
        "NHAI012",
        "AI tool flat export schema requires an object input type",
        "AI tool '{0}' declares a flat export schema, so its input parameter must be an object type whose properties become the top-level arguments",
        "NewHeap.AI",
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor WideningAnnotationHint = new(
        "NHAI013",
        "AI tool annotation hint is less cautious than its effect",
        "AI tool '{0}' declares {1}, which is less cautious than its effect; a hint override may only make the published annotations more cautious",
        "NewHeap.AI",
        DiagnosticSeverity.Error,
        true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var attributedMethods = context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is MethodDeclarationSyntax method
                    && method.AttributeLists.Count > 0,
                static (syntaxContext, cancellationToken) =>
                    syntaxContext.SemanticModel.GetDeclaredSymbol(
                        (MethodDeclarationSyntax)syntaxContext.Node,
                        cancellationToken) as IMethodSymbol)
            .Where(static method => method is not null)
            .Select(static (method, _) => method!)
            .Collect();

        context.RegisterSourceOutput(attributedMethods, Generate);
    }

    private static void Generate(
        SourceProductionContext context,
        ImmutableArray<IMethodSymbol> methods)
    {
        var tools = new List<ToolModel>();

        foreach (var method in methods)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var location = method.Locations.FirstOrDefault();
            var toolAttribute = FindAttribute(method, ToolAttributeName);
            if (toolAttribute is null)
            {
                continue;
            }

            var toolSetAttribute = FindAttribute(method.ContainingType, ToolSetAttributeName);
            if (toolSetAttribute is null)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    MissingToolSet,
                    location,
                    method.Name));
                continue;
            }

            var setId = (string?)toolSetAttribute.ConstructorArguments[0].Value ?? string.Empty;
            var jsonSerializerContextType = GetNamedType(
                toolSetAttribute,
                "JsonSerializerContextType");
            var jsonSerializerContextSymbol = GetNamedTypeSymbol(
                toolSetAttribute,
                "JsonSerializerContextType");
            var toolId = (string?)toolAttribute.ConstructorArguments[0].Value ?? string.Empty;
            var exportNameAttribute = FindAttribute(method, ToolExportNameAttributeName);
            var explicitExportName = exportNameAttribute is null
                ? null
                : (string?)exportNameAttribute.ConstructorArguments[0].Value;
            if (!IsDashCase(setId))
            {
                context.ReportDiagnostic(Diagnostic.Create(InvalidIdentifier, location, setId));
                continue;
            }
            if (!IsDashCase(toolId))
            {
                context.ReportDiagnostic(Diagnostic.Create(InvalidIdentifier, location, toolId));
                continue;
            }
            if (explicitExportName is not null && !IsExportName(explicitExportName))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    InvalidExportName,
                    location,
                    explicitExportName));
                continue;
            }

            if (!TryGetSignature(
                method,
                out var inputTypeSymbol,
                out var outputTypeSymbol,
                out var inputType,
                out var outputType))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    UnsupportedSignature,
                    location,
                    method.Name));
                continue;
            }

            var descriptionAttribute = FindAttribute(method, DescriptionAttributeName);
            var description = descriptionAttribute is null
                ? null
                : (string?)descriptionAttribute.ConstructorArguments[0].Value;
            if (string.IsNullOrWhiteSpace(description))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    MissingDescription,
                    location,
                    method.Name));
                continue;
            }

            var version = (int)(toolAttribute.ConstructorArguments[1].Value ?? 0);
            var effect = (int)(toolAttribute.ConstructorArguments[2].Value ?? 0);
            var exposure = (int)(toolAttribute.ConstructorArguments[3].Value ?? 0);
            var approval = GetNamedInt(toolAttribute, "Approval", 0);
            var idempotency = GetNamedInt(toolAttribute, "Idempotency", 0);
            var exportSchema = GetNamedInt(toolAttribute, "ExportSchema", 0);
            var readOnlyHint = GetNamedInt(toolAttribute, "ReadOnlyHint", 0);
            var destructiveHint = GetNamedInt(toolAttribute, "DestructiveHint", 0);
            var idempotentHint = GetNamedInt(toolAttribute, "IdempotentHint", 0);
            var openWorldHint = GetNamedInt(toolAttribute, "OpenWorldHint", 0);
            var verifierId = GetNamedString(toolAttribute, "VerifierId");
            var timeoutSeconds = GetNamedInt(toolAttribute, "TimeoutSeconds", 60);
            var maxConcurrency = GetNamedInt(toolAttribute, "MaxConcurrency", 1);
            var maxInputBytes = GetNamedInt(toolAttribute, "MaxInputBytes", 65_536);
            var maxResultBytes = GetNamedInt(toolAttribute, "MaxResultBytes", 65_536);
            var dataClassification = GetNamedInt(toolAttribute, "DataClassification", 1);
            var retentionCategory = GetNamedInt(toolAttribute, "RetentionCategory", 1);
            var requiredCapabilities = GetNamedStringArray(toolAttribute, "RequiredCapabilities")
                .OrderBy(capability => capability, StringComparer.Ordinal)
                .ToImmutableArray();
            if (version < 1)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    InvalidVersion,
                    location,
                    setId + "." + toolId));
                continue;
            }
            var authorizationAttributes = method.ContainingType.GetAttributes()
                .Concat(method.GetAttributes())
                .Where(attribute => attribute.AttributeClass?.ToDisplayString() == AuthorizeAttributeName)
                .ToImmutableArray();
            var policies = authorizationAttributes
                .Select(attribute => attribute.NamedArguments
                    .FirstOrDefault(argument => argument.Key == "Policy").Value.Value as string)
                .Where(policy => !string.IsNullOrWhiteSpace(policy))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(policy => policy, StringComparer.Ordinal)
                .ToImmutableArray();
            if ((exposure & ~1) != 0 && authorizationAttributes.Length == 0)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    RemoteExposureRequiresAuthorization,
                    location,
                    setId + "." + toolId));
                continue;
            }
            if (timeoutSeconds < 1
                || maxConcurrency < 1
                || maxInputBytes < 1
                || maxResultBytes < 1)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    InvalidExecutionBounds,
                    location,
                    setId + "." + toolId));
                continue;
            }
            if (requiredCapabilities.Any(capability => !IsDashCase(capability)))
            {
                var invalidCapability = requiredCapabilities.First(capability => !IsDashCase(capability));
                context.ReportDiagnostic(Diagnostic.Create(
                    InvalidIdentifier,
                    location,
                    invalidCapability));
                continue;
            }
            if (!string.IsNullOrWhiteSpace(verifierId) && !IsDashCase(verifierId!))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    InvalidIdentifier,
                    location,
                    verifierId));
                continue;
            }
            // Idempotency 2 = Required, 3 = ConsumerAuthoritative (the tool reconciles replays itself).
            // Approval 1 = Required, 3 = ConsumerAuthoritative (the tool validates its own approval),
            // 4 = Issuer (the tool issues an approval artifact under the consumer's own authorization).
            // Destructive effects always require Platform approval and a verifier.
            var isIssuer = approval == 4;
            var unsafeIssuer = isIssuer
                && (effect == 0 || effect == 4 || idempotency == 1 || idempotency == 2);
            var unsafeSideEffect = effect != 0
                && !isIssuer
                && idempotency != 2
                && idempotency != 3;
            var unsafeMutation = effect == 2 && approval != 1 && approval != 3 && !isIssuer;
            var unsafeExternalEffect = effect == 3 && approval != 1 && approval != 3 && !isIssuer;
            var unsafeDestructive = effect == 4
                && (approval != 1 || string.IsNullOrWhiteSpace(verifierId));
            if (unsafeIssuer || unsafeSideEffect || unsafeMutation || unsafeExternalEffect || unsafeDestructive)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    UnsafeMutationContract,
                    location,
                    setId + "." + toolId,
                    effect));
                continue;
            }

            var wideningHint = FindWideningHint(
                effect,
                readOnlyHint,
                destructiveHint,
                idempotentHint,
                openWorldHint);
            if (wideningHint is not null)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    WideningAnnotationHint,
                    location,
                    setId + "." + toolId,
                    wideningHint));
                continue;
            }

            var usesFlatExportDefaults = exportSchema == 1 && jsonSerializerContextSymbol is null;
            var serializerContract = usesFlatExportDefaults
                ? SerializerContract.FlatExportDefaults
                : SerializerContract.FromContext(jsonSerializerContextSymbol);
            var inputSchema = SchemaWriter.Create(
                inputTypeSymbol!,
                serializerContract,
                SchemaDirection.Input);
            var outputSchema = SchemaWriter.Create(
                outputTypeSymbol!,
                serializerContract,
                SchemaDirection.Output);
            if (exportSchema == 1 && !inputSchema.StartsWith("{\"type\":\"object\",\"properties\"", StringComparison.Ordinal))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    FlatExportRequiresObjectInput,
                    location,
                    setId + "." + toolId));
                continue;
            }
            var schemaHash = ComputeHash(inputSchema + "\n" + outputSchema);
            var contractMaterial = string.Join(
                "\n",
                setId + "." + toolId,
                version.ToString(global::System.Globalization.CultureInfo.InvariantCulture),
                description!,
                effect.ToString(global::System.Globalization.CultureInfo.InvariantCulture),
                exposure.ToString(global::System.Globalization.CultureInfo.InvariantCulture),
                approval.ToString(global::System.Globalization.CultureInfo.InvariantCulture),
                idempotency.ToString(global::System.Globalization.CultureInfo.InvariantCulture),
                verifierId ?? string.Empty,
                timeoutSeconds.ToString(global::System.Globalization.CultureInfo.InvariantCulture),
                maxConcurrency.ToString(global::System.Globalization.CultureInfo.InvariantCulture),
                maxInputBytes.ToString(global::System.Globalization.CultureInfo.InvariantCulture),
                maxResultBytes.ToString(global::System.Globalization.CultureInfo.InvariantCulture),
                dataClassification.ToString(global::System.Globalization.CultureInfo.InvariantCulture),
                retentionCategory.ToString(global::System.Globalization.CultureInfo.InvariantCulture),
                string.Join(",", requiredCapabilities),
                (authorizationAttributes.Length > 0).ToString(),
                string.Join(",", policies),
                schemaHash);
            if (explicitExportName is not null)
            {
                contractMaterial += "\nexport:" + explicitExportName;
            }
            if (exportSchema != 0)
            {
                // Only a non-default export schema enters the contract hash so existing
                // enveloped contracts keep their published hashes.
                contractMaterial += "\nexport-schema:" + exportSchema.ToString(global::System.Globalization.CultureInfo.InvariantCulture);
            }
            if (readOnlyHint != 0 || destructiveHint != 0 || idempotentHint != 0 || openWorldHint != 0)
            {
                // Only declared hint overrides enter the contract hash.
                contractMaterial += "\nhints:"
                    + readOnlyHint.ToString(CultureInfo.InvariantCulture) + ","
                    + destructiveHint.ToString(CultureInfo.InvariantCulture) + ","
                    + idempotentHint.ToString(CultureInfo.InvariantCulture) + ","
                    + openWorldHint.ToString(CultureInfo.InvariantCulture);
            }
            var contractHash = ComputeHash(contractMaterial);

            tools.Add(new ToolModel(
                method,
                setId,
                toolId,
                explicitExportName,
                version,
                effect,
                exposure,
                description!,
                inputType!,
                outputType!,
                jsonSerializerContextType,
                inputSchema,
                outputSchema,
                schemaHash,
                contractHash,
                approval,
                idempotency,
                exportSchema,
                usesFlatExportDefaults,
                readOnlyHint,
                destructiveHint,
                idempotentHint,
                openWorldHint,
                verifierId,
                timeoutSeconds,
                maxConcurrency,
                maxInputBytes,
                maxResultBytes,
                dataClassification,
                retentionCategory,
                requiredCapabilities,
                authorizationAttributes.Length > 0,
                policies));
        }

        var duplicateGroups = tools.GroupBy(tool => tool.LogicalId + "@" + tool.Version)
            .Where(group => group.Count() > 1)
            .ToArray();
        foreach (var duplicate in duplicateGroups)
        {
            foreach (var tool in duplicate)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DuplicateTool,
                    tool.Method.Locations.FirstOrDefault(),
                    tool.LogicalId,
                    tool.Version));
            }
        }

        var duplicateKeys = duplicateGroups.Select(group => group.Key).ToImmutableHashSet(StringComparer.Ordinal);
        var duplicateExportGroups = tools
            .Where(tool => !duplicateKeys.Contains(tool.LogicalId + "@" + tool.Version))
            .GroupBy(tool => tool.ExportName, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .ToArray();
        foreach (var duplicate in duplicateExportGroups)
        {
            foreach (var tool in duplicate)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DuplicateExportName,
                    tool.Method.Locations.FirstOrDefault(),
                    tool.ExportName));
            }
        }
        var duplicateExportNames = duplicateExportGroups
            .Select(group => group.Key)
            .ToImmutableHashSet(StringComparer.Ordinal);
        foreach (var group in tools
            .Where(tool => !duplicateKeys.Contains(tool.LogicalId + "@" + tool.Version))
            .Where(tool => !duplicateExportNames.Contains(tool.ExportName))
            .GroupBy(tool => tool.Method.ContainingType, SymbolEqualityComparer.Default))
        {
            EmitCatalog(
                context,
                (INamedTypeSymbol)group.Key!,
                group.OrderBy(tool => tool.LogicalId, StringComparer.Ordinal)
                    .ThenBy(tool => tool.Version)
                    .ToArray());
        }
    }

    private static void EmitCatalog(
        SourceProductionContext context,
        INamedTypeSymbol containingType,
        IReadOnlyList<ToolModel> tools)
    {
        var namespaceName = containingType.ContainingNamespace.IsGlobalNamespace
            ? null
            : containingType.ContainingNamespace.ToDisplayString();
        var catalogName = containingType.Name + "NhAiCatalog";
        var catalogId = tools[0].SetId;
        var catalogHash = ComputeHash(string.Join(
            "\n",
            tools.Select(tool => tool.LogicalId + "@" + tool.Version + ":" + tool.ContractHash)));
        var typeName = containingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated />");
        builder.AppendLine("#nullable enable");
        if (namespaceName is not null)
        {
            builder.Append("namespace ").Append(namespaceName).AppendLine(";");
            builder.AppendLine();
        }

        builder.Append("public sealed class ").Append(catalogName)
            .AppendLine(" : global::NewHeap.Platform.AI.INhAiGeneratedToolCatalog");
        builder.AppendLine("{");
        builder.AppendLine("    public global::NewHeap.Platform.AI.NhAiToolCatalogGovernance Governance => global::NewHeap.Platform.AI.NhAiToolCatalogGovernance.SharedInvoker;");
        builder.AppendLine();
        for (var index = 0; index < tools.Count; index++)
        {
            AppendDescriptor(builder, tools[index], index);
        }

        builder.AppendLine("    private static readonly global::NewHeap.Platform.AI.NhAiToolDescriptor[] AllDescriptors =");
        builder.AppendLine("    [");
        for (var index = 0; index < tools.Count; index++)
        {
            builder.Append("        Tool").Append(index).AppendLine(",");
        }
        builder.AppendLine("    ];");
        builder.AppendLine();
        builder.AppendLine("    private static readonly global::NewHeap.Platform.AI.NhAiToolCatalogManifest GeneratedManifest = new(");
        builder.Append("        ").Append(Literal(catalogId)).AppendLine(",");
        builder.AppendLine("        1,");
        builder.Append("        ").Append(Literal(catalogHash)).AppendLine(",");
        builder.AppendLine("        new global::NewHeap.Platform.AI.NhAiToolManifestEntry[]");
        builder.AppendLine("        {");
        foreach (var tool in tools)
        {
            builder.Append("            new(")
                .Append(Literal(tool.LogicalId)).Append(", ")
                .Append(tool.Version).Append(", ")
                .Append(Literal(tool.SchemaHash)).Append(", ")
                .Append(Literal(tool.ContractHash)).AppendLine(")")
                .Append("            { ExportName = ")
                .Append(Literal(tool.ExportName)).AppendLine(" },");
        }
        builder.AppendLine("        });");
        builder.AppendLine();
        builder.AppendLine("    public global::System.Collections.Generic.IReadOnlyList<global::NewHeap.Platform.AI.NhAiToolDescriptor> Descriptors => AllDescriptors;");
        builder.AppendLine();
        builder.AppendLine("    public global::NewHeap.Platform.AI.NhAiToolCatalogManifest Manifest => GeneratedManifest;");
        builder.AppendLine();
        builder.AppendLine("    public global::System.Collections.Generic.IReadOnlyList<global::Microsoft.Extensions.AI.AIFunction> CreateFunctions(global::System.IServiceProvider services)");
        builder.AppendLine("    {");
        builder.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(services);");
        builder.Append("        var tool = services.GetService(typeof(").Append(typeName).Append(") ) as ")
            .Append(typeName).AppendLine(";");
        builder.AppendLine("        if (tool is null)");
        builder.AppendLine("        {");
        builder.Append("            throw new global::System.InvalidOperationException(")
            .Append(Literal($"Required AI tool service '{containingType.ToDisplayString()}' is not registered."))
            .AppendLine(");");
        builder.AppendLine("        }");
        builder.AppendLine("        var invoker = services.GetService(typeof(global::NewHeap.Platform.AI.INhAiToolInvoker)) as global::NewHeap.Platform.AI.INhAiToolInvoker;");
        builder.AppendLine("        if (invoker is null)");
        builder.AppendLine("        {");
        builder.AppendLine("            throw new global::System.InvalidOperationException(\"INhAiToolInvoker is not registered. AI tool execution remains disabled.\");");
        builder.AppendLine("        }");
        builder.AppendLine("        var functions = new global::System.Collections.Generic.List<global::Microsoft.Extensions.AI.AIFunction>();");
        builder.AppendLine();

        for (var index = 0; index < tools.Count; index++)
        {
            var tool = tools[index];
            builder.Append("        global::System.Func<").Append(tool.InputType)
                .Append(", global::System.Threading.CancellationToken, global::System.Threading.Tasks.Task<global::NewHeap.Platform.Common.Models.TaskResult<")
                .Append(tool.OutputType).Append(">>> handler").Append(index).AppendLine(" =");
            builder.AppendLine("            (input, cancellationToken) => invoker.InvokeAsync(");
            builder.Append("                Tool").Append(index).AppendLine(",");
            builder.AppendLine("                input,");
            builder.Append("                (invocationContext, invocationCancellationToken) => tool.")
                .Append(tool.Method.Name).AppendLine("(input, invocationContext, invocationCancellationToken),");
            builder.AppendLine("                cancellationToken);");
            builder.Append("        functions.Add(global::NewHeap.Platform.AI.NhAiGovernedAIFunction.Create(Tool")
                .Append(index)
                .Append(", global::Microsoft.Extensions.AI.AIFunctionFactory.Create(handler")
                .Append(index).AppendLine(", new global::Microsoft.Extensions.AI.AIFunctionFactoryOptions");
            builder.AppendLine("        {");
            builder.Append("            Name = ").Append(Literal(tool.ExportName)).AppendLine(",");
            builder.Append("            Description = ").Append(Literal(tool.Description));
            if (tool.JsonSerializerContextType is not null)
            {
                builder.AppendLine(",");
                builder.Append("            SerializerOptions = ")
                    .Append(tool.JsonSerializerContextType)
                    .AppendLine(".Default.Options");
            }
            else if (tool.UsesFlatExportDefaults)
            {
                builder.AppendLine(",");
                builder.AppendLine("            SerializerOptions = global::NewHeap.Platform.AI.NhAiToolJsonSerializerOptions.FlatExport");
            }
            else
            {
                builder.AppendLine();
            }
            builder.AppendLine("        }), services));");
            builder.AppendLine();
        }

        builder.AppendLine("        return functions;");
        builder.AppendLine("    }");
        builder.AppendLine("}");

        var hintName = (namespaceName is null ? string.Empty : namespaceName.Replace('.', '_') + "_")
            + catalogName + ".g.cs";
        context.AddSource(hintName, SourceText.From(builder.ToString(), Encoding.UTF8));
    }

    private static void AppendDescriptor(StringBuilder builder, ToolModel tool, int index)
    {
        builder.Append("    private static readonly global::NewHeap.Platform.AI.NhAiToolDescriptor Tool")
            .Append(index).AppendLine(" = new(");
        builder.Append("        ").Append(Literal(tool.LogicalId)).AppendLine(",");
        builder.Append("        ").Append(tool.Version).AppendLine(",");
        builder.Append("        ").Append(Literal(tool.Description)).AppendLine(",");
        builder.Append("        typeof(").Append(tool.InputType).AppendLine("),");
        builder.Append("        typeof(").Append(tool.OutputType).AppendLine("),");
        builder.Append("        (global::NewHeap.Platform.AI.NhAiToolEffect)").Append(tool.Effect).AppendLine(",");
        builder.Append("        (global::NewHeap.Platform.AI.NhAiToolExposure)").Append(tool.Exposure).AppendLine(",");
        builder.Append("        ").Append(tool.RequiresAuthorization ? "true" : "false").AppendLine(",");
        builder.AppendLine("        new global::System.String[]");
        builder.AppendLine("        {");
        foreach (var policy in tool.AuthorizationPolicies)
        {
            builder.Append("            ").Append(Literal(policy!)).AppendLine(",");
        }
        builder.AppendLine("        })");
        builder.AppendLine("    {");
        builder.Append("        ExportName = ").Append(Literal(tool.ExportName)).AppendLine(",");
        builder.Append("        CatalogId = ").Append(Literal(tool.SetId)).AppendLine(",");
        builder.AppendLine("        CatalogVersion = 1,");
        builder.Append("        DeclaringAssembly = ")
            .Append(Literal(tool.Method.ContainingAssembly.Name)).AppendLine(",");
        builder.Append("        InputSchemaJson = ").Append(Literal(tool.InputSchema)).AppendLine(",");
        builder.Append("        OutputSchemaJson = ").Append(Literal(tool.OutputSchema)).AppendLine(",");
        builder.Append("        SchemaHash = ").Append(Literal(tool.SchemaHash)).AppendLine(",");
        builder.Append("        ContractHash = ").Append(Literal(tool.ContractHash)).AppendLine(",");
        builder.Append("        Approval = (global::NewHeap.Platform.AI.NhAiApprovalRequirement)")
            .Append(tool.Approval).AppendLine(",");
        builder.Append("        Idempotency = (global::NewHeap.Platform.AI.NhAiIdempotencySupport)")
            .Append(tool.Idempotency).AppendLine(",");
        builder.Append("        ExportSchema = (global::NewHeap.Platform.AI.NhAiToolExportSchema)")
            .Append(tool.ExportSchema).AppendLine(",");
        builder.Append("        ReadOnlyHint = (global::NewHeap.Platform.AI.NhAiToolHint)")
            .Append(tool.ReadOnlyHint).AppendLine(",");
        builder.Append("        DestructiveHint = (global::NewHeap.Platform.AI.NhAiToolHint)")
            .Append(tool.DestructiveHint).AppendLine(",");
        builder.Append("        IdempotentHint = (global::NewHeap.Platform.AI.NhAiToolHint)")
            .Append(tool.IdempotentHint).AppendLine(",");
        builder.Append("        OpenWorldHint = (global::NewHeap.Platform.AI.NhAiToolHint)")
            .Append(tool.OpenWorldHint).AppendLine(",");
        if (tool.VerifierId is not null)
        {
            builder.Append("        VerifierId = ").Append(Literal(tool.VerifierId)).AppendLine(",");
        }
        builder.Append("        Timeout = global::System.TimeSpan.FromSeconds(")
            .Append(tool.TimeoutSeconds).AppendLine("),");
        builder.Append("        MaxConcurrency = ").Append(tool.MaxConcurrency).AppendLine(",");
        builder.Append("        MaxInputBytes = ").Append(tool.MaxInputBytes).AppendLine(",");
        builder.Append("        MaxResultBytes = ").Append(tool.MaxResultBytes).AppendLine(",");
        builder.Append("        DataClassification = (global::NewHeap.Platform.AI.NhAiDataClassification)")
            .Append(tool.DataClassification).AppendLine(",");
        builder.Append("        RetentionCategory = (global::NewHeap.Platform.AI.NhAiRetentionCategory)")
            .Append(tool.RetentionCategory).AppendLine(",");
        builder.AppendLine("        RequiredCapabilities = new global::System.String[]");
        builder.AppendLine("        {");
        foreach (var capability in tool.RequiredCapabilities)
        {
            builder.Append("            ").Append(Literal(capability)).AppendLine(",");
        }
        builder.AppendLine("        }");
        builder.AppendLine("    };");
        builder.AppendLine();
    }

    private static bool TryGetSignature(
        IMethodSymbol method,
        out ITypeSymbol? inputTypeSymbol,
        out ITypeSymbol? outputTypeSymbol,
        out string? inputType,
        out string? outputType)
    {
        inputTypeSymbol = null;
        outputTypeSymbol = null;
        inputType = null;
        outputType = null;
        if (method.DeclaredAccessibility != Accessibility.Public
            || method.IsStatic
            || method.TypeParameters.Length != 0
            || method.Parameters.Length != 3
            || method.Parameters[1].Type.ToDisplayString() != InvocationContextName
            || method.Parameters[2].Type.ToDisplayString() != CancellationTokenName)
        {
            return false;
        }

        if (method.ReturnType is not INamedTypeSymbol taskType
            || taskType.Name != "Task"
            || taskType.ContainingNamespace.ToDisplayString() != "System.Threading.Tasks"
            || taskType.TypeArguments.Length != 1
            || taskType.TypeArguments[0] is not INamedTypeSymbol taskResultType
            || taskResultType.Name != "TaskResult"
            || taskResultType.ContainingNamespace.ToDisplayString() != "NewHeap.Platform.Common.Models"
            || taskResultType.TypeArguments.Length != 1)
        {
            return false;
        }

        inputTypeSymbol = method.Parameters[0].Type;
        outputTypeSymbol = taskResultType.TypeArguments[0];
        inputType = inputTypeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        outputType = outputTypeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return true;
    }

    private static AttributeData? FindAttribute(ISymbol symbol, string metadataName)
    {
        return symbol.GetAttributes().FirstOrDefault(
            attribute => attribute.AttributeClass?.ToDisplayString() == metadataName);
    }

    private static int GetNamedInt(
        AttributeData attribute,
        string name,
        int defaultValue)
    {
        var value = attribute.NamedArguments.FirstOrDefault(argument => argument.Key == name);
        return value.Key is null ? defaultValue : (int)(value.Value.Value ?? defaultValue);
    }

    private static string? GetNamedString(AttributeData attribute, string name)
    {
        var value = attribute.NamedArguments.FirstOrDefault(argument => argument.Key == name);
        return value.Key is null ? null : value.Value.Value as string;
    }

    private static string? GetNamedType(AttributeData attribute, string name)
    {
        var argument = attribute.NamedArguments
            .FirstOrDefault(candidate => candidate.Key == name);
        return argument.Key is not null && argument.Value.Value is INamedTypeSymbol type
            ? type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            : null;
    }

    private static IEnumerable<string> GetNamedStringArray(
        AttributeData attribute,
        string name)
    {
        var value = attribute.NamedArguments.FirstOrDefault(argument => argument.Key == name);
        if (value.Key is null || value.Value.Kind != TypedConstantKind.Array)
        {
            return Enumerable.Empty<string>();
        }
        return value.Value.Values
            .Select(item => item.Value as string)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!);
    }

    private static bool IsDashCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value[0] == '-' || value[value.Length - 1] == '-')
        {
            return false;
        }

        var previousWasDash = false;
        foreach (var character in value)
        {
            if (character == '-')
            {
                if (previousWasDash)
                {
                    return false;
                }
                previousWasDash = true;
                continue;
            }

            if ((character < 'a' || character > 'z') && (character < '0' || character > '9'))
            {
                return false;
            }
            previousWasDash = false;
        }

        return true;
    }

    private static string FunctionName(string setId, string toolId, int version)
    {
        return (setId + "_" + toolId).Replace('-', '_') + "_v" + version;
    }

    private static bool IsExportName(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 128
            || global::System.Text.RegularExpressions.Regex.IsMatch(
                value,
                "[.-]v[0-9]+$",
                global::System.Text.RegularExpressions.RegexOptions.CultureInvariant))
        {
            return false;
        }

        var previousWasSeparator = false;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            var separator = character is '-' or '.';
            if (separator && (index == 0 || index == value.Length - 1 || previousWasSeparator))
            {
                return false;
            }
            if (!separator
                && (character < 'a' || character > 'z')
                && (character < '0' || character > '9'))
            {
                return false;
            }
            previousWasSeparator = separator;
        }

        return true;
    }

    private static string Literal(string value)
    {
        return SymbolDisplay.FormatLiteral(value, true);
    }

    private static string ComputeHash(string value)
    {
        using (var sha256 = SHA256.Create())
        {
            var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(value));
            var builder = new StringBuilder(bytes.Length * 2);
            foreach (var item in bytes)
            {
                builder.Append(item.ToString("x2"));
            }
            return builder.ToString();
        }
    }

    private enum SchemaDirection
    {
        Input = 0,
        Output = 1
    }

    /// <summary>
    /// Mirrors <c>System.Text.Json.Serialization.JsonIgnoreCondition</c>.
    /// </summary>
    private static class IgnoreCondition
    {
        public const int Never = 0;
        public const int Always = 1;
        public const int WhenWritingDefault = 2;
        public const int WhenWritingNull = 3;
        public const int WhenWriting = 4;
        public const int WhenReading = 5;
    }

    /// <summary>
    /// Mirrors <c>System.Text.Json.Serialization.JsonKnownNamingPolicy</c>.
    /// </summary>
    private static class NamingPolicy
    {
        public const int Unspecified = 0;
        public const int CamelCase = 1;
        public const int SnakeCaseLower = 2;
        public const int SnakeCaseUpper = 3;
        public const int KebabCaseLower = 4;
        public const int KebabCaseUpper = 5;
    }

    /// <summary>
    /// The serializer contract the generated <c>AIFunction</c> uses at runtime. The schema
    /// must describe exactly what that contract reads and writes.
    /// </summary>
    private sealed class SerializerContract
    {
        private const string SourceGenerationOptionsName =
            "System.Text.Json.Serialization.JsonSourceGenerationOptionsAttribute";
        private const int WebDefaults = 1;

        private SerializerContract(int namingPolicy, int defaultIgnoreCondition, bool stringEnums)
        {
            NamingPolicy = namingPolicy;
            DefaultIgnoreCondition = defaultIgnoreCondition;
            StringEnums = stringEnums;
        }

        public int NamingPolicy { get; }

        public int DefaultIgnoreCondition { get; }

        public bool StringEnums { get; }

        /// <summary>
        /// A generated function without a declared context serializes with
        /// <c>AIJsonUtilities.DefaultOptions</c>: camelCase names, <c>WhenWritingNull</c>
        /// and <c>JsonStringEnumConverter</c>.
        /// </summary>
        public static SerializerContract MicrosoftExtensionsAIDefaults { get; } = new(
            NhAiToolGenerator.NamingPolicy.CamelCase,
            IgnoreCondition.WhenWritingNull,
            true);

        /// <summary>
        /// A flat export without a declared context uses
        /// <c>NhAiToolJsonSerializerOptions.FlatExport</c>: camelCase names, every property
        /// written including nulls, and string enums.
        /// </summary>
        public static SerializerContract FlatExportDefaults { get; } = new(
            NhAiToolGenerator.NamingPolicy.CamelCase,
            IgnoreCondition.Never,
            true);

        public static SerializerContract FromContext(INamedTypeSymbol? contextType)
        {
            if (contextType is null)
            {
                return MicrosoftExtensionsAIDefaults;
            }

            var options = FindAttribute(contextType, SourceGenerationOptionsName);
            if (options is null)
            {
                return new SerializerContract(
                    NhAiToolGenerator.NamingPolicy.Unspecified,
                    IgnoreCondition.Never,
                    false);
            }

            var useWebDefaults = options.ConstructorArguments.Length > 0
                && options.ConstructorArguments[0].Value is int defaults
                && defaults == WebDefaults;
            var namingPolicy = GetNamedInt(
                options,
                "PropertyNamingPolicy",
                NhAiToolGenerator.NamingPolicy.Unspecified);
            if (namingPolicy == NhAiToolGenerator.NamingPolicy.Unspecified && useWebDefaults)
            {
                namingPolicy = NhAiToolGenerator.NamingPolicy.CamelCase;
            }

            var ignoreCondition = GetNamedInt(
                options,
                "DefaultIgnoreCondition",
                IgnoreCondition.Never);
            var stringEnums = GetNamedBool(options, "UseStringEnumConverter")
                || GetNamedTypeArray(options, "Converters").Any(IsStringEnumConverter);
            return new SerializerContract(namingPolicy, ignoreCondition, stringEnums);
        }
    }

    /// <summary>
    /// Mirrors <c>NhAiToolAnnotationHints.FindWideningOverride</c>: an explicit hint may only make
    /// the published annotations more cautious than the effect implies.
    /// Effect 0 = ReadOnly, 1 = IdempotentMutation, 3 = ExternalSideEffect, 4 = Destructive;
    /// hint 1 = True, 2 = False.
    /// </summary>
    private static string? FindWideningHint(
        int effect,
        int readOnlyHint,
        int destructiveHint,
        int idempotentHint,
        int openWorldHint)
    {
        if (readOnlyHint == 1 && effect != 0)
        {
            return "ReadOnlyHint = True";
        }
        if (destructiveHint == 2 && effect == 4)
        {
            return "DestructiveHint = False";
        }
        if (idempotentHint == 1 && effect != 0 && effect != 1)
        {
            return "IdempotentHint = True";
        }
        if (openWorldHint == 2 && effect == 3)
        {
            return "OpenWorldHint = False";
        }
        return null;
    }

    private static bool GetNamedBool(AttributeData attribute, string name)
    {
        var value = attribute.NamedArguments.FirstOrDefault(argument => argument.Key == name);
        return value.Key is not null && value.Value.Value is bool flag && flag;
    }

    private static INamedTypeSymbol? GetNamedTypeSymbol(AttributeData attribute, string name)
    {
        var argument = attribute.NamedArguments
            .FirstOrDefault(candidate => candidate.Key == name);
        return argument.Key is not null
            ? argument.Value.Value as INamedTypeSymbol
            : null;
    }

    private static IEnumerable<ITypeSymbol> GetNamedTypeArray(AttributeData attribute, string name)
    {
        var value = attribute.NamedArguments.FirstOrDefault(argument => argument.Key == name);
        if (value.Key is null || value.Value.Kind != TypedConstantKind.Array)
        {
            return Enumerable.Empty<ITypeSymbol>();
        }
        return value.Value.Values
            .Select(item => item.Value as ITypeSymbol)
            .Where(item => item is not null)
            .Select(item => item!)
            .ToArray();
    }

    private static bool IsStringEnumConverter(ITypeSymbol? converterType)
    {
        if (converterType is not INamedTypeSymbol named)
        {
            return false;
        }
        var definition = named.OriginalDefinition.ToDisplayString();
        return definition == "System.Text.Json.Serialization.JsonStringEnumConverter"
            || definition == "System.Text.Json.Serialization.JsonStringEnumConverter<TEnum>";
    }

    private static class SchemaWriter
    {
        private const int MaxDepth = 16;
        private const string JsonIgnoreName = "System.Text.Json.Serialization.JsonIgnoreAttribute";
        private const string JsonConverterName = "System.Text.Json.Serialization.JsonConverterAttribute";
        private const string JsonPropertyNameName = "System.Text.Json.Serialization.JsonPropertyNameAttribute";
        private const string JsonStringEnumMemberNameName =
            "System.Text.Json.Serialization.JsonStringEnumMemberNameAttribute";

        public static string Create(
            ITypeSymbol type,
            SerializerContract contract,
            SchemaDirection direction)
        {
            return Write(
                type,
                contract,
                direction,
                new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default),
                0,
                false);
        }

        private static string Write(
            ITypeSymbol type,
            SerializerContract contract,
            SchemaDirection direction,
            HashSet<ITypeSymbol> visiting,
            int depth,
            bool stringEnumConverter)
        {
            var isNullable = type.NullableAnnotation == NullableAnnotation.Annotated;
            if (type is INamedTypeSymbol nullable
                && nullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            {
                type = nullable.TypeArguments[0];
                isNullable = true;
            }

            var schema = WriteNonNullable(type, contract, direction, visiting, depth, stringEnumConverter);
            return isNullable
                ? "{\"anyOf\":[" + schema + ",{\"type\":\"null\"}]}"
                : schema;
        }

        private static string WriteNonNullable(
            ITypeSymbol type,
            SerializerContract contract,
            SchemaDirection direction,
            HashSet<ITypeSymbol> visiting,
            int depth,
            bool stringEnumConverter)
        {
            if (depth > MaxDepth)
            {
                return "{\"type\":\"object\"}";
            }

            switch (type.SpecialType)
            {
                case SpecialType.System_String:
                case SpecialType.System_Char:
                    return "{\"type\":\"string\"}";
                case SpecialType.System_Boolean:
                    return "{\"type\":\"boolean\"}";
                case SpecialType.System_Byte:
                case SpecialType.System_SByte:
                case SpecialType.System_Int16:
                case SpecialType.System_UInt16:
                case SpecialType.System_Int32:
                case SpecialType.System_UInt32:
                case SpecialType.System_Int64:
                case SpecialType.System_UInt64:
                    return "{\"type\":\"integer\"}";
                case SpecialType.System_Decimal:
                case SpecialType.System_Double:
                case SpecialType.System_Single:
                    return "{\"type\":\"number\"}";
                case SpecialType.System_DateTime:
                    return "{\"type\":\"string\",\"format\":\"date-time\"}";
                case SpecialType.System_Object:
                    return "{}";
            }

            var displayName = type.ToDisplayString();
            if (displayName == "System.Guid")
            {
                return "{\"type\":\"string\",\"format\":\"uuid\"}";
            }
            if (displayName == "System.DateTimeOffset")
            {
                return "{\"type\":\"string\",\"format\":\"date-time\"}";
            }
            if (displayName == "System.TimeSpan")
            {
                return "{\"type\":\"string\",\"format\":\"duration\"}";
            }
            if (type.TypeKind == TypeKind.Enum)
            {
                var serializesAsString = stringEnumConverter
                    || contract.StringEnums
                    || HasStringEnumConverter(type);
                return serializesAsString
                    ? WriteStringEnum((INamedTypeSymbol)type)
                    : "{\"type\":\"integer\"}";
            }
            if (type is IArrayTypeSymbol array)
            {
                return "{\"type\":\"array\",\"items\":"
                    + Write(array.ElementType, contract, direction, visiting, depth + 1, false)
                    + "}";
            }

            var dictionaryValue = FindDictionaryValue(type);
            if (dictionaryValue is not null)
            {
                return "{\"type\":\"object\",\"additionalProperties\":"
                    + Write(dictionaryValue, contract, direction, visiting, depth + 1, false)
                    + "}";
            }

            var enumerableItem = FindEnumerableItem(type);
            if (enumerableItem is not null)
            {
                return "{\"type\":\"array\",\"items\":"
                    + Write(enumerableItem, contract, direction, visiting, depth + 1, false)
                    + "}";
            }

            if (type is not INamedTypeSymbol namedType || !visiting.Add(type))
            {
                return "{\"type\":\"object\"}";
            }

            var properties = GetSerializableProperties(namedType, direction)
                .Select(property => new
                {
                    Name = GetJsonName(property.Symbol, contract),
                    Schema = Write(
                        property.Symbol.Type,
                        contract,
                        direction,
                        visiting,
                        depth + 1,
                        HasStringEnumConverter(property.Symbol)),
                    Required = IsRequired(
                        property.Symbol,
                        property.IgnoreCondition,
                        contract,
                        direction)
                })
                .OrderBy(property => property.Name, StringComparer.Ordinal)
                .ToArray();
            visiting.Remove(type);

            var builder = new StringBuilder();
            builder.Append("{\"type\":\"object\",\"properties\":{");
            for (var index = 0; index < properties.Length; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }
                builder.Append(JsonString(properties[index].Name))
                    .Append(':')
                    .Append(properties[index].Schema);
            }
            builder.Append('}');

            var required = properties.Where(property => property.Required).ToArray();
            if (required.Length > 0)
            {
                builder.Append(",\"required\":[");
                for (var index = 0; index < required.Length; index++)
                {
                    if (index > 0)
                    {
                        builder.Append(',');
                    }
                    builder.Append(JsonString(required[index].Name));
                }
                builder.Append(']');
            }
            builder.Append(",\"additionalProperties\":false}");
            return builder.ToString();
        }

        private static string WriteStringEnum(INamedTypeSymbol enumType)
        {
            var isFlags = enumType.GetAttributes().Any(attribute =>
                attribute.AttributeClass?.ToDisplayString() == "System.FlagsAttribute");
            if (isFlags)
            {
                // A flags value serializes as a comma-separated combination of member names.
                return "{\"type\":\"string\"}";
            }

            var names = enumType.GetMembers()
                .OfType<IFieldSymbol>()
                .Where(field => field.HasConstantValue)
                .Select(field =>
                {
                    var memberName = FindAttribute(field, JsonStringEnumMemberNameName);
                    return memberName?.ConstructorArguments.FirstOrDefault().Value as string
                        ?? field.Name;
                })
                .ToArray();

            var builder = new StringBuilder("{\"type\":\"string\",\"enum\":[");
            for (var index = 0; index < names.Length; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }
                builder.Append(JsonString(names[index]));
            }
            builder.Append("]}");
            return builder.ToString();
        }

        private static IEnumerable<(IPropertySymbol Symbol, int? IgnoreCondition)> GetSerializableProperties(
            INamedTypeSymbol type,
            SchemaDirection direction)
        {
            for (var current = type; current is not null; current = current.BaseType)
            {
                foreach (var property in current.GetMembers().OfType<IPropertySymbol>())
                {
                    if (property.IsStatic
                        || property.IsIndexer
                        || property.DeclaredAccessibility != Accessibility.Public
                        || property.GetMethod is null)
                    {
                        continue;
                    }

                    var ignoreCondition = GetIgnoreCondition(property);
                    if (ignoreCondition == IgnoreCondition.Always
                        || (direction == SchemaDirection.Input
                            && ignoreCondition == IgnoreCondition.WhenReading)
                        || (direction == SchemaDirection.Output
                            && ignoreCondition == IgnoreCondition.WhenWriting))
                    {
                        continue;
                    }

                    yield return (property, ignoreCondition);
                }
            }
        }

        private static int? GetIgnoreCondition(IPropertySymbol property)
        {
            var ignore = FindAttribute(property, JsonIgnoreName);
            if (ignore is null)
            {
                return null;
            }

            // JsonIgnore without an explicit condition ignores the property entirely.
            return GetNamedInt(ignore, "Condition", IgnoreCondition.Always);
        }

        private static bool HasStringEnumConverter(ISymbol symbol)
        {
            var converter = FindAttribute(symbol, JsonConverterName);
            return converter is not null
                && IsStringEnumConverter(
                    converter.ConstructorArguments.FirstOrDefault().Value as ITypeSymbol);
        }

        private static string GetJsonName(IPropertySymbol property, SerializerContract contract)
        {
            var nameAttribute = FindAttribute(property, JsonPropertyNameName);
            var explicitName = nameAttribute is null
                ? null
                : nameAttribute.ConstructorArguments.FirstOrDefault().Value as string;
            if (!string.IsNullOrWhiteSpace(explicitName))
            {
                return explicitName!;
            }

            switch (contract.NamingPolicy)
            {
                case NhAiToolGenerator.NamingPolicy.CamelCase:
                    return JsonNames.ToCamelCase(property.Name);
                case NhAiToolGenerator.NamingPolicy.SnakeCaseLower:
                    return JsonNames.ToSeparated(property.Name, '_', true);
                case NhAiToolGenerator.NamingPolicy.SnakeCaseUpper:
                    return JsonNames.ToSeparated(property.Name, '_', false);
                case NhAiToolGenerator.NamingPolicy.KebabCaseLower:
                    return JsonNames.ToSeparated(property.Name, '-', true);
                case NhAiToolGenerator.NamingPolicy.KebabCaseUpper:
                    return JsonNames.ToSeparated(property.Name, '-', false);
                default:
                    return property.Name;
            }
        }

        private static bool IsRequired(
            IPropertySymbol property,
            int? ignoreCondition,
            SerializerContract contract,
            SchemaDirection direction)
        {
            var isNullableValue = property.Type is INamedTypeSymbol nullable
                && nullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;
            var declaredNonNull = !isNullableValue
                && (property.Type.IsValueType
                    || property.NullableAnnotation == NullableAnnotation.NotAnnotated);
            if (direction == SchemaDirection.Input)
            {
                return declaredNonNull;
            }

            // A property-level condition replaces the serializer's default ignore condition.
            var writeCondition = ignoreCondition ?? contract.DefaultIgnoreCondition;
            switch (writeCondition)
            {
                case IgnoreCondition.Never:
                case IgnoreCondition.WhenReading:
                    return true;
                case IgnoreCondition.WhenWritingDefault:
                    return declaredNonNull && !property.Type.IsValueType;
                default:
                    return declaredNonNull;
            }
        }

        private static ITypeSymbol? FindDictionaryValue(ITypeSymbol type)
        {
            return FindGenericType(
                    type,
                    "System.Collections.Generic.IDictionary<TKey, TValue>",
                    "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>")
                ?.TypeArguments[1];
        }

        private static ITypeSymbol? FindEnumerableItem(ITypeSymbol type)
        {
            return FindGenericType(
                    type,
                    "System.Collections.Generic.IEnumerable<T>")
                ?.TypeArguments[0];
        }

        private static INamedTypeSymbol? FindGenericType(
            ITypeSymbol type,
            params string[] definitions)
        {
            var candidates = type is INamedTypeSymbol named
                ? named.AllInterfaces.Concat(new[] { named })
                : Enumerable.Empty<INamedTypeSymbol>();
            return candidates.FirstOrDefault(candidate => definitions.Contains(
                candidate.OriginalDefinition.ToDisplayString(),
                StringComparer.Ordinal));
        }

        private static string JsonString(string value)
        {
            var builder = new StringBuilder(value.Length + 2);
            builder.Append('"');
            foreach (var character in value)
            {
                switch (character)
                {
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '\b':
                        builder.Append("\\b");
                        break;
                    case '\f':
                        builder.Append("\\f");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    default:
                        if (character < 0x20)
                        {
                            builder.Append("\\u").Append(((int)character).ToString("x4"));
                        }
                        else
                        {
                            builder.Append(character);
                        }
                        break;
                }
            }
            return builder.Append('"').ToString();
        }
    }

    /// <summary>
    /// Compile-time copies of the System.Text.Json known naming policies, so generated schema
    /// property names match the names the runtime serializer writes.
    /// </summary>
    private static class JsonNames
    {
        private const int NotStarted = 0;
        private const int UppercaseLetter = 1;
        private const int LowercaseLetterOrDigit = 2;
        private const int SpaceSeparator = 3;

        public static string ToCamelCase(string name)
        {
            if (string.IsNullOrEmpty(name) || !char.IsUpper(name[0]))
            {
                return name;
            }

            var chars = name.ToCharArray();
            for (var index = 0; index < chars.Length; index++)
            {
                if (index == 1 && !char.IsUpper(chars[index]))
                {
                    break;
                }

                var hasNext = index + 1 < chars.Length;
                if (index > 0 && hasNext && !char.IsUpper(chars[index + 1]))
                {
                    if (chars[index + 1] == ' ')
                    {
                        chars[index] = char.ToLowerInvariant(chars[index]);
                    }
                    break;
                }

                chars[index] = char.ToLowerInvariant(chars[index]);
            }
            return new string(chars);
        }

        public static string ToSeparated(string name, char separator, bool lowercase)
        {
            if (string.IsNullOrEmpty(name))
            {
                return name;
            }

            var state = NotStarted;
            var builder = new StringBuilder(name.Length + 8);
            for (var index = 0; index < name.Length; index++)
            {
                var current = name[index];
                var category = char.GetUnicodeCategory(current);
                switch (category)
                {
                    case UnicodeCategory.UppercaseLetter:
                        if (state == LowercaseLetterOrDigit || state == SpaceSeparator)
                        {
                            builder.Append(separator);
                        }
                        else if (state == UppercaseLetter
                            && index + 1 < name.Length
                            && char.IsLower(name[index + 1]))
                        {
                            builder.Append(separator);
                        }
                        builder.Append(lowercase ? char.ToLowerInvariant(current) : current);
                        state = UppercaseLetter;
                        break;
                    case UnicodeCategory.LowercaseLetter:
                    case UnicodeCategory.DecimalDigitNumber:
                        if (state == SpaceSeparator)
                        {
                            builder.Append(separator);
                        }
                        if (!lowercase && category == UnicodeCategory.LowercaseLetter)
                        {
                            current = char.ToUpperInvariant(current);
                        }
                        builder.Append(current);
                        state = LowercaseLetterOrDigit;
                        break;
                    case UnicodeCategory.SpaceSeparator:
                        if (state != NotStarted)
                        {
                            state = SpaceSeparator;
                        }
                        break;
                    default:
                        builder.Append(current);
                        state = NotStarted;
                        break;
                }
            }
            return builder.ToString();
        }
    }

    private sealed class ToolModel
    {
        public ToolModel(
            IMethodSymbol method,
            string setId,
            string toolId,
            string? explicitExportName,
            int version,
            int effect,
            int exposure,
            string description,
            string inputType,
            string outputType,
            string? jsonSerializerContextType,
            string inputSchema,
            string outputSchema,
            string schemaHash,
            string contractHash,
            int approval,
            int idempotency,
            int exportSchema,
            bool usesFlatExportDefaults,
            int readOnlyHint,
            int destructiveHint,
            int idempotentHint,
            int openWorldHint,
            string? verifierId,
            int timeoutSeconds,
            int maxConcurrency,
            int maxInputBytes,
            int maxResultBytes,
            int dataClassification,
            int retentionCategory,
            ImmutableArray<string> requiredCapabilities,
            bool requiresAuthorization,
            ImmutableArray<string?> authorizationPolicies)
        {
            Method = method;
            SetId = setId;
            ToolId = toolId;
            ExportName = explicitExportName ?? FunctionName(setId, toolId, version);
            Version = version;
            Effect = effect;
            Exposure = exposure;
            Description = description;
            InputType = inputType;
            OutputType = outputType;
            JsonSerializerContextType = jsonSerializerContextType;
            InputSchema = inputSchema;
            OutputSchema = outputSchema;
            SchemaHash = schemaHash;
            ContractHash = contractHash;
            Approval = approval;
            Idempotency = idempotency;
            ExportSchema = exportSchema;
            UsesFlatExportDefaults = usesFlatExportDefaults;
            ReadOnlyHint = readOnlyHint;
            DestructiveHint = destructiveHint;
            IdempotentHint = idempotentHint;
            OpenWorldHint = openWorldHint;
            VerifierId = verifierId;
            TimeoutSeconds = timeoutSeconds;
            MaxConcurrency = maxConcurrency;
            MaxInputBytes = maxInputBytes;
            MaxResultBytes = maxResultBytes;
            DataClassification = dataClassification;
            RetentionCategory = retentionCategory;
            RequiredCapabilities = requiredCapabilities;
            RequiresAuthorization = requiresAuthorization;
            AuthorizationPolicies = authorizationPolicies;
        }

        public IMethodSymbol Method { get; }
        public string SetId { get; }
        public string ToolId { get; }
        public string LogicalId => SetId + "." + ToolId;
        public string ExportName { get; }
        public int Version { get; }
        public int Effect { get; }
        public int Exposure { get; }
        public string Description { get; }
        public string InputType { get; }
        public string OutputType { get; }
        public string? JsonSerializerContextType { get; }
        public string InputSchema { get; }
        public string OutputSchema { get; }
        public string SchemaHash { get; }
        public string ContractHash { get; }
        public int Approval { get; }
        public int Idempotency { get; }
        public int ExportSchema { get; }
        public bool UsesFlatExportDefaults { get; }
        public int ReadOnlyHint { get; }
        public int DestructiveHint { get; }
        public int IdempotentHint { get; }
        public int OpenWorldHint { get; }
        public string? VerifierId { get; }
        public int TimeoutSeconds { get; }
        public int MaxConcurrency { get; }
        public int MaxInputBytes { get; }
        public int MaxResultBytes { get; }
        public int DataClassification { get; }
        public int RetentionCategory { get; }
        public ImmutableArray<string> RequiredCapabilities { get; }
        public bool RequiresAuthorization { get; }
        public ImmutableArray<string?> AuthorizationPolicies { get; }
    }
}
