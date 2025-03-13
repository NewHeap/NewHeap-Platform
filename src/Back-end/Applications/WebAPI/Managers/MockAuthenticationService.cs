using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Models;
using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Threading.Tasks;

namespace WebAPI.Managers;

public class MockAuthenticationService : INhAuthenticationService
{
    public MockAuthenticationService()
    {
    }

    public string GetIssuerDomain()
    {
        return "localhost";
    }

    public async Task<TaskResult<UserToken>> Authenticate(AuthenticateRequest request,
        IEnumerable<Claim> requiredClaims = null)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            UserName = request.UserName,
            Email = request.UserName,
            PasswordHash = Guid.NewGuid().ToString(),
            RefreshToken = Guid.NewGuid().ToString(),
        };
        var token = await CreateToken(user);
        return new UserToken(new JwtSecurityTokenHandler().WriteToken(token), token.ValidTo, user.RefreshToken,
            token.Issuer);
    }

    public async Task<JwtSecurityToken> CreateToken(User user, TimeSpan? expiration = null)
    {
        return new JwtSecurityToken(
            claims: [
                new Claim(JwtRegisteredClaimNames.Sub, user.Email!),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
                new Claim(ClaimTypes.Name, user.UserName!),
                new Claim(ClaimTypes.Email, user.Email!),
                new Claim(JwtRegisteredClaimNames.Email, user.Email!),
                new Claim(JwtRegisteredClaimNames.NameId, user.Id.ToString()),
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())
            ],
            expires: DateTime.Now.Add(expiration ?? TimeSpan.FromDays(1)),
            issuer: GetIssuer(),
            audience: GetIssuer()
            );
    }

    public JwtSecurityToken DecodeToken(string token)
    {
        return new JwtSecurityToken(token);
    }

    public async Task<TaskResult<UserToken>> RefreshToken(RefreshTokenRequest request)
    {
        return await Authenticate(new AuthenticateRequest(request.UserName, ""));
    }

    public string GetIssuer()
    {
        return "https://localhost:5000";
    }
}