using AutoMapper;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models.View;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Identity.Claims;
using NewHeap.Platform.Common.Models;
using System.Security.Claims;
using HttpMethod = NewHeap.Platform.AspNet.Common.Builders.HttpMethod;

namespace NewHeap.Platform.AspNet.Common.Authentication;

/// <summary>
/// 
/// </summary>
public class NhAccountInformationEndpointHandler<
    TUser,
    TDivision,
    TDivisionUser,
    TDivisionRole,
    TDivisionUserRole,
    TDivisionRoleClaim,
    TUserViewModel,
    TDivisionViewModel,
    TClaimViewModel
    > : BaseNhAuthenticationEndpoint
    where TUser : NhUser<TDivision, TDivisionUser, TDivisionUserRole, TDivisionRole, TDivisionRoleClaim, TUser>
    where TDivision : NhDivision<TDivisionUser, TDivisionUserRole, TDivisionRole, TDivisionRoleClaim, TDivision, TUser>
    where TDivisionRole : NhDivisionRole<TDivisionUserRole, TDivisionRoleClaim, TDivisionUser, TDivisionRole, TDivision, TUser>
    where TDivisionUser : NhDivisionUser<TDivisionUserRole, TDivisionUser, TDivisionRole, TDivisionRoleClaim, TDivision, TUser>
    where TDivisionUserRole : NhDivisionUserRole<TDivisionUser, TDivisionRole, TDivisionRoleClaim, TDivisionUserRole, TDivision, TUser>
    where TDivisionRoleClaim : NhDivisionRoleClaim
    where TUserViewModel : NhUserViewModel<TDivisionViewModel>
    where TDivisionViewModel : NhDivisionViewModel
    where TClaimViewModel : NhClaimViewModel
{
    private readonly AuthenticationConfiguration _configuration;

    /// <summary>
    /// 
    /// </summary>
    /// <param name="serviceProvider"></param>
    /// <param name="configuration"></param>
    /// <param name="httpContextAccessor"></param>
    public NhAccountInformationEndpointHandler(
        AuthenticationConfiguration configuration,
        IHttpContextAccessor httpContextAccessor
    ) : base(httpContextAccessor, "account",configuration)
    {
        _configuration = configuration;
        Handler = ProcessRequest;
        Method = HttpMethod.Get;
        if (!string.IsNullOrWhiteSpace(configuration.AccountInformationEndpoint))
        {
            Pattern = configuration.AccountInformationEndpoint;
        }
    }

    [ApiExplorerSettings(GroupName = "Authentication")]
    [Tags("Account")]
    [EndpointName("Account information")]
    //[Produces<Ok<AccountResponse>]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    protected async Task<IResult> ProcessRequest(
        [FromServices] INhUserManager<TUser> userManager,
        [FromServices] IRepository<TDivision> divisionRepository,
        [FromServices] IMapper mapper
    )
    {
        var authenticationService = GetAuthService();
        
        var token = HttpContext!.Request.Headers.Authorization.ToString().Split(' ', 2)[1];
        var jwt = authenticationService.DecodeToken(token);

        if (jwt == null)
        {
            return BadRequest(TaskResult.Failed("Invalid JWT"));
        }

        var userId = Guid.Parse(jwt.Claims.FirstOrDefault(x => x.Type == ClaimTypes.NameIdentifier)!.Value!);
        var user = await userManager
            .GetRepository()
            .GetAll()
            .Where(x => x.Id == userId)
            .Include(x => x.ActiveDivision)
            .FirstOrDefaultAsync();

        if (user == null)
        {
            return TypedResults.Unauthorized();
        }

        var response = new AccountResponse<TUserViewModel, TDivisionViewModel, TClaimViewModel> { User = mapper.Map<TUserViewModel>(user) };

        var claims = await userManager.GetValidClaims(user, _configuration.DivisionsEnabled);
        var impersonateOriginUserIdClaim = HttpContext?.User.Claims.FirstOrDefault(x => x.Type == NhPlatformClaimTypes.ImpersonateOriginUserId);

        if (impersonateOriginUserIdClaim != null)
        {
            claims.Add(impersonateOriginUserIdClaim);
        }

        response.Claims = claims.Select(mapper.Map<TClaimViewModel>);

        if (_configuration.DivisionsEnabled)
        {
            await GetUserDivisions(userManager, divisionRepository, mapper, response, claims);
        }
        
        response.ValidTo = jwt.ValidTo;
        response.ActiveDivision = response.User.ActiveDivision;
        response.ActiveDivisionId = response.User.ActiveDivisionId;
        response.Roles = await userManager.GetRolesAsync(user)
            .ContinueWith(x => response.Roles = x.Result.ToList());

        return TypedResults.Ok(response);
    }

    private static async Task GetUserDivisions(
        INhUserManager<TUser> userManager,
        IRepository<TDivision> divisionRepository,
        IMapper mapper,
        AccountResponse<TUserViewModel, TDivisionViewModel, TClaimViewModel> accountResponse,
        List<Claim> claims
    )
    {
        var divisionsQuery = divisionRepository!.GetAll();
        if (!claims.Any(x => x.Type == NhPlatformClaimTypes.Permission && x.Value == Platform.Common.Constants.DivisionPermissionClaimValues.AccessAll))
        {
            divisionsQuery = divisionsQuery
                .Where(x => x.UserSelectAllowed)
                .Where(x => x.DivisionUsers.Any(c => c.UserId == accountResponse.User.Id));
        }

        divisionsQuery = divisionsQuery.OrderBy(x => x.Name);

        var divisions = await divisionsQuery.ToListAsync();
        accountResponse.Divisions = mapper.Map<List<TDivisionViewModel>>(divisions);

        var removeDivisions = new List<TDivisionViewModel>();
        foreach (var division in accountResponse.Divisions)
        {
            if (!await userManager.DivisionAccessAsync(division.Id, claims,
                    new List<Claim>() { new Claim(NhPlatformClaimTypes.DivisionPermission, "general.view") }))
            {
                removeDivisions.Add(division);
            }
        }

        if (removeDivisions.Any())
        {
            foreach (var removeDivision in removeDivisions)
            {
                accountResponse.Divisions.Remove(removeDivision);
            }
        }
    }
}

/// <summary>
/// Account information
/// </summary>
public record AccountResponse<TUserViewModel, TDivisionViewModel, TClaimViewModel>
    where TUserViewModel : NhUserViewModel<TDivisionViewModel>
    where TDivisionViewModel : NhDivisionViewModel
    where TClaimViewModel : NhClaimViewModel
{
    public List<TDivisionViewModel> Divisions { get; set; } = new();
    public IEnumerable<TClaimViewModel> Claims { get; set; }
    public TUserViewModel User { get; set; }
    public Guid? ActiveDivisionId { get; set; }
    public TDivisionViewModel? ActiveDivision { get; set; }
    public List<string> Roles { get; set; } = new();

    public DateTime ValidTo { get; set; }
};