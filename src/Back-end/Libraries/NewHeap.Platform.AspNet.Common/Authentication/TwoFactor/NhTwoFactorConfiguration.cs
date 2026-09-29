namespace NewHeap.Platform.AspNet.Common.Authentication.TwoFactor;

/// <summary>
/// Runtime two-factor configuration created by <see cref="NhTwoFactorBuilder"/>.
/// </summary>
public sealed class NhTwoFactorConfiguration
{
    internal NhTwoFactorConfiguration()
    {
    }

    /// <summary>Whether two-factor authentication was added to the application.</summary>
    public bool Enabled { get; internal set; }

    public bool AuthenticatorEnabled { get; internal set; }

    /// <summary>Issuer shown in authenticator apps. Defaults to the application name.</summary>
    public string? AuthenticatorIssuer { get; internal set; }

    public bool RecoveryCodesEnabled { get; internal set; }

    public int RecoveryCodeCount { get; internal set; } = 10;

    /// <summary>How long a second-factor challenge remains valid.</summary>
    public TimeSpan ChallengeLifetime { get; internal set; } = TimeSpan.FromMinutes(5);

    /// <summary>Methods the application offers for enrollment.</summary>
    public IReadOnlyList<string> AvailableMethods
    {
        get
        {
            var methods = new List<string>();
            if (AuthenticatorEnabled)
            {
                methods.Add(NhTwoFactorMethods.Authenticator);
            }

            if (RecoveryCodesEnabled)
            {
                methods.Add(NhTwoFactorMethods.RecoveryCode);
            }

            return methods;
        }
    }

    internal static NhTwoFactorConfiguration Disabled()
    {
        return new NhTwoFactorConfiguration { Enabled = false };
    }

    internal void Validate()
    {
        if (!Enabled)
        {
            return;
        }

        if (!AuthenticatorEnabled)
        {
            throw new InvalidOperationException(
                "Two-factor authentication needs at least one enrollable method. Call EnableAuthenticator().");
        }

        if (RecoveryCodeCount is < 1 or > 50)
        {
            throw new InvalidOperationException("The two-factor recovery code count must be between 1 and 50.");
        }

        if (ChallengeLifetime < TimeSpan.FromMinutes(1) || ChallengeLifetime > TimeSpan.FromMinutes(30))
        {
            throw new InvalidOperationException("The two-factor challenge lifetime must be between 1 and 30 minutes.");
        }
    }
}

/// <summary>
/// Options for authenticator-app (TOTP) enrollment.
/// </summary>
public sealed class NhAuthenticatorOptions
{
    internal NhAuthenticatorOptions()
    {
    }

    /// <summary>Issuer shown in authenticator apps. Defaults to the application name.</summary>
    public string? Issuer { get; set; }
}

/// <summary>
/// Configures two-factor authentication on the NewHeap authentication builder.
/// </summary>
public sealed class NhTwoFactorBuilder
{
    internal NhTwoFactorBuilder()
    {
    }

    internal NhTwoFactorConfiguration Configuration { get; } = new() { Enabled = true };

    internal Type PolicyType { get; private set; } = typeof(NhDefaultTwoFactorPolicy);

    /// <summary>
    /// Lets users enroll an authenticator app that generates time-based one-time passwords.
    /// </summary>
    public NhTwoFactorBuilder EnableAuthenticator(Action<NhAuthenticatorOptions>? configure = null)
    {
        var options = new NhAuthenticatorOptions();
        configure?.Invoke(options);

        Configuration.AuthenticatorEnabled = true;
        Configuration.AuthenticatorIssuer = string.IsNullOrWhiteSpace(options.Issuer) ? null : options.Issuer.Trim();
        return this;
    }

    /// <summary>
    /// Issues single-use recovery codes when a user enables two-factor authentication.
    /// </summary>
    public NhTwoFactorBuilder EnableRecoveryCodes(int count = 10)
    {
        Configuration.RecoveryCodesEnabled = true;
        Configuration.RecoveryCodeCount = count;
        return this;
    }

    /// <summary>
    /// Sets how long a second-factor challenge remains valid. The default is five minutes.
    /// </summary>
    public NhTwoFactorBuilder WithChallengeLifetime(TimeSpan lifetime)
    {
        Configuration.ChallengeLifetime = lifetime;
        return this;
    }

    /// <summary>
    /// Replaces the policy that decides when a second factor is required. The policy is
    /// resolved per request scope.
    /// </summary>
    public NhTwoFactorBuilder UsePolicy<TPolicy>()
        where TPolicy : class, INhTwoFactorPolicy
    {
        PolicyType = typeof(TPolicy);
        return this;
    }
}
