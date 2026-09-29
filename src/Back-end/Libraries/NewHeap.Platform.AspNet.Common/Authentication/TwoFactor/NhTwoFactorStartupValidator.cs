using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AspNet.Common.Services;

namespace NewHeap.Platform.AspNet.Common.Authentication.TwoFactor;

/// <summary>
/// Fails startup when two-factor authentication is enabled but the authentication service
/// cannot enforce it, instead of silently issuing sessions without a second factor.
/// </summary>
internal sealed class NhTwoFactorStartupValidator : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly AuthenticationConfiguration _authenticationConfiguration;
    private readonly IHostEnvironment _hostEnvironment;
    private readonly ILogger<NhTwoFactorStartupValidator> _logger;

    public NhTwoFactorStartupValidator(
        IServiceProvider serviceProvider,
        AuthenticationConfiguration authenticationConfiguration,
        IHostEnvironment hostEnvironment,
        ILogger<NhTwoFactorStartupValidator> logger)
    {
        _serviceProvider = serviceProvider;
        _authenticationConfiguration = authenticationConfiguration;
        _hostEnvironment = hostEnvironment;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
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

        var keyManagementOptions = scope.ServiceProvider.GetRequiredService<IOptions<KeyManagementOptions>>().Value;
        if (!_hostEnvironment.IsDevelopment() && keyManagementOptions.XmlRepository == null)
        {
            _logger.LogWarning(
                "Two-factor challenges are protected with ASP.NET Core Data Protection, but no key repository is " +
                "configured. Persist the key ring to storage shared by every instance, otherwise challenges fail " +
                "after a restart or on another instance.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
