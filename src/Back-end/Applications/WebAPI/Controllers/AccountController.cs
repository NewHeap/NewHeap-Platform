using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using NewHeap.Platform.AspNet.Common.Controllers;
using NewHeap.Platform.AspNet.Common.Models;
using NewHeap.Platform.AspNet.Common.Services;
using System;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using NewHeap.Platform.AspNet.Common.Models.Mutate;

namespace WebAPI.Controllers;

[Route("[controller]")]
public class AccountController : ProtectedNhBaseController
{
    protected readonly NhUserManager _userManager;

    public AccountController(
        IStringLocalizer<AccountController> localizer,
        ILogger<AccountController> logger,
        IHttpCollectionProcessingService collectionRequestProcessingService,
        IConfiguration config,
        IMapper mapper,
        NhUserManager userManager
        )
        : base(mapper, logger, config, localizer, collectionRequestProcessingService)
    {
        _userManager = userManager;
    }

    [HttpPost("password/change")]
    public async Task<IActionResult> PostPasswordChange(
        [FromBody] NhChangePasswordUserMutateModel mutateModel,
        [FromServices] NhUserManager userManager,
        CancellationToken cancellationToken = default
        )
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var user = await _userManager.FindByIdAsync(UserId!.ToString()!);
        if (user == null)
        {
            return Unauthorized();
        }

        if (mutateModel.ConfirmPassword != mutateModel.Password)
        {
            ModelState.AddModelError(nameof(mutateModel.ConfirmPassword),"Passwords do not match.");
            return BadRequest(ModelState);
        }

        var result = await userManager.ChangePasswordAsync(user.Id, mutateModel, UserId, cancellationToken);
        
        if (!result.Success)
        {
            result.ApplyTo(ModelState);
            return BadRequest(ModelState);
        }

        return Ok();
    }
}