using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NewHeap.Platform.AspNet.Common.Authentication;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Extensions;
using NewHeap.Platform.AspNet.Common.Services;
using NSubstitute;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

/// <summary>
/// The authentication service signs with the configured keys: the shared key by default, exactly as
/// before, or the asymmetric key with its key id; a delegated token for another service carries that
/// service as <c>aud</c> only when the application signs asymmetrically.
/// </summary>
public sealed class NhAuthenticationServiceSigningTests
{
    private const string Issuer = "https://api.example.test";
    private const string SharedKey = "a-random-test-signing-key-of-forty-bytes";
    private const string Audience = "https://planning.example.test";

    [Fact]
    public async Task An_application_with_the_shared_key_signs_hs256_with_its_issuer_as_audience()
    {
        var (service, user) = Create(new() { [NhJwtSigningKey.ConfigurationKey] = SharedKey });

        var token = await service.CreateToken(user.Id, [new Claim("scope", "orders")]);
        var delegated = await service.CreateDelegatedTokenAsync(user.Id, [new Claim("nh-ai-mcp-audience", Audience)], TimeSpan.FromMinutes(2), Audience);

        Assert.Equal(SecurityAlgorithms.HmacSha256, token.SignatureAlgorithm);
        Assert.Equal([Issuer], token.Audiences);
        Assert.Null(token.Header.Kid);
        Assert.True(delegated.Success);
        // The hosts that share the key validate the issuer as audience, as before.
        Assert.Equal([Issuer], delegated.Data!.Audiences);
        Assert.Equal(SecurityAlgorithms.HmacSha256, delegated.Data.SignatureAlgorithm);
    }

    [Fact]
    public async Task An_application_that_signs_asymmetrically_binds_a_delegated_token_to_the_service()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var (service, user) = Create(new()
        {
            [NhJwtSigningKeys.AlgorithmKey] = "ES256",
            [NhJwtSigningKeys.KeyIdKey] = "2026-10",
            [NhJwtSigningKeys.PrivateKeyKey] = key.ExportPkcs8PrivateKeyPem()
        });

        var token = await service.CreateToken(user.Id, [new Claim("scope", "orders")]);
        var delegated = await service.CreateDelegatedTokenAsync(user.Id, [new Claim("nh-ai-mcp-audience", Audience)], TimeSpan.FromMinutes(2), Audience);
        var legacy = await service.CreateDelegatedTokenAsync(user.Id, [new Claim("marker", "read")], TimeSpan.FromMinutes(2));

        Assert.Equal(SecurityAlgorithms.EcdsaSha256, token.SignatureAlgorithm);
        Assert.Equal("2026-10", token.Header.Kid);
        Assert.Equal([Issuer], token.Audiences);
        Assert.True(delegated.Success);
        Assert.Equal([Audience], delegated.Data!.Audiences);
        Assert.Equal("2026-10", delegated.Data.Header.Kid);
        Assert.Equal(Audience, delegated.Data.Claims.Single(claim => claim.Type == "nh-ai-mcp-audience").Value);
        Assert.True(delegated.Data.ValidTo <= DateTime.UtcNow.AddMinutes(2).AddSeconds(5));
        // A delegated token without a service keeps the issuer as audience.
        Assert.Equal([Issuer], legacy.Data!.Audiences);

        // The service validates it with the public key and its own origin; the application refuses it.
        var keys = NhJwtSigningKeys.Read(service.Configuration);
        var serviceParameters = new TokenValidationParameters { ValidIssuer = Issuer, ValidAudience = Audience, IssuerSigningKeys = keys.ValidationKeys };
        var applicationParameters = new TokenValidationParameters { ValidIssuer = Issuer, ValidAudience = Issuer, IssuerSigningKeys = keys.ValidationKeys };
        var written = new JwtSecurityTokenHandler().WriteToken(delegated.Data);
        new JwtSecurityTokenHandler().ValidateToken(written, serviceParameters, out _);
        Assert.Throws<SecurityTokenInvalidAudienceException>(() => new JwtSecurityTokenHandler().ValidateToken(written, applicationParameters, out _));
    }

    [Fact]
    public async Task A_person_who_may_not_sign_in_gets_no_delegated_token_for_a_service()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var (service, user) = Create(new()
        {
            [NhJwtSigningKeys.AlgorithmKey] = "ES256",
            [NhJwtSigningKeys.KeyIdKey] = "2026-10",
            [NhJwtSigningKeys.PrivateKeyKey] = key.ExportPkcs8PrivateKeyPem()
        });
        service.Users.IsBlockedAsync(user).Returns(true);

        var delegated = await service.CreateDelegatedTokenAsync(user.Id, [], TimeSpan.FromMinutes(2), Audience);

        Assert.False(delegated.Success);
    }

    private static (SigningAuthenticationService Service, NhUser User) Create(Dictionary<string, string?> settings)
    {
        settings["NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:Issuer"] = Issuer;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var user = new NhUser { Id = Guid.NewGuid(), UserName = "person@example.test", Email = "person@example.test" };

        var identityUsers = Substitute.For<UserManager<NhUser>>(
            Substitute.For<IUserStore<NhUser>>(), null, null, null, null, null, null, null, null);
        identityUsers.GetSecurityStampAsync(user).Returns("stamp-1");
        var signInManager = Substitute.For<SignInManager<NhUser>>(
            identityUsers,
            Substitute.For<IHttpContextAccessor>(),
            Substitute.For<IUserClaimsPrincipalFactory<NhUser>>(),
            Options.Create(new IdentityOptions()),
            Substitute.For<ILogger<SignInManager<NhUser>>>(),
            Substitute.For<IAuthenticationSchemeProvider>(),
            Substitute.For<IUserConfirmation<NhUser>>());
        signInManager.CanSignInAsync(user).Returns(true);

        var users = Substitute.For<INhUserManager<NhUser>>();
        users.FindByIdAsync(user.Id.ToString()).Returns(user);
        users.IsLockedOutAsync(user).Returns(false);
        users.IsBlockedAsync(user).Returns(false);
        users.GetValidClaimsByUserIdAsync(user.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(_ => [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())]);

        var service = new SigningAuthenticationService(
            signInManager,
            users,
            Substitute.For<ILogger<AuthenticationService>>(),
            configuration,
            new TokenValidationParameters(),
            new AuthenticationConfiguration());
        return (service, user);
    }

    private sealed class SigningAuthenticationService : NhAuthenticationService<
        NhUser,
        NhDivision,
        NhDivisionUser,
        NhDivisionRole,
        NhDivisionUserRole,
        NhDivisionRoleClaim>
    {
        internal SigningAuthenticationService(
            SignInManager<NhUser> signInManager,
            INhUserManager<NhUser> userManager,
            ILogger<AuthenticationService> logger,
            IConfiguration configuration,
            TokenValidationParameters tokenValidationParameters,
            AuthenticationConfiguration authConfiguration)
            : base(signInManager, userManager, logger, configuration, tokenValidationParameters, authConfiguration)
        {
        }

        internal IConfiguration Configuration => _configuration;

        internal INhUserManager<NhUser> Users => _userManager;
    }
}
