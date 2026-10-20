using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using NewHeap.Platform.AspNet.Common.Extensions;
using NewHeap.Platform.AspNet.Common.Models.Options;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

/// <summary>
/// Asymmetric signing of NewHeap access tokens: RS256 and ES256 from a private key in the secrets
/// file, validation with the public keys including previous ones, the published key set and hosts
/// that validate with it, while an application without the signing section keeps HS256 unchanged.
/// </summary>
public sealed class NhJwtSigningKeysTests
{
    private const string Issuer = "https://api.example.test";
    private const string SharedKey = "a-random-test-signing-key-of-forty-bytes";

    [Fact]
    public void Without_the_signing_section_tokens_keep_the_shared_hs256_key()
    {
        var configuration = Configuration(new() { [NhJwtSigningKey.ConfigurationKey] = SharedKey });

        var keys = NhJwtSigningKeys.Read(configuration);
        var parameters = Parameters(configuration);

        Assert.False(keys.IsAsymmetric);
        Assert.False(NhJwtSigningKeys.IsAsymmetric(configuration));
        Assert.Equal(NhJwtSigningAlgorithms.HS256, keys.SigningCredentials.Algorithm);
        Assert.Equal(SharedKey, System.Text.Encoding.UTF8.GetString(Assert.IsType<SymmetricSecurityKey>(keys.SigningCredentials.Key).Key));
        // The validation parameters are those of before: the shared key, no algorithm list.
        Assert.IsType<SymmetricSecurityKey>(parameters.IssuerSigningKey);
        Assert.Null(parameters.IssuerSigningKeys);
        Assert.Null(parameters.ValidAlgorithms);
        Assert.Empty(keys.PublishedKeyIds);
        Assert.Throws<InvalidOperationException>(() => keys.ToJwksJson());
        Assert.True(Validates(parameters, Sign(keys.SigningCredentials)));
    }

    [Fact]
    public void Es256_signs_with_the_private_key_from_the_secrets_file_and_validates_with_its_public_key()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var configuration = Configuration(new()
        {
            [NhJwtSigningKeys.AlgorithmKey] = "ES256",
            [NhJwtSigningKeys.KeyIdKey] = "2026-10",
            // As a secrets file provides it: the configuration loads that file under "Secrets".
            ["Secrets:" + NhJwtSigningKeys.PrivateKeyKey] = key.ExportPkcs8PrivateKeyPem()
        });

        var keys = NhJwtSigningKeys.Read(configuration);
        var token = Sign(keys.SigningCredentials);
        var parameters = Parameters(configuration);

        Assert.True(keys.IsAsymmetric);
        Assert.Equal("ES256", new JwtSecurityTokenHandler().ReadJwtToken(token).Header.Alg);
        Assert.Equal("2026-10", new JwtSecurityTokenHandler().ReadJwtToken(token).Header.Kid);
        Assert.True(Validates(parameters, token));
        // Validation holds only the public key: it cannot sign.
        Assert.All(parameters.IssuerSigningKeys, validation => Assert.Throws<CryptographicException>(
            () => Assert.IsType<ECDsaSecurityKey>(validation).ECDsa.ExportParameters(includePrivateParameters: true)));
        Assert.Equal(["ES256"], parameters.ValidAlgorithms);
        // A token of the shared key no longer validates, nor one of another key with the same id.
        Assert.False(Validates(parameters, Sign(new SigningCredentials(new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(SharedKey)), SecurityAlgorithms.HmacSha256))));
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.False(Validates(parameters, Sign(new SigningCredentials(new ECDsaSecurityKey(other) { KeyId = "2026-10" }, SecurityAlgorithms.EcdsaSha256))));
    }

    [Fact]
    public void Rs256_signs_with_a_pkcs12_file_and_its_password()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=NewHeap token signing tests", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var configuration = Configuration(new()
        {
            [NhJwtSigningKeys.AlgorithmKey] = "RS256",
            [NhJwtSigningKeys.KeyIdKey] = "rsa-1",
            ["Secrets:" + NhJwtSigningKeys.Pkcs12Key] = Convert.ToBase64String(certificate.Export(X509ContentType.Pkcs12, "pkcs12-password")),
            ["Secrets:" + NhJwtSigningKeys.Pkcs12PasswordKey] = "pkcs12-password"
        });

        var keys = NhJwtSigningKeys.Read(configuration);
        var token = Sign(keys.SigningCredentials);

        Assert.Equal("RS256", new JwtSecurityTokenHandler().ReadJwtToken(token).Header.Alg);
        Assert.True(Validates(Parameters(configuration), token));
    }

    [Fact]
    public void A_rotated_key_keeps_validating_its_tokens_as_a_previous_key()
    {
        using var old = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var current = RSA.Create(2048);
        var before = Configuration(new()
        {
            [NhJwtSigningKeys.AlgorithmKey] = "ES256",
            [NhJwtSigningKeys.KeyIdKey] = "2026-09",
            [NhJwtSigningKeys.PrivateKeyKey] = old.ExportPkcs8PrivateKeyPem()
        });
        var issuedBefore = Sign(NhJwtSigningKeys.Read(before).SigningCredentials);

        // The new key signs; the old one validates for as long as its tokens live.
        var after = Configuration(new()
        {
            [NhJwtSigningKeys.AlgorithmKey] = "RS256",
            [NhJwtSigningKeys.KeyIdKey] = "2026-10",
            [NhJwtSigningKeys.PrivateKeyKey] = current.ExportRSAPrivateKeyPem(),
            [NhJwtSigningKeys.PreviousKeysSection + ":0:KeyId"] = "2026-09",
            [NhJwtSigningKeys.PreviousKeysSection + ":0:PublicKey"] = old.ExportSubjectPublicKeyInfoPem()
        });
        var keys = NhJwtSigningKeys.Read(after);
        var parameters = Parameters(after);

        Assert.True(Validates(parameters, issuedBefore));
        Assert.True(Validates(parameters, Sign(keys.SigningCredentials)));
        Assert.Equal(["2026-10", "2026-09"], keys.PublishedKeyIds);
        Assert.Equal(["RS256", "ES256"], parameters.ValidAlgorithms);

        // Once the previous key is removed, its tokens no longer validate.
        var removed = Configuration(new()
        {
            [NhJwtSigningKeys.AlgorithmKey] = "RS256",
            [NhJwtSigningKeys.KeyIdKey] = "2026-10",
            [NhJwtSigningKeys.PrivateKeyKey] = current.ExportRSAPrivateKeyPem()
        });
        Assert.False(Validates(Parameters(removed), issuedBefore));
    }

    [Fact]
    public void A_previous_key_may_be_a_certificate()
    {
        using var old = RSA.Create(2048);
        using var certificate = new CertificateRequest("CN=previous", old, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var current = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var issuedBefore = Sign(new SigningCredentials(new RsaSecurityKey(old) { KeyId = "old" }, SecurityAlgorithms.RsaSha256));
        var configuration = Configuration(new()
        {
            [NhJwtSigningKeys.AlgorithmKey] = "ES256",
            [NhJwtSigningKeys.KeyIdKey] = "new",
            [NhJwtSigningKeys.PrivateKeyKey] = current.ExportECPrivateKeyPem(),
            [NhJwtSigningKeys.PreviousKeysSection + ":0:KeyId"] = "old",
            [NhJwtSigningKeys.PreviousKeysSection + ":0:PublicKey"] = certificate.ExportCertificatePem()
        });

        Assert.True(Validates(Parameters(configuration), issuedBefore));
    }

    [Fact]
    public void Shared_key_tokens_are_accepted_only_while_an_application_moves_from_hs256()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var settings = new Dictionary<string, string?>
        {
            [NhJwtSigningKey.ConfigurationKey] = SharedKey,
            [NhJwtSigningKeys.AlgorithmKey] = "ES256",
            [NhJwtSigningKeys.KeyIdKey] = "2026-10",
            [NhJwtSigningKeys.PrivateKeyKey] = key.ExportPkcs8PrivateKeyPem()
        };
        var sharedKeyToken = Sign(new SigningCredentials(new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(SharedKey)), SecurityAlgorithms.HmacSha256));

        Assert.False(Validates(Parameters(Configuration(settings)), sharedKeyToken));
        settings[NhJwtSigningKeys.AcceptSharedKeyTokensKey] = "true";
        var moving = Configuration(settings);
        Assert.True(Validates(Parameters(moving), sharedKeyToken));
        // The shared key is never published.
        Assert.Equal(["2026-10"], NhJwtSigningKeys.Read(moving).PublishedKeyIds);
        Assert.DoesNotContain("\"oct\"", NhJwtSigningKeys.Read(moving).ToJwksJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void Configuration_errors_stop_the_platform_and_never_show_a_key()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        using var small = RSA.Create(1024);
        var privatePem = ec.ExportPkcs8PrivateKeyPem();

        AssertRefused(new() { [NhJwtSigningKeys.AlgorithmKey] = "PS256" }, NhJwtSigningKeys.AlgorithmKey, null);
        AssertRefused(new() { [NhJwtSigningKeys.AlgorithmKey] = "ES256", [NhJwtSigningKeys.PrivateKeyKey] = privatePem }, NhJwtSigningKeys.KeyIdKey, privatePem);
        AssertRefused(new() { [NhJwtSigningKeys.AlgorithmKey] = "ES256", [NhJwtSigningKeys.KeyIdKey] = "has spaces", [NhJwtSigningKeys.PrivateKeyKey] = privatePem }, NhJwtSigningKeys.KeyIdKey, privatePem);
        AssertRefused(new() { [NhJwtSigningKeys.AlgorithmKey] = "ES256", [NhJwtSigningKeys.KeyIdKey] = "k" }, NhJwtSigningKeys.PrivateKeyKey, null);
        // An unresolved reference counts as missing, so its text never becomes a key.
        AssertRefused(new() { [NhJwtSigningKeys.AlgorithmKey] = "ES256", [NhJwtSigningKeys.KeyIdKey] = "k", [NhJwtSigningKeys.PrivateKeyKey] = "${Secrets:signing}" }, NhJwtSigningKeys.PrivateKeyKey, null);
        AssertRefused(new() { [NhJwtSigningKeys.AlgorithmKey] = "ES256", [NhJwtSigningKeys.KeyIdKey] = "k", [NhJwtSigningKeys.PrivateKeyKey] = privatePem, [NhJwtSigningKeys.Pkcs12Key] = "AAAA" }, NhJwtSigningKeys.Pkcs12Key, privatePem);
        // A public key cannot sign, another curve or a small RSA key does not fit.
        AssertRefused(new() { [NhJwtSigningKeys.AlgorithmKey] = "ES256", [NhJwtSigningKeys.KeyIdKey] = "k", [NhJwtSigningKeys.PrivateKeyKey] = ec.ExportSubjectPublicKeyInfoPem() }, NhJwtSigningKeys.PrivateKeyKey, null);
        AssertRefused(new() { [NhJwtSigningKeys.AlgorithmKey] = "ES256", [NhJwtSigningKeys.KeyIdKey] = "k", [NhJwtSigningKeys.PrivateKeyKey] = p384.ExportPkcs8PrivateKeyPem() }, NhJwtSigningKeys.PrivateKeyKey, null);
        AssertRefused(new() { [NhJwtSigningKeys.AlgorithmKey] = "RS256", [NhJwtSigningKeys.KeyIdKey] = "k", [NhJwtSigningKeys.PrivateKeyKey] = small.ExportPkcs8PrivateKeyPem() }, NhJwtSigningKeys.PrivateKeyKey, null);
        AssertRefused(new Dictionary<string, string?> { [NhJwtSigningKeys.AlgorithmKey] = "ES256", [NhJwtSigningKeys.KeyIdKey] = "k", [NhJwtSigningKeys.PrivateKeyKey] = privatePem }.With(NhJwtSigningKeys.PreviousKeysSection + ":0:KeyId", "k").With(NhJwtSigningKeys.PreviousKeysSection + ":0:PublicKey", ec.ExportSubjectPublicKeyInfoPem()), NhJwtSigningKeys.PreviousKeysSection + ":0:KeyId", privatePem);
        AssertRefused(new Dictionary<string, string?> { [NhJwtSigningKeys.AlgorithmKey] = "ES256", [NhJwtSigningKeys.KeyIdKey] = "k", [NhJwtSigningKeys.PrivateKeyKey] = privatePem }.With(NhJwtSigningKeys.PreviousKeysSection + ":0:KeyId", "old"), NhJwtSigningKeys.PreviousKeysSection + ":0:PublicKey", privatePem);
        // The platform's validation parameters refuse them as well.
        Assert.Throws<InvalidOperationException>(() => Parameters(Configuration(new() { [NhJwtSigningKeys.AlgorithmKey] = "ES256" })));
    }

    [Fact]
    public async Task The_key_set_publishes_the_public_keys_only()
    {
        using var current = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var previous = RSA.Create(2048);
        await using var app = await StartIssuerAsync(new()
        {
            [NhJwtSigningKeys.AlgorithmKey] = "ES256",
            [NhJwtSigningKeys.KeyIdKey] = "current",
            [NhJwtSigningKeys.PrivateKeyKey] = current.ExportPkcs8PrivateKeyPem(),
            [NhJwtSigningKeys.PreviousKeysSection + ":0:KeyId"] = "previous",
            [NhJwtSigningKeys.PreviousKeysSection + ":0:PublicKey"] = previous.ExportSubjectPublicKeyInfoPem()
        });
        using var client = app.GetTestClient();

        using var response = await client.GetAsync(NhJwtSigningKeys.DefaultJwksPath);
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(NhJwksEndpointRouteBuilderExtensions.ContentType, response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("public, max-age=300", response.Headers.CacheControl?.ToString());
        using var document = JsonDocument.Parse(json);
        var keys = document.RootElement.GetProperty("keys").EnumerateArray().ToArray();
        Assert.Equal(2, keys.Length);
        var ec = keys[0];
        Assert.Equal(["kty", "use", "kid", "alg", "crv", "x", "y"], ec.EnumerateObject().Select(property => property.Name));
        Assert.Equal("EC", ec.GetProperty("kty").GetString());
        Assert.Equal("sig", ec.GetProperty("use").GetString());
        Assert.Equal("current", ec.GetProperty("kid").GetString());
        Assert.Equal("ES256", ec.GetProperty("alg").GetString());
        Assert.Equal("P-256", ec.GetProperty("crv").GetString());
        var rsa = keys[1];
        Assert.Equal(["kty", "use", "kid", "alg", "n", "e"], rsa.EnumerateObject().Select(property => property.Name));
        Assert.Equal("previous", rsa.GetProperty("kid").GetString());
        Assert.Equal("RS256", rsa.GetProperty("alg").GetString());
        // The published key is the configured public key.
        Assert.Equal(Base64UrlEncoder.Encode(previous.ExportParameters(false).Modulus!), rsa.GetProperty("n").GetString());
        foreach (var privateMember in new[] { "d", "p", "q", "dp", "dq", "qi", "k" })
        {
            Assert.All(keys, key => Assert.False(key.TryGetProperty(privateMember, out _)));
        }
    }

    [Fact]
    public async Task An_application_with_the_shared_key_cannot_map_the_key_set()
    {
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => StartIssuerAsync(new() { [NhJwtSigningKey.ConfigurationKey] = SharedKey }));

        Assert.Contains(NhJwtSigningKeys.AlgorithmKey, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Another_host_validates_tokens_with_the_published_key_set_and_picks_up_a_rotation()
    {
        using var first = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var second = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var settings = new Dictionary<string, string?>
        {
            [NhJwtSigningKeys.AlgorithmKey] = "ES256",
            [NhJwtSigningKeys.KeyIdKey] = "first",
            [NhJwtSigningKeys.PrivateKeyKey] = first.ExportPkcs8PrivateKeyPem()
        };
        await using var issuer = await StartIssuerAsync(settings);
        await using var receiver = await StartReceiverAsync(issuer.GetTestServer().CreateHandler(), "https://planning.example.test");
        using var client = receiver.GetTestClient();
        var configuration = issuer.Services.GetRequiredService<IConfiguration>();

        Assert.Equal(HttpStatusCode.OK, await SendAsync(client, Sign(NhJwtSigningKeys.Read(configuration).SigningCredentials)));
        Assert.Equal(HttpStatusCode.OK, await SendAsync(client, Sign(NhJwtSigningKeys.Read(configuration).SigningCredentials, audience: "https://planning.example.test")));
        // Another audience, another issuer or a key the issuer never published are refused.
        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(client, Sign(NhJwtSigningKeys.Read(configuration).SigningCredentials, audience: "https://other.example.test")));
        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(client, Sign(NhJwtSigningKeys.Read(configuration).SigningCredentials, issuer: "https://other.example.test")));
        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(client, Sign(new SigningCredentials(new ECDsaSecurityKey(second) { KeyId = "second" }, SecurityAlgorithms.EcdsaSha256))));

        // The issuer rotates to the second key: the receiver reads the key set again for the new key id.
        configuration[NhJwtSigningKeys.KeyIdKey] = "second";
        configuration[NhJwtSigningKeys.PrivateKeyKey] = second.ExportPkcs8PrivateKeyPem();
        configuration[NhJwtSigningKeys.PreviousKeysSection + ":0:KeyId"] = "first";
        configuration[NhJwtSigningKeys.PreviousKeysSection + ":0:PublicKey"] = first.ExportSubjectPublicKeyInfoPem();
        Assert.Equal(["second", "first"], NhJwtSigningKeys.Read(configuration).PublishedKeyIds);
        var rotated = Sign(NhJwtSigningKeys.Read(configuration).SigningCredentials);
        // The first request with the unknown key id asks for the key set again; the scheme may still
        // answer from the old one while it is read.
        var status = HttpStatusCode.Unauthorized;
        for (var attempt = 0; attempt < 50 && status != HttpStatusCode.OK; attempt++)
        {
            status = await SendAsync(client, rotated);
            if (status != HttpStatusCode.OK)
            {
                await Task.Delay(100);
            }
        }
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(HttpStatusCode.OK, await SendAsync(client, Sign(new SigningCredentials(new ECDsaSecurityKey(first) { KeyId = "first" }, SecurityAlgorithms.EcdsaSha256))));
    }

    private static void AssertRefused(Dictionary<string, string?> settings, string setting, string? secret)
    {
        var failure = Assert.Throws<InvalidOperationException>(() => NhJwtSigningKeys.Read(Configuration(settings)));

        Assert.Contains(setting, failure.Message, StringComparison.Ordinal);
        if (secret is not null)
        {
            Assert.DoesNotContain(secret, failure.Message, StringComparison.Ordinal);
        }
    }

    private static async Task<WebApplication> StartIssuerAsync(Dictionary<string, string?> settings)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(settings);
        var app = builder.Build();
        app.MapNewHeapJwks();
        await app.StartAsync();
        return app;
    }

    private static async Task<WebApplication> StartReceiverAsync(HttpMessageHandler issuerHandler, string ownAudience)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.BackchannelHttpHandler = issuerHandler;
                // The test server answers over http; a real issuer is reached over https.
                options.RequireHttpsMetadata = false;
                options.UseNewHeapJwks(new Uri("http://localhost" + NhJwtSigningKeys.DefaultJwksPath), Issuer, Issuer, ownAudience);
                // A minute between reads for unknown key ids is long for a test.
                ((Microsoft.IdentityModel.Protocols.ConfigurationManager<Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfiguration>)options.ConfigurationManager!)
                    .RefreshInterval = TimeSpan.FromSeconds(1);
            });
        builder.Services.AddAuthorization();
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/mcp", () => Results.Ok()).RequireAuthorization();
        await app.StartAsync();
        return app;
    }

    private static async Task<HttpStatusCode> SendAsync(HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/mcp");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    private static TokenValidationParameters Parameters(IConfiguration configuration)
    {
        var parameters = new TokenValidationParameters { ValidateLifetime = true, ClockSkew = TimeSpan.Zero };
        NewHeapAspNetCommonOptions.Builder(configuration).Build().JwtBearerOptionsTokenValidationParametersAction(parameters);
        parameters.ValidIssuer = Issuer;
        parameters.ValidAudience = Issuer;
        return parameters;
    }

    private static bool Validates(TokenValidationParameters parameters, string token)
    {
        try
        {
            new JwtSecurityTokenHandler().ValidateToken(token, parameters, out _);
            return true;
        }
        catch (SecurityTokenException)
        {
            return false;
        }
    }

    private static string Sign(SigningCredentials credentials, string issuer = Issuer, string audience = Issuer)
    {
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            issuer,
            audience,
            [new Claim("sub", "person-1")],
            DateTime.UtcNow,
            DateTime.UtcNow.AddMinutes(5),
            credentials));
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values)
    {
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}

internal static class NhJwtSigningKeysTestSettings
{
    public static Dictionary<string, string?> With(this Dictionary<string, string?> settings, string key, string? value)
    {
        settings[key] = value;
        return settings;
    }
}
