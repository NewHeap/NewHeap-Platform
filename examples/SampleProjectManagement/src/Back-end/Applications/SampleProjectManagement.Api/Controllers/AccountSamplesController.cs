using NewHeap.Platform.Mapping;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using NewHeap.Platform.AspNet.Common.Controllers;
using NewHeap.Platform.AspNet.Common.Models;
using NewHeap.Platform.AspNet.Common.Models.Mutate;
using NewHeap.Platform.AspNet.Common.Models.ResponseTypes;
using NewHeap.Platform.AspNet.Common.Services;
using SampleProjectManagement.Api.Services;

namespace SampleProjectManagement.Api.Controllers;

[ApiController]
[Route("account-samples")]
public sealed class AccountSamplesController : ProtectedNhBaseController
{
    private readonly AccountSampleService _accountService;

    public AccountSamplesController(
        IMapper mapper,
        ILogger<AccountSamplesController> logger,
        IConfiguration config,
        IStringLocalizer<AccountSamplesController> localizer,
        IHttpCollectionProcessingService collectionProcessingService,
        AccountSampleService accountService)
        : base(mapper, logger, config, localizer, collectionProcessingService)
    {
        _accountService = accountService;
    }

    [HttpPut("active-division")]
    [Authorize]
    [EndpointSummary("Change the active division")]
    [EndpointDescription("Changes the authenticated user's active division and refreshes the division context used by authorization policies.")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ModelStateResponseType>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ChangeActiveDivision(
        [FromBody] ChangeActiveDivisionAccountModel model,
        CancellationToken cancellationToken)
    {
        var result = await _accountService.ChangeActiveDivisionAsync(
            UserId!.Value,
            model,
            cancellationToken);
        if (!result.Success)
        {
            result.ApplyToModelState(ModelState);
            return BadRequest(ModelState);
        }
        return Ok();
    }

    [HttpPost("password/change")]
    [Authorize]
    [EndpointSummary("Change the current password")]
    [EndpointDescription("Changes the password of the authenticated local account after validating the current password.")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ModelStateResponseType>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ChangePassword(
        [FromBody] NhChangePasswordUserMutateModel model,
        CancellationToken cancellationToken)
    {
        var result = await _accountService.ChangePasswordAsync(
            UserId!.Value,
            model,
            cancellationToken);
        if (!result.Success)
        {
            result.ApplyToModelState(ModelState);
            return BadRequest(ModelState);
        }
        return Ok();
    }

    [HttpPost("password/recover")]
    [AllowAnonymous]
    [EndpointSummary("Start password recovery")]
    [EndpointDescription("Starts the password recovery flow without revealing whether the supplied account exists.")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ModelStateResponseType>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RecoverPassword(
        [FromBody] NhRecoverPasswordUserMutateModel model,
        CancellationToken cancellationToken)
    {
        await _accountService.RecoverPasswordAsync(model, cancellationToken);
        return Accepted();
    }

    [HttpPost("password/reset")]
    [AllowAnonymous]
    [EndpointSummary("Reset a recovered password")]
    [EndpointDescription("Completes password recovery by validating the reset token and setting the new password.")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ModelStateResponseType>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ResetPassword(
        [FromBody] NhResetPasswordUserMutateModel model,
        CancellationToken cancellationToken)
    {
        var result = await _accountService.ResetPasswordAsync(
            model,
            cancellationToken);
        if (!result.Success)
        {
            result.ApplyToModelState(ModelState);
            return BadRequest(ModelState);
        }
        return Ok();
    }
}
