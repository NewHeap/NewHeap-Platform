namespace SampleProjectManagement.Api.Composition;

/// <summary>
/// SPM-075: where the password recovery mail sends its reader. The reset link is built from this
/// configuration alone, never from the anonymous request: not from a reset URL in its body and not
/// from its <c>Host</c> or <c>Origin</c>, so a requester cannot make the application mail a valid
/// reset token to a site of their choosing.
/// </summary>
public sealed class SampleAccountRecoveryOptions
{
    public const string SectionName = "AccountRecovery";

    /// <summary>The origin of the application that shows the reset page, such as <c>https://app.example.test</c>.</summary>
    public string ApplicationOrigin { get; set; } = "";

    /// <summary>The path of the reset page on <see cref="ApplicationOrigin"/>.</summary>
    public string ResetPasswordPath { get; set; } = "/reset-password";

    /// <summary>The reset page's absolute URL, without the query that carries the user id and token.</summary>
    public Uri ResetPasswordPage()
    {
        return new Uri(new Uri(ApplicationOrigin), ResetPasswordPath);
    }

    internal bool IsValid()
    {
        if (!Uri.TryCreate(ApplicationOrigin, UriKind.Absolute, out var origin)
            || origin.Scheme is not ("https" or "http")
            || !string.IsNullOrEmpty(origin.UserInfo)
            || origin.AbsolutePath != "/"
            || !string.IsNullOrEmpty(origin.Query)
            || !string.IsNullOrEmpty(origin.Fragment))
        {
            return false;
        }

        return ResetPasswordPath.StartsWith('/')
            && !ResetPasswordPath.StartsWith("//", StringComparison.Ordinal)
            && !ResetPasswordPath.Contains('\\')
            && !ResetPasswordPath.Contains('?')
            && !ResetPasswordPath.Contains('#');
    }
}

public static class SampleAccountRecoveryComposition
{
    /// <summary>
    /// Binds <see cref="SampleAccountRecoveryOptions"/> from <c>AccountRecovery</c> and refuses to
    /// start without a valid application origin and reset path.
    /// </summary>
    public static IServiceCollection AddSampleAccountRecovery(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SampleAccountRecoveryOptions>()
            .Bind(configuration.GetSection(SampleAccountRecoveryOptions.SectionName))
            .Validate(
                options => options.IsValid(),
                "AccountRecovery:ApplicationOrigin must be the http(s) origin of the application, without a path, " +
                "and AccountRecovery:ResetPasswordPath a path on it that starts with a single '/'.")
            .ValidateOnStart();

        return services;
    }
}
