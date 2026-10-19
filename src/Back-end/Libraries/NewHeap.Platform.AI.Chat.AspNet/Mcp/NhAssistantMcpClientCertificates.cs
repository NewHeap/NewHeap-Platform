using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.Mcp;

/// <summary>
/// Client certificates for mutual TLS with MCP servers, named by
/// <see cref="Entities.AssistantMcpServer.ClientCertificateReference"/> and configured under
/// <see cref="Section"/><c>:&lt;name&gt;</c>: either <c>Path</c> to a PKCS#12 file with an optional
/// <c>Password</c>, or <c>Thumbprint</c> of a certificate in a store (<c>StoreLocation</c>
/// <c>CurrentUser</c> or <c>LocalMachine</c>, <c>StoreName</c> <c>My</c> by default). The certificate
/// must have a private key. A loaded certificate is kept until its configuration changes.
/// </summary>
internal sealed partial class NhAssistantMcpClientCertificates(
    IServiceProvider services,
    ILogger<NhAssistantMcpClientCertificates> logger) : IDisposable
{
    public const string Section = "NewHeap:AI:Assistant:Mcp:ClientCertificates";

    private readonly ConcurrentDictionary<string, LoadedCertificate> _loaded = new(StringComparer.Ordinal);

    public static bool IsValidName(string? name)
    {
        return name is not null && NamePattern().IsMatch(name);
    }

    public TaskResult<X509Certificate2> Load(string name)
    {
        if (!IsValidName(name))
        {
            return Invalid();
        }
        var configuration = services.GetService<IConfiguration>();
        var section = configuration?.GetSection(Section + ":" + name);
        if (section is null || !section.Exists())
        {
            return Invalid();
        }

        var path = section["Path"];
        var thumbprint = section["Thumbprint"];
        var fingerprint = Fingerprint(path, section["Password"], thumbprint, section["StoreLocation"], section["StoreName"]);
        if (_loaded.TryGetValue(name, out var cached) && cached.Fingerprint == fingerprint)
        {
            return TaskResult<X509Certificate2>.Succeeded(cached.Certificate);
        }

        X509Certificate2? certificate = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                certificate = X509CertificateLoader.LoadPkcs12FromFile(path, section["Password"]);
            }
            else if (!string.IsNullOrWhiteSpace(thumbprint))
            {
                certificate = FromStore(thumbprint, section["StoreLocation"], section["StoreName"]);
            }
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            LogNotLoaded(logger, name, exception.GetType().Name);
        }

        if (certificate is null || !certificate.HasPrivateKey)
        {
            certificate?.Dispose();
            return Invalid();
        }
        // A replaced certificate is not disposed: a cached connection may still use it until it ends.
        _loaded[name] = new LoadedCertificate(fingerprint, certificate);
        return TaskResult<X509Certificate2>.Succeeded(certificate);
    }

    public void Dispose()
    {
        foreach (var loaded in _loaded.Values)
        {
            loaded.Certificate.Dispose();
        }
        _loaded.Clear();
    }

    private static X509Certificate2? FromStore(string thumbprint, string? storeLocation, string? storeName)
    {
        var location = Enum.TryParse<StoreLocation>(storeLocation, ignoreCase: true, out var parsedLocation)
            ? parsedLocation
            : StoreLocation.CurrentUser;
        var name = Enum.TryParse<StoreName>(storeName, ignoreCase: true, out var parsedName)
            ? parsedName
            : StoreName.My;
        using var store = new X509Store(name, location);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint.Replace(" ", string.Empty, StringComparison.Ordinal), validOnly: false);
        return found.Count == 1 ? found[0] : null;
    }

    private static string Fingerprint(params string?[] values)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', values.Select(value => value ?? string.Empty)))));
    }

    private static TaskResult<X509Certificate2> Invalid()
    {
        return TaskResult<X509Certificate2>.Failed(
            NhAssistantAdminErrorCodes.McpCertificateReferenceInvalid,
            "The client certificate is not configured or has no private key.");
    }

    private sealed record LoadedCertificate(string Fingerprint, X509Certificate2 Certificate);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    [LoggerMessage(Level = LogLevel.Warning, Message = "The MCP client certificate {Name} could not be loaded ({ExceptionType}).")]
    private static partial void LogNotLoaded(ILogger logger, string name, string exceptionType);
}
