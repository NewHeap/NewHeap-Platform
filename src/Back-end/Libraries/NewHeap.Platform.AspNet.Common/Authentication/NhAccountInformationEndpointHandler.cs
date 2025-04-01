using AutoMapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
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
public class NhAccountInformationEndpointHandler : BaseNhAuthenticationEndpoint
{
    private readonly AuthenticationConfiguration _configuration;

    /// <summary>
    /// 
    /// </summary>
    /// <param name="configuration"></param>
    /// <param name="httpContextAccessor"></param>
    /// <param name="pattern"></param>
    public NhAccountInformationEndpointHandler(
        AuthenticationConfiguration configuration,
        IHttpContextAccessor httpContextAccessor
    ) : base(httpContextAccessor, "account")
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
    [Produces<Ok<AccountResponse>>]
    private async Task<IResult> ProcessRequest(
        [FromServices] INhAuthenticationService authenticationService,
        [FromServices] INhUserManager userManager,
        [FromServices] IRepository<Division> divisionRepository,
        [FromServices] IMapper mapper
    )
    {
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

        var response = new AccountResponse { User = mapper.Map<UserViewModel>(user) };

        var claims = await userManager.GetValidClaims(user, _configuration.DivisionsEnabled);
        response.Claims = claims.Select(mapper.Map<ClaimViewModel>);

        if (_configuration.DivisionsEnabled)
        {
            await GetUserDivisions(userManager, divisionRepository, mapper, response, claims);
        }

        response.ActiveDivision = response.User.ActiveDivision;
        response.ActiveDivisionId = response.User.ActiveDivisionId;
        response.Roles = await userManager.GetRolesAsync(user)
            .ContinueWith(x => response.Roles = x.Result.ToList());

        return TypedResults.Ok(response);
    }

    private static async Task GetUserDivisions(
        INhUserManager userManager,
        IRepository<Division> divisionRepository,
        IMapper mapper,
        AccountResponse accountResponse,
        List<Claim> claims
    )
    {
        var divisionsQuery = divisionRepository!.GetAll();
        if (!claims.Any(x => x.Type == NhPlatformClaimTypes.Permission && x.Value == "app.division.access-all"))
        {
            divisionsQuery = divisionsQuery
                .Where(x => x.UserSelectAllowed)
                .Where(x => x.DivisionUsers.Any(c => c.UserId == accountResponse.User.Id));
        }

        divisionsQuery = divisionsQuery.OrderBy(x => x.Name);

        var divisions = await divisionsQuery.ToListAsync();
        accountResponse.Divisions = mapper.Map<List<DivisionViewModel>>(divisions);

        var removeDivisions = new List<DivisionViewModel>();
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
public record AccountResponse
{
    public List<DivisionViewModel> Divisions { get; set; } = new();
    public IEnumerable<ClaimViewModel> Claims { get; set; }
    public UserViewModel User { get; set; }
    public Guid? ActiveDivisionId { get; set; }
    public DivisionViewModel? ActiveDivision { get; set; }
    public List<string> Roles { get; set; } = new();
};