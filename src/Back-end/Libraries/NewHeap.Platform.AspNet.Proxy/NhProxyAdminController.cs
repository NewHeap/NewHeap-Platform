using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace NewHeap.Platform.AspNet.Proxy;

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
    private readonly NhProxyEditorService _editor = new(configuration, validator);

    public override void OnActionExecuting(ActionExecutingContext context)
    {
        if (!HttpContext.Items.ContainsKey(typeof(NhProxyAdminController)))
        {
            context.Result = NotFound();
        }
    }

    [HttpGet, AllowAnonymous]
    [EndpointSummary("Open administrator sign-in")]
    [EndpointDescription("Displays the built-in login form or starts the configured host login.")]
    [ProducesResponseType(typeof(NhProxyLoginRequest), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status302Found)]
    public IActionResult Login()
    {
        if (options.Value.AdministrationAuthentication is not null)
        {
            return Challenge(NhProxyHostAuthenticationHandler.AdministrationScheme);
        }

        return View();
    }

    [HttpPost, AllowAnonymous]
    [EndpointSummary("Sign in to NewHeap Proxy")]
    [EndpointDescription("Checks credentials, audits the attempt and issues a dedicated administration session.")]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Login([FromForm] NhProxyLoginRequest request, CancellationToken cancellationToken)
    {
        if (options.Value.AdministrationAuthentication is not null)
        {
            return NotFound();
        }

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
    [EndpointDescription("Ends the built-in session or invokes the configured host sign-out scheme.")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        if (options.Value.AdministrationAuthentication is not null)
        {
            return SignOut(new AuthenticationProperties { RedirectUri = Request.PathBase + "/Login" },
                NhProxyHostAuthenticationHandler.AdministrationScheme);
        }

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
    [EndpointDescription("Previews a synthetic GET request against saved redirects, then rewrites. A path starting with / uses the administration request's scheme, host and port. Shows the selected rule, mismatch reasons and skipped rules without contacting the destination. Requires an administrator session and antiforgery token.")]
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

        var model = await _editor.GetRedirectAsync(id, cancellationToken);
        return model is null ? NotFound() : View(model);
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

            var tested = await NhProxyDraftTester.TestIsolatedRedirectAsync(rule,
                new NhProxyTestRequest { Url = url, Method = model.TestMethod }, validator, options, cancellationToken);
            if (!tested.Success)
            {
                model.TestResult = tested.GetResultItems().Any(item => item.Name == NhProxyErrorCodes.MaximumChainDepth)
                    ? $"Request refused: the rule chain exceeds the maximum depth of {options.Value.Limits.MaximumChainDepth}."
                    : string.Join("; ", tested.AllErrorMessages);
            }
            else
            {
                model.TestResult = tested.Data is { } redirect
                    ? $"Match: {(int)redirect.Status} → {redirect.Location}"
                    : "No match. This draft would pass the request to the next middleware.";
            }
            return View(model);
        }

        var saved = await _editor.SaveRedirectAsync(model, rule, cancellationToken);
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

        var saved = await _editor.DeleteRedirectAsync(id, revision, cancellationToken);
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
