using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace NewHeap.Platform.AspNet.Common.Extensions;

/// <summary>
/// Publishes the public keys of an application that signs its tokens asymmetrically
/// (<see cref="NhJwtSigningKeys"/>), so other hosts validate its tokens without holding a key that
/// could mint them.
/// </summary>
public static class NhJwksEndpointRouteBuilderExtensions
{
    /// <summary>The media type of a JSON Web Key Set (RFC 7517).</summary>
    public const string ContentType = "application/jwk-set+json";

    /// <summary>
    /// Maps <c>GET <paramref name="pattern"/></c> (by default <see cref="NhJwtSigningKeys.DefaultJwksPath"/>):
    /// anonymous, cached by clients for five minutes, with the current and previous public keys and
    /// never a private or shared key. Opt-in: an application that signs with the shared HS256 key has
    /// no key to publish, so mapping the endpoint then fails at startup.
    /// </summary>
    /// <exception cref="InvalidOperationException">The application signs with the shared HS256 key.</exception>
    public static RouteHandlerBuilder MapNewHeapJwks(
        this IEndpointRouteBuilder endpoints,
        string pattern = NhJwtSigningKeys.DefaultJwksPath)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        var configuration = endpoints.ServiceProvider.GetRequiredService<IConfiguration>();
        if (!NhJwtSigningKeys.Read(configuration).IsAsymmetric)
        {
            throw new InvalidOperationException(
                $"MapNewHeapJwks publishes the public keys of asymmetric signing; set {NhJwtSigningKeys.AlgorithmKey} to RS256 or ES256 first. The shared HS256 key is never published.");
        }

        return endpoints.MapGet(pattern, (HttpContext context) =>
            {
                // Read per request: the cached key set changes only with its configuration.
                var json = NhJwtSigningKeys.Read(context.RequestServices.GetRequiredService<IConfiguration>()).ToJwksJson();
                context.Response.Headers.CacheControl = "public, max-age=300";
                return Results.Text(json, ContentType);
            })
            .AllowAnonymous()
            .WithName("NewHeapJwks")
            .WithSummary("Public keys of the application's access tokens")
            .WithDescription("The JSON Web Key Set (RFC 7517) with the current and previous public keys the application signs its access tokens with, for hosts that validate those tokens. It never contains a private or shared key.")
            .Produces(StatusCodes.Status200OK, contentType: ContentType);
    }
}

/// <summary>
/// Validates the access tokens of a NewHeap application that signs asymmetrically on another host,
/// with the public keys it publishes (<see cref="NhJwksEndpointRouteBuilderExtensions.MapNewHeapJwks"/>).
/// </summary>
public static class NhJwksJwtBearerOptionsExtensions
{
    /// <summary>How often the key set is read again: every hour.</summary>
    public static readonly TimeSpan AutomaticRefreshInterval = TimeSpan.FromHours(1);

    /// <summary>How often a token with an unknown key id may cause the key set to be read again: once a minute.</summary>
    public static readonly TimeSpan UnknownKeyRefreshInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Validates tokens with the key set at <paramref name="jwksUri"/>: issuer <paramref name="issuer"/>,
    /// one of <paramref name="audiences"/> as <c>aud</c>, RS256 or ES256 only and their lifetime. The key
    /// set is cached, read again every <see cref="AutomaticRefreshInterval"/> and when a token names an
    /// unknown key id (at most every <see cref="UnknownKeyRefreshInterval"/>), so a rotated key is
    /// picked up without a restart. The key set is fetched through the scheme's
    /// <see cref="JwtBearerOptions.Backchannel"/> or <see cref="JwtBearerOptions.BackchannelHttpHandler"/>,
    /// over https unless <see cref="JwtBearerOptions.RequireHttpsMetadata"/> is off.
    /// </summary>
    public static JwtBearerOptions UseNewHeapJwks(
        this JwtBearerOptions options,
        Uri jwksUri,
        string issuer,
        params string[] audiences)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(jwksUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        ArgumentNullException.ThrowIfNull(audiences);
        if (!jwksUri.IsAbsoluteUri)
        {
            throw new ArgumentException("The key set address must be absolute.", nameof(jwksUri));
        }
        if (audiences.Length == 0 || audiences.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Name at least one audience the host accepts.", nameof(audiences));
        }

        options.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
            jwksUri.AbsoluteUri,
            new NhJwksConfigurationRetriever(issuer),
            new NhJwksDocumentRetriever(options))
        {
            // The key set is small: read it every hour, and at most once a minute for an unknown key id.
            AutomaticRefreshInterval = AutomaticRefreshInterval,
            RefreshInterval = UnknownKeyRefreshInterval
        };
        options.RefreshOnIssuerKeyNotFound = true;
        options.TokenValidationParameters.ValidIssuer = issuer;
        options.TokenValidationParameters.ValidAudiences = audiences;
        options.TokenValidationParameters.ValidateIssuerSigningKey = true;
        options.TokenValidationParameters.ValidAlgorithms = [NhJwtSigningAlgorithms.RS256, NhJwtSigningAlgorithms.ES256];
        return options;
    }
}

/// <summary>
/// Reads a JSON Web Key Set as the configuration of a JWT bearer scheme: the expected issuer and
/// the signing keys.
/// </summary>
internal sealed class NhJwksConfigurationRetriever(string issuer) : IConfigurationRetriever<OpenIdConnectConfiguration>
{
    public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(
        string address,
        IDocumentRetriever retriever,
        CancellationToken cancel)
    {
        var json = await retriever.GetDocumentAsync(address, cancel);
        var keys = new JsonWebKeySet(json);
        var configuration = new OpenIdConnectConfiguration
        {
            Issuer = issuer,
            JsonWebKeySet = keys,
            JwksUri = address
        };
        foreach (var key in keys.GetSigningKeys())
        {
            configuration.SigningKeys.Add(key);
        }

        return configuration;
    }
}

/// <summary>
/// Fetches the key set through the scheme's backchannel, which ASP.NET sets after the scheme is
/// configured, so the HTTP client is created on first use.
/// </summary>
internal sealed class NhJwksDocumentRetriever(JwtBearerOptions options) : IDocumentRetriever
{
    private readonly Lock _gate = new();
    private HttpDocumentRetriever? _retriever;

    public Task<string> GetDocumentAsync(string address, CancellationToken cancel)
    {
        return Retriever().GetDocumentAsync(address, cancel);
    }

    private HttpDocumentRetriever Retriever()
    {
        lock (_gate)
        {
            if (_retriever is null)
            {
                var client = options.Backchannel
                    ?? new HttpClient(options.BackchannelHttpHandler ?? new HttpClientHandler())
                    {
                        Timeout = options.BackchannelTimeout
                    };
                _retriever = new HttpDocumentRetriever(client) { RequireHttps = options.RequireHttpsMetadata };
            }

            return _retriever;
        }
    }
}
