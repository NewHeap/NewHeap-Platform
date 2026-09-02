using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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
            var toolId = (string?)toolAttribute.ConstructorArguments[0].Value ?? string.Empty;
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
            var unsafeSideEffect = effect != 0 && idempotency != 2;
            var unsafeMutation = effect == 2 && approval != 1;
            var unsafeExternalEffect = effect == 3 && approval != 1;
            var unsafeDestructive = effect == 4
                && (approval != 1 || string.IsNullOrWhiteSpace(verifierId));
            if (unsafeSideEffect || unsafeMutation || unsafeExternalEffect || unsafeDestructive)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    UnsafeMutationContract,
                    location,
                    setId + "." + toolId,
                    effect));
                continue;
            }

            var inputSchema = SchemaWriter.Create(inputTypeSymbol!);
            var outputSchema = SchemaWriter.Create(outputTypeSymbol!);
            var schemaHash = ComputeHash(inputSchema + "\n" + outputSchema);
            var contractHash = ComputeHash(string.Join(
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
                schemaHash));

            tools.Add(new ToolModel(
                method,
                setId,
                toolId,
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
        foreach (var group in tools
            .Where(tool => !duplicateKeys.Contains(tool.LogicalId + "@" + tool.Version))
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
            .AppendLine(" : global::NewHeap.Platform.AI.INhAiToolCatalog");
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
                .Append(Literal(tool.ContractHash)).AppendLine("),");
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
            builder.Append("            Name = ").Append(Literal(FunctionName(tool))).AppendLine(",");
            builder.Append("            Description = ").Append(Literal(tool.Description));
            if (tool.JsonSerializerContextType is not null)
            {
                builder.AppendLine(",");
                builder.Append("            SerializerOptions = ")
                    .Append(tool.JsonSerializerContextType)
                    .AppendLine(".Default.Options");
            }
            else
            {
                builder.AppendLine();
            }
            builder.AppendLine("        })));");
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

    private static string FunctionName(ToolModel tool)
    {
        return (tool.SetId + "_" + tool.ToolId).Replace('-', '_') + "_v" + tool.Version;
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

    private static class SchemaWriter
    {
        private const int MaxDepth = 16;

        public static string Create(ITypeSymbol type)
        {
            return Write(type, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default), 0);
        }

        private static string Write(
            ITypeSymbol type,
            HashSet<ITypeSymbol> visiting,
            int depth)
        {
            var isNullable = type.NullableAnnotation == NullableAnnotation.Annotated;
            if (type is INamedTypeSymbol nullable
                && nullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            {
                type = nullable.TypeArguments[0];
                isNullable = true;
            }

            var schema = WriteNonNullable(type, visiting, depth);
            return isNullable
                ? "{\"anyOf\":[" + schema + ",{\"type\":\"null\"}]}"
                : schema;
        }

        private static string WriteNonNullable(
            ITypeSymbol type,
            HashSet<ITypeSymbol> visiting,
            int depth)
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
                return "{\"type\":\"integer\"}";
            }
            if (type is IArrayTypeSymbol array)
            {
                return "{\"type\":\"array\",\"items\":"
                    + Write(array.ElementType, visiting, depth + 1)
                    + "}";
            }

            var dictionaryValue = FindDictionaryValue(type);
            if (dictionaryValue is not null)
            {
                return "{\"type\":\"object\",\"additionalProperties\":"
                    + Write(dictionaryValue, visiting, depth + 1)
                    + "}";
            }

            var enumerableItem = FindEnumerableItem(type);
            if (enumerableItem is not null)
            {
                return "{\"type\":\"array\",\"items\":"
                    + Write(enumerableItem, visiting, depth + 1)
                    + "}";
            }

            if (type is not INamedTypeSymbol namedType || !visiting.Add(type))
            {
                return "{\"type\":\"object\"}";
            }

            var properties = GetSerializableProperties(namedType)
                .Select(property => new
                {
                    Name = GetJsonName(property),
                    Symbol = property,
                    Schema = Write(property.Type, visiting, depth + 1),
                    Required = IsRequired(property)
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

        private static IEnumerable<IPropertySymbol> GetSerializableProperties(
            INamedTypeSymbol type)
        {
            for (var current = type; current is not null; current = current.BaseType)
            {
                foreach (var property in current.GetMembers().OfType<IPropertySymbol>())
                {
                    if (!property.IsStatic
                        && !property.IsIndexer
                        && property.DeclaredAccessibility == Accessibility.Public
                        && property.GetMethod is not null
                        && !HasJsonIgnore(property))
                    {
                        yield return property;
                    }
                }
            }
        }

        private static bool HasJsonIgnore(IPropertySymbol property)
        {
            return property.GetAttributes().Any(attribute =>
                attribute.AttributeClass?.ToDisplayString()
                    == "System.Text.Json.Serialization.JsonIgnoreAttribute");
        }

        private static string GetJsonName(IPropertySymbol property)
        {
            var nameAttribute = property.GetAttributes().FirstOrDefault(attribute =>
                attribute.AttributeClass?.ToDisplayString()
                    == "System.Text.Json.Serialization.JsonPropertyNameAttribute");
            var explicitName = nameAttribute is null
                ? null
                : nameAttribute.ConstructorArguments.FirstOrDefault().Value as string;
            if (!string.IsNullOrWhiteSpace(explicitName))
            {
                return explicitName!;
            }
            return property.Name.Length == 1
                ? property.Name.ToLowerInvariant()
                : char.ToLowerInvariant(property.Name[0]) + property.Name.Substring(1);
        }

        private static bool IsRequired(IPropertySymbol property)
        {
            if (property.Type is INamedTypeSymbol nullable
                && nullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            {
                return false;
            }
            return property.Type.IsValueType
                || property.NullableAnnotation == NullableAnnotation.NotAnnotated;
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

    private sealed class ToolModel
    {
        public ToolModel(
            IMethodSymbol method,
            string setId,
            string toolId,
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
