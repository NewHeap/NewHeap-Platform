using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.AspNet.Common.Services.Notification;
using System.Net.Mail;

namespace NewHeap.Platform.AspNet.Common.Authentication.TwoFactor;

/// <summary>
/// Fails startup when two-factor authentication is enabled but the authentication service
/// cannot enforce it, instead of silently issuing sessions without a second factor.
/// </summary>
internal sealed class NhTwoFactorStartupValidator : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly AuthenticationConfiguration _authenticationConfiguration;
    private readonly NhTwoFactorConfiguration _twoFactorConfiguration;
    private readonly IHostEnvironment _hostEnvironment;
    private readonly ILogger<NhTwoFactorStartupValidator> _logger;

    public NhTwoFactorStartupValidator(
        IServiceProvider serviceProvider,
        AuthenticationConfiguration authenticationConfiguration,
        NhTwoFactorConfiguration twoFactorConfiguration,
        IHostEnvironment hostEnvironment,
        ILogger<NhTwoFactorStartupValidator> logger)
    {
        _serviceProvider = serviceProvider;
        _authenticationConfiguration = authenticationConfiguration;
        _twoFactorConfiguration = twoFactorConfiguration;
        _hostEnvironment = hostEnvironment;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();

        var authenticationService = string.IsNullOrEmpty(_authenticationConfiguration.AuthenticationServiceKey)
            ? scope.ServiceProvider.GetRequiredService<INhAuthenticationService>()
            : scope.ServiceProvider.GetRequiredKeyedService<INhAuthenticationService>(
                _authenticationConfiguration.AuthenticationServiceKey);

        if (authenticationService is not INhMultiFactorAuthenticationService { IsTwoFactorAvailable: true })
        {
            throw new InvalidOperationException(
                $"Two-factor authentication is enabled, but {authenticationService.GetType().Name} cannot enforce it. " +
                "Derive from NhAuthenticationService, add an NhTwoFactorAuthenticationContext<TUser> constructor " +
                "parameter and pass it to the base constructor.");
        }

        ValidateNotifications(scope.ServiceProvider);
        await ValidateAdministrationPolicyAsync(scope.ServiceProvider);

        var keyManagementOptions = scope.ServiceProvider.GetRequiredService<IOptions<KeyManagementOptions>>().Value;
        if (!_hostEnvironment.IsDevelopment() && keyManagementOptions.XmlRepository == null)
        {
            _logger.LogWarning(
                "Two-factor challenges are protected with ASP.NET Core Data Protection, but no key repository is " +
                "configured. Persist the key ring to storage shared by every instance, otherwise challenges fail " +
                "after a restart or on another instance.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private void ValidateNotifications(IServiceProvider services)
    {
        var sendsEmail = _twoFactorConfiguration.EmailCodesEnabled
            || (_twoFactorConfiguration.SecurityNotificationsEnabled && _twoFactorConfiguration.SecurityNotificationsByEmail);
        var needsNotifications = _twoFactorConfiguration.EmailCodesEnabled || _twoFactorConfiguration.SecurityNotificationsEnabled;

        if (needsNotifications && services.GetService<INhNotificationService>() == null)
        {
            throw new InvalidOperationException(
                "E-mail codes and security notifications for two-factor authentication require WithNotifications(...).");
        }

        var usesDefaultComposer = services.GetRequiredService<INhTwoFactorMessageComposer>().GetType() == typeof(NhTwoFactorMessageComposer);
        if (!sendsEmail || !usesDefaultComposer)
        {
            return;
        }

        // The default composer relies on the e-mail dispatcher's default sender.
        var emailSettings = services.GetRequiredService<IOptions<NhEmailNotificationSettings>>().Value;
        if (!emailSettings.AllowDefaultFromAddress
            || string.IsNullOrWhiteSpace(emailSettings.DefaultFromAddress)
            || !MailAddress.TryCreate(emailSettings.DefaultFromAddress, out _))
        {
            throw new InvalidOperationException(
                "Two-factor e-mails use the default sender of the e-mail dispatcher. Configure " +
                "NhEmailNotificationSettings.AllowDefaultFromAddress and DefaultFromAddress, or register a message composer " +
                "with UseMessageComposer<T>() that sets the sender.");
        }
    }

    private async Task ValidateAdministrationPolicyAsync(IServiceProvider services)
    {
        if (_twoFactorConfiguration.AdministrationPolicy == null)
        {
            return;
        }

        var policyProvider = services.GetRequiredService<IAuthorizationPolicyProvider>();
        if (await policyProvider.GetPolicyAsync(_twoFactorConfiguration.AdministrationPolicy) == null)
        {
            throw new InvalidOperationException(
                $"The two-factor administration policy '{_twoFactorConfiguration.AdministrationPolicy}' is not registered.");
        }
    }
}
