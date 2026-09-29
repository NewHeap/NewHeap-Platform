namespace NewHeap.Platform.AspNet.Common.Authentication.TwoFactor;

/// <summary>
/// Runtime two-factor configuration created by <see cref="NhTwoFactorBuilder"/>.
/// </summary>
public sealed class NhTwoFactorConfiguration
{
    private readonly List<string> _requiredRoles = [];
    private readonly List<string> _requiredPermissions = [];

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

    public bool EmailCodesEnabled { get; internal set; }

    /// <summary>How long an e-mailed code remains valid.</summary>
    public TimeSpan EmailCodeLifetime { get; internal set; } = TimeSpan.FromMinutes(10);

    /// <summary>Minimum time between two e-mailed codes for the same user.</summary>
    public TimeSpan EmailCodeResendCooldown { get; internal set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Whether an e-mailed code satisfies a second factor that the policy requires. The mailbox
    /// usually also resets the password, so it is not accepted for required users by default.
    /// </summary>
    public bool EmailSatisfiesRequirement { get; internal set; }

    public bool SecurityNotificationsEnabled { get; internal set; }

    public bool SecurityNotificationsByEmail { get; internal set; } = true;

    public bool SecurityNotificationsInApp { get; internal set; } = true;

    public bool RememberDeviceEnabled { get; internal set; }

    public TimeSpan RememberDeviceLifetime { get; internal set; } = TimeSpan.FromDays(30);

    public string RememberDeviceCookieName { get; internal set; } = "nh_two_factor_device";

    /// <summary>How long a second-factor challenge remains valid.</summary>
    public TimeSpan ChallengeLifetime { get; internal set; } = TimeSpan.FromMinutes(5);

    /// <summary>How long a user whom the policy requires to enroll can finish the enrollment.</summary>
    public TimeSpan EnrollmentLifetime { get; internal set; } = TimeSpan.FromMinutes(15);

    /// <summary>Authorization policy of the administration endpoints; <see langword="null"/> disables them.</summary>
    public string? AdministrationPolicy { get; internal set; }

    /// <summary>Whether every user must use a second factor.</summary>
    public bool RequireForAllUsers { get; internal set; }

    /// <summary>Application roles whose members must use a second factor.</summary>
    public IReadOnlyList<string> RequiredRoles => _requiredRoles;

    /// <summary>Application permissions whose holders must use a second factor.</summary>
    public IReadOnlyList<string> RequiredPermissions => _requiredPermissions;

    /// <summary>
    /// Whether a sign-in through an external identity provider such as Microsoft OAuth
    /// satisfies the requirement. The identity provider then owns the MFA policy.
    /// </summary>
    public bool ExternalProvidersSatisfyRequirement { get; internal set; } = true;

    /// <summary>Whether users whom the policy requires to use a second factor may remember a device.</summary>
    public bool RememberDeviceForRequiredUsers { get; internal set; } = true;

    /// <summary>Whether the configuration requires a second factor for anyone.</summary>
    public bool HasRequirements => RequireForAllUsers || _requiredRoles.Count > 0 || _requiredPermissions.Count > 0;

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

            if (EmailCodesEnabled)
            {
                methods.Add(NhTwoFactorMethods.Email);
            }

            if (RecoveryCodesEnabled)
            {
                methods.Add(NhTwoFactorMethods.RecoveryCode);
            }

            return methods;
        }
    }

    internal void AddRequiredRoles(IEnumerable<string> roles)
    {
        foreach (var role in roles)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(role);
            if (!_requiredRoles.Contains(role.Trim(), StringComparer.Ordinal))
            {
                _requiredRoles.Add(role.Trim());
            }
        }
    }

    internal void AddRequiredPermissions(IEnumerable<string> permissions)
    {
        foreach (var permission in permissions)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(permission);
            if (!_requiredPermissions.Contains(permission.Trim(), StringComparer.Ordinal))
            {
                _requiredPermissions.Add(permission.Trim());
            }
        }
    }

    /// <summary>
    /// Methods a user can enroll to satisfy a requirement of the policy.
    /// </summary>
    internal IReadOnlyList<string> EnrollableMethods(bool requiredByPolicy)
    {
        var methods = new List<string>();
        if (AuthenticatorEnabled)
        {
            methods.Add(NhTwoFactorMethods.Authenticator);
        }

        if (EmailCodesEnabled && (!requiredByPolicy || EmailSatisfiesRequirement))
        {
            methods.Add(NhTwoFactorMethods.Email);
        }

        return methods;
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

        if (!AuthenticatorEnabled && !EmailCodesEnabled)
        {
            throw new InvalidOperationException(
                "Two-factor authentication needs at least one enrollable method. Call EnableAuthenticator() or EnableEmailCodes().");
        }

        if (HasRequirements && !AuthenticatorEnabled && !EmailSatisfiesRequirement)
        {
            throw new InvalidOperationException(
                "A required second factor needs an enrollable method that satisfies it. Call EnableAuthenticator() or allow e-mail codes for required users.");
        }

        if (RecoveryCodeCount is < 1 or > 50)
        {
            throw new InvalidOperationException("The two-factor recovery code count must be between 1 and 50.");
        }

        if (ChallengeLifetime < TimeSpan.FromMinutes(1) || ChallengeLifetime > TimeSpan.FromMinutes(30))
        {
            throw new InvalidOperationException("The two-factor challenge lifetime must be between 1 and 30 minutes.");
        }

        if (EnrollmentLifetime < TimeSpan.FromMinutes(1) || EnrollmentLifetime > TimeSpan.FromHours(2))
        {
            throw new InvalidOperationException("The two-factor enrollment lifetime must be between 1 minute and 2 hours.");
        }

        if (EmailCodeLifetime < TimeSpan.FromMinutes(1) || EmailCodeLifetime > TimeSpan.FromMinutes(30))
        {
            throw new InvalidOperationException("The e-mail code lifetime must be between 1 and 30 minutes.");
        }

        if (EmailCodeResendCooldown < TimeSpan.Zero || EmailCodeResendCooldown >= EmailCodeLifetime)
        {
            throw new InvalidOperationException("The e-mail code resend cooldown must be shorter than the code lifetime.");
        }

        if (RememberDeviceLifetime < TimeSpan.FromHours(1) || RememberDeviceLifetime > TimeSpan.FromDays(365))
        {
            throw new InvalidOperationException("The remember-device lifetime must be between 1 hour and 365 days.");
        }

        if (AdministrationPolicy is not null && string.IsNullOrWhiteSpace(AdministrationPolicy))
        {
            throw new InvalidOperationException("The two-factor administration policy must be null or a policy name.");
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
/// Options for codes sent by e-mail.
/// </summary>
public sealed class NhEmailCodeOptions
{
    internal NhEmailCodeOptions()
    {
    }

    public TimeSpan CodeLifetime { get; set; } = TimeSpan.FromMinutes(10);

    public TimeSpan ResendCooldown { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Whether an e-mailed code satisfies a second factor that the policy requires. Keep this
    /// off when the same mailbox can reset the password.
    /// </summary>
    public bool SatisfiesRequirement { get; set; }
}

/// <summary>
/// Options for security notifications about two-factor changes.
/// </summary>
public sealed class NhSecurityNotificationOptions
{
    internal NhSecurityNotificationOptions()
    {
    }

    public bool Email { get; set; } = true;

    public bool InApp { get; set; } = true;
}

/// <summary>
/// Selects who must use a second factor. Requirements add up: a user who matches any of
/// them must enroll before a session is issued.
/// </summary>
public sealed class NhTwoFactorRequirementOptions
{
    private readonly NhTwoFactorConfiguration _configuration;

    internal NhTwoFactorRequirementOptions(NhTwoFactorConfiguration configuration)
    {
        _configuration = configuration;
    }

    /// <summary>Requires a second factor for members of the application roles.</summary>
    public NhTwoFactorRequirementOptions Roles(params string[] roles)
    {
        _configuration.AddRequiredRoles(roles);
        return this;
    }

    /// <summary>Requires a second factor for holders of the application permissions.</summary>
    public NhTwoFactorRequirementOptions Permissions(params string[] permissions)
    {
        _configuration.AddRequiredPermissions(permissions);
        return this;
    }

    /// <summary>Requires a second factor for every user.</summary>
    public NhTwoFactorRequirementOptions AllUsers()
    {
        _configuration.RequireForAllUsers = true;
        return this;
    }

    /// <summary>
    /// Also requires the NewHeap second factor after a Microsoft OAuth sign-in. By default the
    /// identity provider's MFA policy is trusted.
    /// </summary>
    public NhTwoFactorRequirementOptions IncludingExternalProviders()
    {
        _configuration.ExternalProvidersSatisfyRequirement = false;
        return this;
    }

    /// <summary>Asks required users for their second factor on every sign-in.</summary>
    public NhTwoFactorRequirementOptions WithoutRememberedDevices()
    {
        _configuration.RememberDeviceForRequiredUsers = false;
        return this;
    }
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

    internal Type? MessageComposerType { get; private set; }

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
    /// Lets users confirm their e-mail address as a second factor and receive sign-in codes
    /// by e-mail. Requires <c>WithNotifications(...)</c>.
    /// </summary>
    public NhTwoFactorBuilder EnableEmailCodes(Action<NhEmailCodeOptions>? configure = null)
    {
        var options = new NhEmailCodeOptions();
        configure?.Invoke(options);

        Configuration.EmailCodesEnabled = true;
        Configuration.EmailCodeLifetime = options.CodeLifetime;
        Configuration.EmailCodeResendCooldown = options.ResendCooldown;
        Configuration.EmailSatisfiesRequirement = options.SatisfiesRequirement;
        return this;
    }

    /// <summary>
    /// Notifies users about two-factor changes on their account through the NewHeap
    /// notification pipeline. Requires <c>WithNotifications(...)</c>.
    /// </summary>
    public NhTwoFactorBuilder UseSecurityNotifications(Action<NhSecurityNotificationOptions>? configure = null)
    {
        var options = new NhSecurityNotificationOptions();
        configure?.Invoke(options);

        Configuration.SecurityNotificationsEnabled = true;
        Configuration.SecurityNotificationsByEmail = options.Email;
        Configuration.SecurityNotificationsInApp = options.InApp;
        return this;
    }

    /// <summary>
    /// Lets users skip the second factor on a device they chose to remember. Every credential
    /// change forgets all remembered devices.
    /// </summary>
    public NhTwoFactorBuilder EnableRememberDevice(TimeSpan? lifetime = null)
    {
        Configuration.RememberDeviceEnabled = true;
        if (lifetime.HasValue)
        {
            Configuration.RememberDeviceLifetime = lifetime.Value;
        }

        return this;
    }

    /// <summary>
    /// Requires a second factor for selected users, for example
    /// <c>RequireFor(r => r.Roles("administrator"))</c>. Required users without a second
    /// factor enroll during sign-in before they receive a session.
    /// </summary>
    public NhTwoFactorBuilder RequireFor(Action<NhTwoFactorRequirementOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(new NhTwoFactorRequirementOptions(Configuration));
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
    /// Sets how long a required user can finish enrollment during sign-in. The default is
    /// fifteen minutes.
    /// </summary>
    public NhTwoFactorBuilder WithEnrollmentLifetime(TimeSpan lifetime)
    {
        Configuration.EnrollmentLifetime = lifetime;
        return this;
    }

    /// <summary>
    /// Enables the administration endpoints (reset a user's second factor, send enrollment
    /// reminders, end sessions of users who must enroll) for users who satisfy
    /// <paramref name="policyName"/>. Startup fails when the policy is not registered.
    /// </summary>
    public NhTwoFactorBuilder UseAdministrationPolicy(string policyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);
        Configuration.AdministrationPolicy = policyName.Trim();
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

    /// <summary>
    /// Replaces the texts of e-mailed codes and security notifications, for example to use
    /// branded templates.
    /// </summary>
    public NhTwoFactorBuilder UseMessageComposer<TComposer>()
        where TComposer : class, INhTwoFactorMessageComposer
    {
        MessageComposerType = typeof(TComposer);
        return this;
    }
}
