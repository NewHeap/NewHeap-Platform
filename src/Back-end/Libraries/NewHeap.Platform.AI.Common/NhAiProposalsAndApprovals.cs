using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NewHeap.Platform.AI;

public enum NhAiActorKind
{
    Human = 0,
    Service = 1,
    Agent = 2
}

public sealed record NhAiProposalTarget(
    string Type,
    string Id);

public sealed record NhAiActionBudget(
    int MaxCalls,
    decimal? MaxEstimatedCost = null);

public sealed record NhAiProposalCreateRequest(
    Guid ProposalId,
    string RunId,
    NhAiActorKind ActorKind,
    string ActorId,
    string AccountableOwnerId,
    NhAiToolDescriptor Tool,
    object Arguments,
    IReadOnlyList<NhAiProposalTarget> Targets,
    string Intent,
    IReadOnlyList<string> ExpectedEffects,
    IReadOnlyDictionary<string, string> Constraints,
    NhAiActionBudget EstimatedBudget,
    DateTimeOffset GeneratedAt,
    DateTimeOffset ExpiresAt)
{
    public string? ModelProfileName { get; init; }
    public string? PromptVersion { get; init; }
    public string? PromptHash { get; init; }
    public string? CatalogHash { get; init; }
    public string? ContextHash { get; init; }
}

public sealed record NhAiProposal(
    Guid ProposalId,
    string RunId,
    NhAiActorKind ActorKind,
    string ActorId,
    string AccountableOwnerId,
    string ToolId,
    int ToolVersion,
    string ToolContractHash,
    JsonElement Arguments,
    IReadOnlyList<NhAiProposalTarget> Targets,
    string Intent,
    IReadOnlyList<string> ExpectedEffects,
    IReadOnlyDictionary<string, string> Constraints,
    NhAiActionBudget EstimatedBudget,
    DateTimeOffset GeneratedAt,
    DateTimeOffset ExpiresAt,
    string? ModelProfileName,
    string? PromptVersion,
    string? CatalogHash,
    string? ContextHash,
    string ProposalHash)
{
    public string? PromptHash { get; init; }
}

public sealed record NhAiApproval(
    Guid ApprovalId,
    Guid ProposalId,
    string ProposalHash,
    string ApprovingActorId,
    IReadOnlyList<NhAiProposalTarget> AllowedTargets,
    IReadOnlyDictionary<string, string> Constraints,
    DateTimeOffset ApprovedAt,
    DateTimeOffset ExpiresAt,
    NhAiActionBudget? MaximumBudget = null);

public sealed record NhAiApprovalEvidence(
    NhAiProposal Proposal,
    NhAiApproval Approval);

public sealed record NhAiApprovalValidationResult(
    bool Succeeded,
    string Code);

public interface INhAiProposalFactory
{
    NhAiProposal Create(NhAiProposalCreateRequest request);

    string ComputeHash(NhAiProposal proposal);
}

public interface INhAiApprovalValidator
{
    NhAiApprovalValidationResult Validate(
        NhAiToolDescriptor descriptor,
        NhAiInvocationContext context,
        object arguments,
        NhAiApprovalEvidence evidence,
        DateTimeOffset now);
}

public interface INhAiApprovalEvidenceProvider
{
    ValueTask<NhAiApprovalEvidence?> GetAsync(
        NhAiToolDescriptor descriptor,
        NhAiInvocationContext context,
        object arguments,
        CancellationToken cancellationToken = default);
}

public sealed class NhAiProposalFactory : INhAiProposalFactory
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public NhAiProposal Create(NhAiProposalCreateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        var arguments = Canonicalize(JsonSerializer.SerializeToElement(
            request.Arguments,
            request.Arguments.GetType(),
            SerializerOptions));
        var proposal = new NhAiProposal(
            request.ProposalId,
            request.RunId,
            request.ActorKind,
            request.ActorId,
            request.AccountableOwnerId,
            request.Tool.Id,
            request.Tool.Version,
            request.Tool.ContractHash,
            arguments,
            NormalizeTargets(request.Targets),
            request.Intent,
            request.ExpectedEffects.OrderBy(item => item, StringComparer.Ordinal).ToArray(),
            new SortedDictionary<string, string>(
                request.Constraints.ToDictionary(
                    item => item.Key,
                    item => item.Value,
                    StringComparer.Ordinal),
                StringComparer.Ordinal),
            request.EstimatedBudget,
            request.GeneratedAt,
            request.ExpiresAt,
            request.ModelProfileName,
            request.PromptVersion,
            request.CatalogHash,
            request.ContextHash,
            string.Empty)
        {
            PromptHash = request.PromptHash
        };
        return proposal with { ProposalHash = ComputeHash(proposal) };
    }

    public string ComputeHash(NhAiProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        var material = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["accountableOwnerId"] = proposal.AccountableOwnerId,
            ["actorId"] = proposal.ActorId,
            ["actorKind"] = proposal.ActorKind,
            ["arguments"] = proposal.Arguments,
            ["catalogHash"] = proposal.CatalogHash,
            ["constraints"] = new SortedDictionary<string, string>(
                proposal.Constraints.ToDictionary(
                    item => item.Key,
                    item => item.Value,
                    StringComparer.Ordinal),
                StringComparer.Ordinal),
            ["contextHash"] = proposal.ContextHash,
            ["estimatedBudget"] = proposal.EstimatedBudget,
            ["expectedEffects"] = proposal.ExpectedEffects.OrderBy(item => item, StringComparer.Ordinal).ToArray(),
            ["expiresAt"] = proposal.ExpiresAt,
            ["generatedAt"] = proposal.GeneratedAt,
            ["intent"] = proposal.Intent,
            ["modelProfileName"] = proposal.ModelProfileName,
            ["promptVersion"] = proposal.PromptVersion,
            ["promptHash"] = proposal.PromptHash,
            ["proposalId"] = proposal.ProposalId,
            ["runId"] = proposal.RunId,
            ["targets"] = NormalizeTargets(proposal.Targets),
            ["toolContractHash"] = proposal.ToolContractHash,
            ["toolId"] = proposal.ToolId,
            ["toolVersion"] = proposal.ToolVersion
        };
        var element = Canonicalize(JsonSerializer.SerializeToElement(material, SerializerOptions));
        return Sha256(element.GetRawText());
    }

    internal static JsonElement Canonicalize(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonical(writer, element);
        }
        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject()
                    .OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static IReadOnlyList<NhAiProposalTarget> NormalizeTargets(
        IEnumerable<NhAiProposalTarget> targets)
    {
        return targets
            .OrderBy(target => target.Type, StringComparer.Ordinal)
            .ThenBy(target => target.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static void ValidateRequest(NhAiProposalCreateRequest request)
    {
        if (request.ProposalId == Guid.Empty)
        {
            throw new ArgumentException("Proposal ID is required.", nameof(request));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ActorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AccountableOwnerId);
        ArgumentNullException.ThrowIfNull(request.Arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Intent);
        if (request.Intent.Length > 512 || request.ExpiresAt <= request.GeneratedAt)
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }
        if (request.EstimatedBudget.MaxCalls < 1
            || request.EstimatedBudget.MaxEstimatedCost < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request.EstimatedBudget));
        }
    }

    private static string Sha256(string value)
    {
        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}

public sealed class NhAiApprovalValidator(
    INhAiProposalFactory proposalFactory) : INhAiApprovalValidator
{
    public NhAiApprovalValidationResult Validate(
        NhAiToolDescriptor descriptor,
        NhAiInvocationContext context,
        object arguments,
        NhAiApprovalEvidence evidence,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(evidence);
        var proposal = evidence.Proposal;
        var approval = evidence.Approval;

        if (proposal.ProposalId == Guid.Empty
            || approval.ApprovalId == Guid.Empty
            || string.IsNullOrWhiteSpace(approval.ApprovingActorId)
            || approval.ApprovingActorId.Length > 256)
        {
            return Invalid("approval-identity-invalid");
        }
        if (proposal.ProposalHash != proposalFactory.ComputeHash(proposal))
        {
            return Invalid("proposal-hash-invalid");
        }
        if (approval.ProposalId != proposal.ProposalId
            || approval.ProposalHash != proposal.ProposalHash)
        {
            return Invalid("approval-proposal-mismatch");
        }
        if (proposal.ToolId != descriptor.Id
            || proposal.ToolVersion != descriptor.Version
            || proposal.ToolContractHash != descriptor.ContractHash)
        {
            return Invalid("tool-contract-mismatch");
        }
        if (context.ProposalId != proposal.ProposalId.ToString()
            || context.ApprovalId != approval.ApprovalId.ToString())
        {
            return Invalid("invocation-evidence-mismatch");
        }
        if (!string.Equals(context.RunId, proposal.RunId, StringComparison.Ordinal))
        {
            return Invalid("run-mismatch");
        }
        if (proposal.ModelProfileName is not null
            && !string.Equals(
                context.ModelProfileName,
                proposal.ModelProfileName,
                StringComparison.Ordinal))
        {
            return Invalid("model-profile-mismatch");
        }
        if (proposal.PromptVersion is not null
            && !string.Equals(
                context.PromptVersion,
                proposal.PromptVersion,
                StringComparison.Ordinal))
        {
            return Invalid("prompt-version-mismatch");
        }
        if (proposal.PromptHash is not null
            && !string.Equals(
                context.PromptHash,
                proposal.PromptHash,
                StringComparison.Ordinal))
        {
            return Invalid("prompt-hash-mismatch");
        }
        if (proposal.CatalogHash is not null
            && !string.Equals(
                context.CatalogHash,
                proposal.CatalogHash,
                StringComparison.Ordinal))
        {
            return Invalid("catalog-hash-mismatch");
        }
        if (proposal.ContextHash is not null
            && !string.Equals(
                context.ContextHash,
                proposal.ContextHash,
                StringComparison.Ordinal))
        {
            return Invalid("context-hash-mismatch");
        }
        if (!string.Equals(context.ActorId, proposal.ActorId, StringComparison.Ordinal)
            || context.ActorKind != proposal.ActorKind
            || !string.Equals(
                context.AccountableOwnerId,
                proposal.AccountableOwnerId,
                StringComparison.Ordinal))
        {
            return Invalid("actor-or-owner-mismatch");
        }
        if (approval.ApprovedAt < proposal.GeneratedAt
            || approval.ApprovedAt > now
            || approval.ExpiresAt <= approval.ApprovedAt)
        {
            return Invalid("approval-time-invalid");
        }
        if (now > proposal.ExpiresAt || now > approval.ExpiresAt)
        {
            return Invalid("approval-expired");
        }
        if (proposal.ActorKind == NhAiActorKind.Agent
            && string.Equals(proposal.ActorId, approval.ApprovingActorId, StringComparison.Ordinal))
        {
            return Invalid("agent-self-approval-denied");
        }

        var invocationArguments = NhAiProposalFactory.Canonicalize(
            JsonSerializer.SerializeToElement(
                arguments,
                arguments.GetType(),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        if (!JsonElement.DeepEquals(proposal.Arguments, invocationArguments))
        {
            return Invalid("arguments-changed");
        }
        if (!proposal.Targets.All(target => approval.AllowedTargets.Contains(target)))
        {
            return Invalid("target-not-approved");
        }
        if (proposal.Constraints.Any(constraint =>
            !approval.Constraints.TryGetValue(constraint.Key, out var value)
            || !string.Equals(value, constraint.Value, StringComparison.Ordinal)))
        {
            return Invalid("constraint-not-approved");
        }
        if (approval.MaximumBudget is { } maximum
            && (maximum.MaxCalls < proposal.EstimatedBudget.MaxCalls
                || (maximum.MaxEstimatedCost.HasValue
                    && (!proposal.EstimatedBudget.MaxEstimatedCost.HasValue
                        || maximum.MaxEstimatedCost < proposal.EstimatedBudget.MaxEstimatedCost))))
        {
            return Invalid("budget-not-approved");
        }
        return new NhAiApprovalValidationResult(true, "approved");
    }

    private static NhAiApprovalValidationResult Invalid(string code)
    {
        return new NhAiApprovalValidationResult(false, code);
    }
}

internal sealed class NhAiDenyApprovalEvidenceProvider : INhAiApprovalEvidenceProvider
{
    public ValueTask<NhAiApprovalEvidence?> GetAsync(
        NhAiToolDescriptor descriptor,
        NhAiInvocationContext context,
        object arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<NhAiApprovalEvidence?>(null);
    }
}
