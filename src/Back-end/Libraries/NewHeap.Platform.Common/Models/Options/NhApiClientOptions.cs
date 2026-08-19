using System.Text.Json;
using System.Text.Json.Serialization;

namespace NewHeap.Platform.Common.Models.Options;

/// <summary>
/// Configuration for a NewHeap API client registration.
/// </summary>
public sealed class NhApiClientOptions
{
    public NhApiClientOptions()
    {
        JsonSerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };
        JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    }

    /// <summary>
    /// Base address of the API.
    /// </summary>
    public Uri? BaseAddress { get; set; }

    /// <summary>
    /// Timeout used by both the API and authentication clients.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(100);

    /// <summary>
    /// JSON settings used for request and response bodies.
    /// </summary>
    public JsonSerializerOptions JsonSerializerOptions { get; }

    /// <summary>
    /// Optional username/password authentication against a NewHeap authentication endpoint.
    /// </summary>
    public NhApiUsernamePasswordAuthenticationOptions? Authentication { get; set; }

    internal void Validate()
    {
        if (BaseAddress == null)
        {
            throw new InvalidOperationException($"{nameof(BaseAddress)} is required.");
        }

        if (!BaseAddress.IsAbsoluteUri)
        {
            throw new InvalidOperationException($"{nameof(BaseAddress)} must be an absolute URI.");
        }

        if (Timeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException($"{nameof(Timeout)} must be greater than zero.");
        }

        Authentication?.Validate();
    }
}

/// <summary>
/// Username/password authentication settings for a NewHeap API.
/// </summary>
public sealed class NhApiUsernamePasswordAuthenticationOptions
{
    /// <summary>
    /// Absolute or base-address-relative authentication endpoint.
    /// </summary>
    public string Endpoint { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string Realm { get; set; } = string.Empty;

    /// <summary>
    /// Refresh the cached token this long before it expires.
    /// </summary>
    public TimeSpan RefreshBeforeExpiration { get; set; } = TimeSpan.FromMinutes(3);

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Endpoint))
        {
            throw new InvalidOperationException(
                $"{nameof(NhApiUsernamePasswordAuthenticationOptions)}.{nameof(Endpoint)} is required.");
        }

        if (string.IsNullOrWhiteSpace(Username))
        {
            throw new InvalidOperationException(
                $"{nameof(NhApiUsernamePasswordAuthenticationOptions)}.{nameof(Username)} is required.");
        }

        if (string.IsNullOrWhiteSpace(Password))
        {
            throw new InvalidOperationException(
                $"{nameof(NhApiUsernamePasswordAuthenticationOptions)}.{nameof(Password)} is required.");
        }

        if (RefreshBeforeExpiration < TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"{nameof(NhApiUsernamePasswordAuthenticationOptions)}.{nameof(RefreshBeforeExpiration)} cannot be negative.");
        }
    }
}
