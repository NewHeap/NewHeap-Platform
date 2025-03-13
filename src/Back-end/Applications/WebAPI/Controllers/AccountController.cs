using AutoMapper;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NewHeap.Platform.AspNet.Common.Controllers;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models.View;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common;
using NewHeap.Platform.Common.Identity.Claims;
using NewHeap.Platform.Common.Models.Options;
using NewHeap.Platform.Common.Services;
using NewHeap.Platform.Common.Translations;
using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using WebAPI.Models.Mutate;

namespace WebAPI.Controllers;

[Route("api/[controller]")]
public class AccountController : PublicNhBaseController
{
    private readonly MailService _appMailManager;
    private readonly MicrosoftAuthService _appMicrosoftAuthManager;
    private readonly RazorViewService _appRazorViewManager;
    private readonly DbLogService _dbLogService;
    private readonly MicrosoftAuthSettings _microsoftAuthSettings;
    private readonly RoleManager<UserRole> _roleManager;
    private readonly IStringLocalizer<SharedResources> _sharedLocalizer;
    private readonly SignInManager<User> _signInManager;
    private readonly IRepository<User> _userRepository;
    private readonly NhUserManager _userManager;

    public AccountController(
        IStringLocalizer<SharedResources> sharedLocalizer,
        IStringLocalizer<AccountController> localizer,
        NhUserManager userManager,
        RoleManager<UserRole> roleManager,
        SignInManager<User> signInManager,
        ILogger<AccountController> logger,
        IRepository<User> appUserRepository,
        MailService appMailManager,
        RazorViewService appRazorViewManager,
        DbLogService dbLogService,
        IOptions<MicrosoftAuthSettings> microsoftAuthSettings,
        MicrosoftAuthService appMicrosoftAuthManager,
        IConfiguration config,
        IHttpCollectionProcessingService collectionRequestProcessingService,
        IMapper mapper)
        : base(mapper, logger, config, localizer, collectionRequestProcessingService)
    {
        _userManager = userManager;
        _sharedLocalizer = sharedLocalizer;
        _roleManager = roleManager;
        _signInManager = signInManager;
        _userRepository = appUserRepository;
        _appMailManager = appMailManager;
        _appRazorViewManager = appRazorViewManager;
        _microsoftAuthSettings = microsoftAuthSettings.Value;
        _appMicrosoftAuthManager = appMicrosoftAuthManager;
        _dbLogService = dbLogService;
    }

    [NonAction]
    private bool IsLockedOut(User user)
    {
        var lockoutEnd = user.LockoutEnd;
        if (lockoutEnd <= user.LockoutStart)
        {
            lockoutEnd = null;
        }

        var inLockOutPeriod = user.LockoutStart <= DateTimeOffset.Now &&
                              (lockoutEnd == null || lockoutEnd >= DateTimeOffset.Now);
        return inLockOutPeriod;
    }

    [AllowAnonymous]
    [HttpPost]
    [Route("Authorize")]
    public async Task<IActionResult> Authorize([FromBody] LoginAccountMutateModel model)
    {
        User user = null;
        if (ModelState.IsValid)
        {
            user = await _userManager.FindByEmailAsync(model.Email);
        }

        #region No user

        if (!ModelState.IsValid || user == null)
        {
            await _dbLogService.LogAsync(
                "Failed login attempt for user {0}.",
                messageArguments: new[] { model.Email },
                objectId: null,
                objectType: typeof(User).Name,
                objectTypeFull: typeof(User).FullName,
                userId: UserId,
                action: LogAction.Read,
                type: LogType.Warning,
                source: LogSource.Internal,
                tag: GetType().Name
            );
            return BadRequest(_localizer["Invalid username or password."]);
        }

        #endregion

        var result = await _signInManager.CheckPasswordSignInAsync(user, model.Password, true);

        if (!result.Succeeded && !result.IsLockedOut && !result.IsNotAllowed)
        {
            await _dbLogService.LogAsync(
                "Failed login attempt for user {0}.",
                messageArguments: new[] { model.Email },
                objectId: user.Id.ToString(),
                objectType: typeof(User).Name,
                objectTypeFull: typeof(User).FullName,
                userId: UserId,
                action: LogAction.Read,
                type: LogType.Warning,
                source: LogSource.Internal,
                tag: GetType().Name
            );

            return BadRequest(_localizer["Invalid username or password."]);
        }

        #region User locked out

        var inLockOutPeriod = IsLockedOut(user);

        if (result.IsLockedOut || inLockOutPeriod || result.IsNotAllowed)
        {
            await _dbLogService.LogAsync(
                "User login blocked {0}.",
                messageArguments: new[] { user.Id.ToString() },
                objectId: user.Id.ToString(),
                objectType: typeof(User).Name,
                objectTypeFull: typeof(User).FullName,
                userId: UserId,
                action: LogAction.Read,
                type: LogType.Warning,
                source: LogSource.Internal,
                tag: GetType().Name
            );

            return BadRequest(_localizer["Your account has been blocked."]);
        }

        #endregion

        #region Valid login

        if (result.Succeeded)
        {
            await _userManager.UpdateAsync(user);
            var claims = await _userManager.GetValidClaims(user);

            SymmetricSecurityKey key = new(Encoding.UTF8.GetBytes(_config["Tokens:Key"]));
            SigningCredentials creds = new(key, SecurityAlgorithms.HmacSha256);

            JwtSecurityToken token = new(_config["Tokens:Issuer"],
                _config["Tokens:Issuer"],
                claims,
                expires: model.RememberMe ? DateTime.Now.AddDays(14) : DateTime.Now.AddHours(10),
                signingCredentials: creds);

            await _dbLogService.LogAsync(
                "User login succesfull {0}.",
                messageArguments: new[] { user.Id.ToString() },
                objectId: user.Id.ToString(),
                objectType: typeof(User).Name,
                objectTypeFull: typeof(User).FullName,
                userId: user.Id,
                action: LogAction.Read,
                type: LogType.Information,
                source: LogSource.Internal,
                tag: GetType().Name
            );

            return Ok(new
            {
                token = new JwtSecurityTokenHandler().WriteToken(token), expires = token.ValidTo, refreshToken = ""
            });
        }

        #endregion

        await _dbLogService.LogAsync(
            "Invalid application state when authorizing.",
            objectId: user.Id.ToString(),
            objectType: typeof(User).Name,
            objectTypeFull: typeof(User).FullName,
            userId: UserId,
            action: LogAction.Read,
            type: LogType.Error,
            source: LogSource.Internal,
            tag: GetType().Name
        );

        return StatusCode(500);
    }

    [HttpGet]
    [Authorize]
    public async Task<IActionResult> GetAccount()
    {
        List<string> allowedClaimTypes = new()
        {
            ClaimTypes.Name,
            ClaimTypes.Email,
            ClaimTypes.NameIdentifier,
            ClaimTypes.Country,
            JwtRegisteredClaimNames.Email,
            JwtRegisteredClaimNames.NameId,
            ClaimTypes.Role,
            NhPlatformClaimTypes.Permission,
            NhPlatformClaimTypes.DivisionRole,
            NhPlatformClaimTypes.DivisionPermission
        };

        var user = await _userManager.FindByIdAsync(UserId.ToString());
        await _userRepository.Reference(user, x => x.ActiveDivision).LoadAsync();

        IQueryable<Division> divisionsQuery = _userRepository
            .GetDbSet<Division>();

        if (!User.HasClaim(x =>
                x.Type == NhPlatformClaimTypes.Permission &&
                x.Value == Constants.DivisionPermissionClaimValues.AccessAll))
        {
            divisionsQuery = divisionsQuery
                .Where(x => x.UserSelectAllowed)
                .Where(x => x.DivisionUsers.Any(c => c.UserId == UserId.Value));
        }

        var divisions = await divisionsQuery.OrderBy(x => x.Name).ToListAsync();

        //if (!user.ActiveDivisionId.HasValue)
        //{
        //    var division = divisions
        //        .FirstOrDefault();

        //    if (division != null)
        //    {
        //        user.ActiveDivisionId = division.Id;
        //        user.ActiveDivision = division;
        //        await _appUserRepository.SaveChangesAsync();
        //    }
        //}

        var claims = await _userManager.GetValidClaims(user, true);

        if (user.ActiveDivisionId.HasValue && !await _userManager.DivisionAccessAsync(user.ActiveDivisionId,
                claims,
                new List<Claim>
                {
                    new(NhPlatformClaimTypes.DivisionPermission,
                        Constants.DivisionPermissionClaimValues.GeneralView)
                }))
        {
            user.ActiveDivisionId = null;
            await _userRepository.SaveChangesAsync();
        }

        List<Division> removeDivisions = new();
        foreach (var division in divisions)
        {
            if (!await _userManager.DivisionAccessAsync(division.Id, claims,
                    new List<Claim>
                    {
                        new(NhPlatformClaimTypes.DivisionPermission,
                            Constants.DivisionPermissionClaimValues.GeneralView)
                    }))
            {
                removeDivisions.Add(division);
            }
        }

        if (removeDivisions.Any())
        {
            foreach (var removeDivision in removeDivisions)
            {
                divisions.Remove(removeDivision);
            }
        }

        ProfileAccountViewModel model = new()
        {
            User = _mapper.Map<UserViewModel>(user),
            Divisions = _mapper.Map<List<DivisionViewModel>>(divisions),
            Claims = _mapper.Map<List<ClaimViewModel>>(claims.Where(x => allowedClaimTypes.Contains(x.Type)))
        };

        return Ok(model);
    }
}