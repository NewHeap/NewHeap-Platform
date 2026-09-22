using System.Collections.Immutable;
using System.Text.Json;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AspNet.Proxy;

/// <summary>Builds editor drafts and revision-checked saves without owning HTTP responses or live publication.</summary>
internal sealed class NhProxyEditorService(INhProxyConfigurationService configuration, INhProxyConfigurationValidator validator)
{
    internal const string InvalidJson = "newheap-proxy.invalid-editor-json";
    internal const string InvalidTestInput = "newheap-proxy.invalid-editor-test-input";

    internal async Task<NhProxyRedirectEditorModel?> GetRedirectAsync(Guid? id, CancellationToken cancellationToken)
    {
        var snapshot = await configuration.GetRedirectsAsync(cancellationToken);
        if (id is null)
        {
            return new NhProxyRedirectEditorModel { Id = Guid.NewGuid(), IsNew = true, Revision = snapshot.Revision };
        }

        var rule = snapshot.Rules.FirstOrDefault(rule => rule.Id == id);
        return rule is null ? null : new NhProxyRedirectEditorModel
        {
            Id = rule.Id, Revision = snapshot.Revision, Name = rule.Name, Path = rule.Match.Path, Target = rule.Target,
            Enabled = rule.Enabled, Priority = rule.Priority, Status = rule.Status, QueryMode = rule.QueryMode,
            IsRegex = rule.Match.PathMode == NhProxyRedirectPathMatchMode.Regex,
            Hosts = string.Join(", ", rule.Match.Hosts), Methods = string.Join(", ", rule.Match.Methods)
        };
    }

    internal async Task<NhProxyRewriteEditorModel?> GetRewriteAsync(Guid? id, CancellationToken cancellationToken)
    {
        var snapshot = await configuration.GetRewritesAsync(cancellationToken);
        var redirects = await configuration.GetRedirectsAsync(cancellationToken);
        var rule = snapshot.Rules.FirstOrDefault(rule => rule.Id == id);
        if (id is not null && rule is null)
        {
            return null;
        }

        var cluster = rule is null ? new NhProxyCluster
        {
            Id = Guid.NewGuid(), Name = "Backend", Destination = new("backend", new("https://backend.example/"))
        } : snapshot.Clusters.Single(cluster => cluster.Id == rule.ClusterId);
        rule ??= new NhProxyRewriteRule { Id = Guid.NewGuid(), Name = "", ClusterId = cluster.Id, Match = new() { Path = "/{**rest}" } };
        var model = NhProxyRewriteEditorModel.FromRule(rule, cluster, snapshot.Revision, redirects.Revision);
        model.IsNew = id is null;
        model.AvailableClusters = snapshot.Clusters;
        if (model.IsNew)
        {
            model.DestinationAddress = "";
        }

        return model;
    }

    internal async Task<TaskResult<NhProxySaveResult>> SaveRedirectAsync(
        NhProxyRedirectEditorModel model, NhProxyRedirectRule rule, CancellationToken cancellationToken)
    {
        var snapshot = await configuration.GetRedirectsAsync(cancellationToken);
        if (snapshot.Revision != model.Revision || snapshot.Rules.Any(existing => existing.Id == model.Id) == model.IsNew)
        {
            return TaskResult<NhProxySaveResult>.Failed(NhProxyErrorCodes.RevisionConflict, NhProxyErrorCodes.RevisionConflict);
        }

        var rules = model.IsNew ? snapshot.Rules.Add(rule)
            : snapshot.Rules.Select(existing => existing.Id == rule.Id ? rule : existing).ToImmutableArray();
        return await configuration.SaveRedirectsAsync(new(model.Revision, rules), cancellationToken);
    }

    internal async Task<TaskResult<NhProxyRewriteSaveRequest>> PrepareRewriteAsync(
        NhProxyRewriteEditorModel model, NhProxyRewriteConfiguration snapshot, CancellationToken cancellationToken)
    {
        NhProxyRewriteRule rule;
        NhProxyCluster cluster;
        try
        {
            rule = model.ToRule();
            cluster = model.ToCluster();
        }
        catch (Exception exception) when (exception is JsonException or UriFormatException or ArgumentException)
        {
            return TaskResult<NhProxyRewriteSaveRequest>.Failed(NhProxyErrorCodes.Validation, InvalidJson);
        }

        var request = new NhProxyRewriteSaveRequest(model.Revision,
            snapshot.Rules.Where(existing => existing.Id != rule.Id).Append(rule).ToImmutableArray(),
            snapshot.Clusters.Where(existing => existing.Id != cluster.Id).Append(cluster).ToImmutableArray());
        var validation = await validator.ValidateRewritesAsync(request, cancellationToken);
        if (!validation.Success)
        {
            return TaskResult<NhProxyRewriteSaveRequest>.Failed(validation);
        }

        return TaskResult<NhProxyRewriteSaveRequest>.Succeeded(request);
    }

    internal static async Task<TaskResult<NhProxyRuleTestResult>> TestRewriteAsync(NhProxyRewriteEditorModel model,
        NhProxyRewriteSaveRequest draft, INhProxyDraftTester tester, CancellationToken cancellationToken)
    {
        NhProxyTestRequest input;
        try
        {
            input = new()
            {
                Url = new(model.TestUrl ?? "", UriKind.Absolute), Method = model.TestMethod,
                Headers = JsonSerializer.Deserialize<ImmutableDictionary<string, ImmutableArray<string>>>(model.TestHeadersJson)
                    ?? throw new JsonException()
            };
        }
        catch (Exception exception) when (exception is UriFormatException or JsonException)
        {
            return TaskResult<NhProxyRuleTestResult>.Failed(NhProxyErrorCodes.Validation, InvalidTestInput);
        }

        return await tester.TestRewriteAsync(new(new(model.Revision, model.RedirectRevision),
            draft.Rules.Single(rule => rule.Id == model.Id), input)
        {
            DraftClusters = draft.Clusters, SimulateEnabled = model.SimulateEnabled
        }, cancellationToken);
    }

    internal async Task<TaskResult<NhProxySaveResult>> DeleteRedirectAsync(Guid id, long revision, CancellationToken cancellationToken)
    {
        var snapshot = await configuration.GetRedirectsAsync(cancellationToken);
        if (snapshot.Revision != revision)
        {
            return TaskResult<NhProxySaveResult>.Failed(NhProxyErrorCodes.RevisionConflict, NhProxyErrorCodes.RevisionConflict);
        }

        return await configuration.SaveRedirectsAsync(new(revision,
            snapshot.Rules.Where(rule => rule.Id != id).ToImmutableArray()), cancellationToken);
    }

    internal async Task<TaskResult<NhProxySaveResult>> DeleteRewriteAsync(Guid id, long revision, CancellationToken cancellationToken)
    {
        var snapshot = await configuration.GetRewritesAsync(cancellationToken);
        return await configuration.SaveRewritesAsync(new(revision,
            snapshot.Rules.Where(rule => rule.Id != id).ToImmutableArray(), snapshot.Clusters), cancellationToken);
    }
}
