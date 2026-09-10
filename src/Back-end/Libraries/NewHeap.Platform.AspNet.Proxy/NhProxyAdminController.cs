using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Options;
using NewHeap.Platform.Common.Attributes;

namespace NewHeap.Platform.AspNet.Proxy;

public sealed class NhProxyRedirectEditorModel
{
    [Filterable] public Guid Id { get; set; }
    [Range(0, long.MaxValue)] public long Revision { get; set; }
    public bool IsNew { get; set; }
    [Required, StringLength(200)] public string Name { get; set; } = "";
    [Required, StringLength(2048)] public string Path { get; set; } = "";
    public bool IsRegex { get; set; }
    [Required, StringLength(4096)] public string Target { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public int Priority { get; set; }
    public NhProxyRedirectStatus Status { get; set; } = NhProxyRedirectStatus.Found;
    public NhProxyRedirectQueryMode QueryMode { get; set; }
    [StringLength(2048)] public string? Hosts { get; set; }
    [StringLength(512)] public string? Methods { get; set; }
    [StringLength(4096)] public string? TestUrl { get; set; }
    [StringLength(32)] public string TestMethod { get; set; } = "GET";
    [BindNever] public string? TestResult { get; set; }

    internal NhProxyRedirectRule ToRule() => new()
    {
        Id = Id, Name = Name, Enabled = Enabled, Priority = Priority, Status = Status, QueryMode = QueryMode, Target = Target,
        Match = new NhProxyRedirectMatch
        {
            Path = Path, PathMode = IsRegex ? NhProxyRedirectPathMatchMode.Regex : NhProxyRedirectPathMatchMode.Exact,
            Hosts = Split(Hosts), Methods = Split(Methods)
        }
    };

    private static ImmutableArray<string> Split(string? value) => (value ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToImmutableArray();
}

public sealed record NhProxyRedirectListModel(NhProxyRedirectConfiguration Configuration, NhProxyEngineStatus Status, string? Search);
public sealed record NhProxyRedirectDeleteModel(NhProxyRedirectRule Rule, long Revision);

/// <summary>Embedded MVC administration for managed proxy rules. The reserved pipeline branch owns access checks.</summary>
[Area("NewHeapProxy")]
[Authorize(Policy = NhProxyOptions.AdministrationPolicy)]
[AutoValidateAntiforgeryToken]
[RequestSizeLimit(65536)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
[ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
public sealed partial class NhProxyAdminController(INhProxyConfigurationService configuration, INhProxyAdministrationService administration,
    INhProxyConfigurationValidator validator, INhProxyLoginAuditStore audit, IOptions<NhProxyOptions> options) : Controller
{
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        if (!HttpContext.Items.ContainsKey(typeof(NhProxyAdminController)))
        {
            context.Result = NotFound();
        }
    }

    [HttpGet, AllowAnonymous]
    [EndpointSummary("Open administrator sign-in")]
    [EndpointDescription("Displays the dedicated NewHeap Proxy login form.")]
    [ProducesResponseType(typeof(NhProxyLoginRequest), StatusCodes.Status200OK)]
    public IActionResult Login() => View();

    [HttpPost, AllowAnonymous]
    [EndpointSummary("Sign in to NewHeap Proxy")]
    [EndpointDescription("Checks credentials, audits the attempt and issues a dedicated administration session.")]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Login([FromForm] NhProxyLoginRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            Response.StatusCode = 400;
            ViewData["Error"] = "Enter your username and password.";
            return View();
        }

        var result = await administration.SignInAsync(HttpContext, request, cancellationToken);
        if (!result.Success)
        {
            Response.StatusCode = result.GetResultItems().Any(item => item.Name == NhProxyErrorCodes.Throttled) ? 429 : 401;
            ViewData["Error"] = Response.StatusCode == 429 ? "Too many login attempts. Please wait before trying again." : "Sign-in failed. Check your username and password.";
            return View();
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [EndpointSummary("Sign out of NewHeap Proxy")]
    [EndpointDescription("Removes the scoped administrator session cookie.")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        await administration.SignOutAsync(HttpContext, cancellationToken);
        return RedirectToAction(nameof(Login));
    }

    [HttpGet]
    [EndpointSummary("List redirects")]
    [EndpointDescription("Shows persisted redirect rules, their priority and the active configuration revision.")]
    [ProducesResponseType(typeof(NhProxyRedirectListModel), StatusCodes.Status200OK)]
    public async Task<IActionResult> Index([FromQuery] string? search, CancellationToken cancellationToken)
    {
        return View(new NhProxyRedirectListModel(await configuration.GetRedirectsAsync(cancellationToken), configuration.GetStatus().Redirect, search));
    }

    [HttpPost]
    [EndpointSummary("Test a URL against saved proxy rules")]
    [EndpointDescription("Previews a synthetic GET request against saved redirects, then rewrites. A path starting with / uses the administration request's scheme, host and port. Identifies the selected rule without contacting the destination. Requires an administrator session and antiforgery token.")]
    [ProducesResponseType(typeof(NhProxyRuleTestResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> TestUrl([FromForm, Required, StringLength(4096)] string? url,
        [FromServices] INhProxyDraftTester tester, CancellationToken cancellationToken)
    {
        ViewData["TestUrl"] = url;
        var inputUrl = url;
        if (url?.StartsWith('/') == true && !url.StartsWith("//", StringComparison.Ordinal) && !url.Contains('\\'))
        {
            inputUrl = $"{Request.Scheme}://{Request.Host.ToUriComponent()}{url}";
        }

        if (!ModelState.IsValid || !Uri.TryCreate(inputUrl, UriKind.Absolute, out var requestUrl)
            || requestUrl.Scheme is not ("http" or "https") || requestUrl.UserInfo.Length != 0 || requestUrl.Fragment.Length != 0)
        {
            Response.StatusCode = 400;
            ViewData["Error"] = "Enter a path starting with / or a complete HTTP(S) URL, without credentials or a fragment.";
            return View();
        }

        ViewData["RequestUrl"] = requestUrl.AbsoluteUri;
        var rewrites = await configuration.GetRewritesAsync(cancellationToken);
        var redirects = await configuration.GetRedirectsAsync(cancellationToken);
        var tested = await tester.TestSavedAsync(new(rewrites.Revision, redirects.Revision), new() { Url = requestUrl }, cancellationToken);
        if (!tested.Success)
        {
            var conflict = tested.GetResultItems().Any(item => item.Name == NhProxyErrorCodes.RevisionConflict);
            Response.StatusCode = conflict ? 409 : 400;
            ViewData["Error"] = conflict ? "The saved rules changed during the test. Test the URL again."
                : tested.GetResultItems().Any(item => item.Name == NhProxyRuntime.ChainDepthFailure)
                    ? $"Request refused: the managed rule chain exceeds the maximum depth of {options.Value.Limits.MaximumChainDepth}. Check the matched rules for a loop or shorten the chain."
                : "The URL could not be tested. Check for overlapping rewrite matches and invalid rules, then try again.";
            return View();
        }

        var result = tested.Data!;
        ViewData["RuleName"] = result.Outcome == NhProxyTestOutcome.Redirect
            ? redirects.Rules.FirstOrDefault(rule => rule.Id == result.SelectedRuleId)?.Name
            : rewrites.Rules.FirstOrDefault(rule => rule.Id == result.SelectedRuleId)?.Name;
        ViewData["RulePath"] = result.Outcome == NhProxyTestOutcome.Redirect
            ? redirects.Rules.FirstOrDefault(rule => rule.Id == result.SelectedRuleId)?.Match.Path
            : rewrites.Rules.FirstOrDefault(rule => rule.Id == result.SelectedRuleId)?.Match.Path;
        return View(result);
    }

    [HttpGet]
    [EndpointSummary("Edit a redirect")]
    [EndpointDescription("Loads a redirect or prepares a new exact rule with optional regex matching and its current configuration revision.")]
    [ProducesResponseType(typeof(NhProxyRedirectEditorModel), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Edit([FromRoute] Guid? id, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return NotFound();
        }

        var snapshot = await configuration.GetRedirectsAsync(cancellationToken);
        if (id is null)
        {
            return View(new NhProxyRedirectEditorModel { Id = Guid.NewGuid(), IsNew = true, Revision = snapshot.Revision });
        }

        var rule = snapshot.Rules.FirstOrDefault(rule => rule.Id == id);
        if (rule is null)
        {
            return NotFound();
        }

        return View(new NhProxyRedirectEditorModel
        {
            Id = rule.Id, Revision = snapshot.Revision, Name = rule.Name, Path = rule.Match.Path, Target = rule.Target,
            Enabled = rule.Enabled, Priority = rule.Priority, Status = rule.Status, QueryMode = rule.QueryMode,
            IsRegex = rule.Match.PathMode == NhProxyRedirectPathMatchMode.Regex,
            Hosts = string.Join(", ", rule.Match.Hosts), Methods = string.Join(", ", rule.Match.Methods)
        });
    }

    [HttpPost]
    [EndpointSummary("Save or test a redirect")]
    [EndpointDescription("Tests an exact or regex draft locally, including capture substitutions, or commits and activates a revision-checked change.")]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(NhProxyRedirectEditorModel), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Edit([FromForm] NhProxyRedirectEditorModel model, [FromForm] string? operation, CancellationToken cancellationToken)
    {
        if (operation is not ("save" or "test"))
        {
            return BadRequest("Choose save or test.");
        }

        if (!model.IsRegex && model.Path?.StartsWith('/') == true && new PathString(model.Path).StartsWithSegments(NhProxyOptions.AdministrationPath, StringComparison.OrdinalIgnoreCase))
        {
            ModelState.AddModelError(nameof(model.Path), "The administration path is reserved and cannot be redirected.");
        }

        if (!ModelState.IsValid)
        {
            Response.StatusCode = 400;
            return View(model);
        }

        var rule = model.ToRule();
        var validation = await validator.ValidateRedirectsAsync(new(model.Revision, [rule]), cancellationToken);
        if (!validation.Success)
        {
            ModelState.AddModelError("", model.IsRegex
                ? "The rule is invalid. Use a valid .NET regex and a safe HTTP(S) or root-relative target with capture substitutions. Host restrictions must be literal hosts; methods must be HTTP tokens."
                : "The rule is invalid. Use a literal path starting with / and a safe HTTP(S) or root-relative target. Host restrictions must be literal hosts; methods must be HTTP tokens.");
            Response.StatusCode = 400;
            return View(model);
        }

        if (operation == "test")
        {
            if (!Uri.TryCreate(model.TestUrl, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https")
                || string.IsNullOrWhiteSpace(model.TestMethod) || model.TestMethod.Any(character => !char.IsAsciiLetter(character)))
            {
                ModelState.AddModelError(nameof(model.TestUrl), "Enter an absolute HTTP(S) request URL and a valid method.");
                Response.StatusCode = 400;
                return View(model);
            }

            var preview = new NhProxyRuntime(validator, options);
            var published = await preview.PublishRedirectsAsync(new NhProxyRedirectConfiguration { Rules = [rule] }, cancellationToken);
            if (!published.Success)
            {
                ModelState.AddModelError("", "This draft could not be tested.");
                Response.StatusCode = 400;
                return View(model);
            }

            var request = new DefaultHttpContext();
            request.Request.Path = PathString.FromUriComponent(url);
            request.Request.Host = HostString.FromUriComponent(url);
            request.Request.QueryString = new QueryString(url.Query);
            request.Request.Method = model.TestMethod;
            request.Request.Scheme = url.Scheme;
            var chainFailure = await preview.CheckChainAsync(request);
            if (chainFailure == NhProxyRuntime.ChainDepthFailure)
            {
                model.TestResult = $"Request refused: the rule chain exceeds the maximum depth of {options.Value.Limits.MaximumChainDepth}.";
                return View(model);
            }

            model.TestResult = preview.TryRedirect(request, out var failure)
                ? $"Match: {request.Response.StatusCode} → {request.Response.Headers.Location}"
                : failure ?? "No match. This draft would pass the request to the next middleware.";
            return View(model);
        }

        var snapshot = await configuration.GetRedirectsAsync(cancellationToken);
        var exists = snapshot.Rules.Any(existing => existing.Id == model.Id);
        if (snapshot.Revision != model.Revision || exists == model.IsNew)
        {
            return ConflictView(model);
        }

        var rules = model.IsNew ? snapshot.Rules.Add(rule) : snapshot.Rules.Select(existing => existing.Id == rule.Id ? rule : existing).ToImmutableArray();
        var saved = await configuration.SaveRedirectsAsync(new(model.Revision, rules), cancellationToken);
        if (!saved.Success)
        {
            if (saved.Data is not null)
            {
                return RedirectToAction(nameof(Index));
            }

            if (saved.GetResultItems().Any(item => item.Name == NhProxyErrorCodes.RevisionConflict))
            {
                return ConflictView(model);
            }

            Response.StatusCode = 400;
            ModelState.AddModelError("", "The rule could not be saved. Check the configured rule limit and the rule values.");
            return View(model);
        }

        return RedirectToAction(nameof(Index));
    }

    private IActionResult ConflictView(NhProxyRedirectEditorModel model)
    {
        Response.StatusCode = 409;
        ModelState.AddModelError("", "The configuration changed or the rule could not be saved. Reload the rules and review your changes before saving again.");
        return View("Edit", model);
    }

    [HttpGet]
    [EndpointSummary("Confirm redirect deletion")]
    [EndpointDescription("Displays the rule and the configuration revision that will be deleted.")]
    [ProducesResponseType(typeof(NhProxyRedirectDeleteModel), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete([FromRoute] Guid id, CancellationToken cancellationToken)
    {
        var snapshot = await configuration.GetRedirectsAsync(cancellationToken);
        var rule = snapshot.Rules.FirstOrDefault(rule => rule.Id == id);
        return rule is null ? NotFound() : View(new NhProxyRedirectDeleteModel(rule, snapshot.Revision));
    }

    [HttpPost, ActionName(nameof(Delete))]
    [EndpointSummary("Delete a redirect")]
    [EndpointDescription("Deletes a rule only when the submitted configuration revision is still current.")]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteConfirmed([FromRoute] Guid id, [FromForm] long revision, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid || id == Guid.Empty || revision < 0)
        {
            return BadRequest("A valid rule and revision are required.");
        }

        var snapshot = await configuration.GetRedirectsAsync(cancellationToken);
        if (snapshot.Revision != revision)
        {
            return Conflict("The configuration changed. Reload before deleting.");
        }

        var saved = await configuration.SaveRedirectsAsync(new(revision, snapshot.Rules.Where(rule => rule.Id != id).ToImmutableArray()), cancellationToken);
        return saved.Success || saved.Data is not null ? RedirectToAction(nameof(Index)) : Conflict("The rule could not be deleted. Reload before trying again.");
    }

    [HttpPost]
    [EndpointSummary("Retry redirect activation")]
    [EndpointDescription("Reloads the saved snapshot and retries activation without changing persisted rules.")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    public async Task<IActionResult> Activate(CancellationToken cancellationToken)
    {
        var result = await configuration.RetryActivationAsync(NhProxyEngine.Redirect, cancellationToken);
        if (!result.Success)
        {
            return StatusCode(503, "The saved configuration could not be activated. Please try again.");
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    [EndpointSummary("View login activity")]
    [EndpointDescription("Shows a page of persisted login outcomes and client IP addresses, newest first.")]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(NhProxyLoginAuditPage), StatusCodes.Status200OK)]
    public async Task<IActionResult> Activity([FromQuery] int offset, CancellationToken cancellationToken)
    {
        var pageSize = Math.Min(20, options.Value.LoginAudit.MaximumPageSize);
        ViewData["PageSize"] = pageSize;
        var result = await audit.QueryAsync(new NhProxyLoginAuditQuery { Offset = offset, PageSize = pageSize }, cancellationToken);
        return result.Success ? View(result.Data) : BadRequest("Invalid audit page.");
    }
}
