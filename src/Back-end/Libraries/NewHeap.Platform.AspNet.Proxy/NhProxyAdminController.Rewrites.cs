using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace NewHeap.Platform.AspNet.Proxy;

public sealed partial class NhProxyAdminController
{
    [HttpGet]
    [EndpointSummary("List managed rewrites")]
    [EndpointDescription("Shows saved rewrite rules, destinations and independently confirmed YARP activation.")]
    [ProducesResponseType(typeof(NhProxyRewriteListModel), StatusCodes.Status200OK)]
    public async Task<IActionResult> Rewrites([FromQuery] string? search, CancellationToken cancellationToken)
    {
        return View(new NhProxyRewriteListModel(await configuration.GetRewritesAsync(cancellationToken), configuration.GetStatus().Rewrite, search));
    }

    [HttpGet]
    [EndpointSummary("Edit a managed rewrite")]
    [EndpointDescription("Loads a stored rule or prepares a draft with its rewrite and redirect revisions. Native configuration sources are outside this editor.")]
    [ProducesResponseType(typeof(NhProxyRewriteEditorModel), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RewriteEdit([FromRoute] Guid? id, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return NotFound();
        }

        var snapshot = await configuration.GetRewritesAsync(cancellationToken);
        var redirects = await configuration.GetRedirectsAsync(cancellationToken);
        var rule = snapshot.Rules.FirstOrDefault(rule => rule.Id == id);
        if (id is not null && rule is null)
        {
            return NotFound();
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

        return View(model);
    }

    [HttpPost]
    [EndpointSummary("Save or test a managed rewrite")]
    [EndpointDescription("Tests a revision-checked candidate with saved redirects and rewrites without outbound requests, or commits and activates the rewrite configuration. Shared destination edits affect every referencing rule.")]
    [ProducesResponseType(typeof(NhProxyRewriteEditorModel), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RewriteEdit([FromForm] NhProxyRewriteEditorModel model, [FromForm] string? operation,
        [FromServices] INhProxyDraftTester tester, CancellationToken cancellationToken)
    {
        if (operation is not ("save" or "test" or "cluster"))
        {
            return BadRequest("Choose save, test or load destination.");
        }

        var snapshot = await configuration.GetRewritesAsync(cancellationToken);
        model.AvailableClusters = snapshot.Clusters;
        if (operation == "cluster")
        {
            var selected = snapshot.Clusters.FirstOrDefault(cluster => cluster.Id == model.SelectedClusterId);
            if (selected is not null)
            {
                model.ClusterId = selected.Id;
                model.ClusterName = selected.Name;
                model.DestinationAddress = selected.Destination.Address.AbsoluteUri;
                model.AdvancedClusterJson = JsonSerializer.Serialize(selected, NhProxyRewriteEditorModel.JsonOptions);
                ModelState.Clear();
            }

            return View(model);
        }

        if (snapshot.Revision != model.Revision || snapshot.Rules.Any(rule => rule.Id == model.Id) == model.IsNew)
        {
            return RewriteError(model, 409, "The configuration changed. Reload this editor and review your draft before saving or testing again.");
        }

        if (!ModelState.IsValid)
        {
            return RewriteError(model, 400, "Review the highlighted fields.");
        }

        if (model.SelectedClusterId is { } selectedId && selectedId != model.ClusterId)
        {
            return RewriteError(model, 400, "Load the selected destination before saving or testing its settings.");
        }

        NhProxyRewriteRule rule;
        NhProxyCluster cluster;
        try
        {
            rule = model.ToRule();
            cluster = model.ToCluster();
        }
        catch (Exception exception) when (exception is JsonException or UriFormatException or ArgumentException)
        {
            return RewriteError(model, 400, "Enter an absolute HTTP(S) destination and valid advanced configuration. Unknown fields are not supported.");
        }

        var rules = snapshot.Rules.Where(existing => existing.Id != rule.Id).Append(rule).ToImmutableArray();
        var clusters = snapshot.Clusters.Where(existing => existing.Id != cluster.Id).Append(cluster).ToImmutableArray();
        var validation = await validator.ValidateRewritesAsync(new(model.Revision, rules, clusters), cancellationToken);
        if (!validation.Success)
        {
            return RewriteError(model, 400, "The rewrite is invalid. Check the route template, destination restrictions, transforms and registered policy names.");
        }

        if (operation == "test")
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
                return RewriteError(model, 400, "Enter an absolute request URL and headers as a JSON object of string arrays.");
            }

            var tested = await tester.TestRewriteAsync(new(new(model.Revision, model.RedirectRevision), rule, input)
            {
                DraftClusters = clusters, SimulateEnabled = model.SimulateEnabled
            }, cancellationToken);
            if (!tested.Success)
            {
                var stale = tested.GetResultItems().Any(item => item.Name == NhProxyErrorCodes.RevisionConflict);
                return RewriteError(model, stale ? 409 : 400, stale
                    ? "The saved rules changed. Reload this editor before testing again."
                    : tested.GetResultItems().Any(item => item.Name == NhProxyRuntime.ChainDepthFailure)
                        ? $"Request refused: the managed rule chain exceeds the maximum depth of {options.Value.Limits.MaximumChainDepth}. Check for a loop or shorten the chain."
                    : "The draft could not be evaluated. Check request values and overlapping routes. Credential headers are not accepted; complex matches may exceed the test budget.");
            }

            model.TestResult = tested.Data;
            return View(model);
        }

        var saved = await configuration.SaveRewritesAsync(new(model.Revision, rules, clusters), cancellationToken);
        if (!saved.Success && saved.Data is null)
        {
            return RewriteError(model, 409, "The configuration could not be saved. Reload and review your changes.");
        }

        return RedirectToAction(nameof(Rewrites));
    }

    private IActionResult RewriteError(NhProxyRewriteEditorModel model, int status, string message)
    {
        Response.StatusCode = status;
        ModelState.AddModelError("", message);
        return View("RewriteEdit", model);
    }

    [HttpGet]
    [EndpointSummary("Confirm rewrite deletion")]
    [EndpointDescription("Displays the stored rewrite to remove. Shared destinations are retained.")]
    [ProducesResponseType(typeof(NhProxyRewriteDeleteModel), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RewriteDelete([FromRoute] Guid id, CancellationToken cancellationToken)
    {
        var snapshot = await configuration.GetRewritesAsync(cancellationToken);
        var rule = snapshot.Rules.FirstOrDefault(rule => rule.Id == id);
        return rule is null ? NotFound() : View(new NhProxyRewriteDeleteModel(rule, snapshot.Revision));
    }

    [HttpPost, ActionName(nameof(RewriteDelete))]
    [EndpointSummary("Delete a managed rewrite")]
    [EndpointDescription("Removes a rewrite with revision protection and activates the remaining rules. Destinations are retained for reuse.")]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RewriteDeleteConfirmed([FromRoute] Guid id, [FromForm] long revision, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid || id == Guid.Empty || revision < 0)
        {
            return BadRequest("A valid rule and revision are required.");
        }

        var snapshot = await configuration.GetRewritesAsync(cancellationToken);
        var saved = await configuration.SaveRewritesAsync(new(revision, snapshot.Rules.Where(rule => rule.Id != id).ToImmutableArray(), snapshot.Clusters), cancellationToken);
        return saved.Success || saved.Data is not null ? RedirectToAction(nameof(Rewrites)) : Conflict("The configuration changed. Reload before deleting.");
    }

    [HttpPost]
    [EndpointSummary("Retry managed rewrite activation")]
    [EndpointDescription("Publishes the current stored rewrite revision and waits for YARP confirmation without modifying storage or redirects.")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    public async Task<IActionResult> RewriteActivate(CancellationToken cancellationToken)
    {
        var activated = await configuration.RetryActivationAsync(NhProxyEngine.Rewrite, cancellationToken);
        return activated.Success ? RedirectToAction(nameof(Rewrites)) : StatusCode(503, "The saved rewrites could not be activated. Review the host logs and retry.");
    }
}
