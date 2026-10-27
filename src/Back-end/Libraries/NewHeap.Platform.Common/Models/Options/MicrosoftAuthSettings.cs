namespace NewHeap.Platform.Common.Models.Options;

public class MicrosoftAuthSettings
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string CallbackUrl { get; set; } = "";
    public string ProfileEndpoint { get; set; } = "";
    public string[] AuthDomains { get; set; } = [];

    /// <summary>
    /// The absolute origins, such as <c>https://app.example.test</c>, that the Microsoft OAuth
    /// callback may return the browser to, besides paths on the callback's own host. When it is
    /// empty, the callback uses <c>NewHeapAspNetCommonSettings.AllowedOrigins</c>, the origins the
    /// application already allows to call the API.
    /// </summary>
    public string[] AllowedReturnOrigins { get; set; } = [];

    /// <summary>
    /// The directory (tenant) ids, as GUIDs, whose accounts may sign in. When it is empty, every
    /// tenant that the <see cref="TenantId"/> authority accepts may sign in, but only to local
    /// accounts that are already linked, unless <see cref="TenantId"/> itself is a tenant id.
    /// </summary>
    public string[] AllowedTenantIds { get; set; } = [];

    /// <summary>
    /// Whether a Microsoft account that is not linked to a local account yet may be linked, once,
    /// to the local account whose user name equals its Graph <c>mail</c>. Only an account of a tenant
    /// the application names, in <see cref="AllowedTenantIds"/> or as <see cref="TenantId"/>, is
    /// linked, and only to a local account without a Microsoft link; later sign-ins find the
    /// account by the stored tenant and object id. Turn it off once every account is linked, or to
    /// link accounts only through the application. Defaults to <see langword="true"/>.
    /// </summary>
    public bool LinkAccountsByEmail { get; set; } = true;
}