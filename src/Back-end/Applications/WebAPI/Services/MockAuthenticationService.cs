using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Models;
using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;

namespace WebAPI.Services;

public class MockAuthenticationService : INhAuthenticationService
{
    private class User
    {
        public Guid Id { get; set; }
        public string UserName { get; set; }
        public string Email { get; set; }
        public string PasswordHash { get; set; }
        public string RefreshToken { get; set; }
    }

    private List<User> _users = new List<User>();

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
        var (user, token) = await CreateToken(request);

        return new UserToken(new JwtSecurityTokenHandler().WriteToken(token), token.ValidTo, user.RefreshToken,
            token.Issuer);
    }

    private async Task<(User user, JwtSecurityToken)> CreateToken(AuthenticateRequest request)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            UserName = request.UserName,
            Email = request.UserName,
            PasswordHash = Guid.NewGuid().ToString(),
            RefreshToken = Guid.NewGuid().ToString(),
        };

        if (!_users.Any(u => u.UserName.Equals(user.UserName, StringComparison.InvariantCultureIgnoreCase)))
        { 
            _users.Add(user);
        }

        var token = await CreateToken(user.Id);

        return (user, token);
    }

    public async Task<JwtSecurityToken> CreateToken(Guid userId, TimeSpan? expiration = null, bool withDivisionClaims = false)
    {
        var user = _users.FirstOrDefault(u => u.Id == userId);
        
        if (user == null)
        {
            throw new Exception("User not found");
        }

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