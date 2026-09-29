using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AspNet.Common.Authentication;
using NewHeap.Platform.AspNet.Common.Authentication.TwoFactor;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models.View;

namespace NewHeap.Platform.AspNet.Common.Builders;

public class NhAuthenticationConfigurationBuilder<
    TUser,
    TDivision,
    TDivisionUser,
    TDivisionRole,
    TDivisionUserRole,
    TDivisionRoleClaim,
    TUserViewModel,
    TDivisionViewModel,
    TClaimViewModel
>
    where TUser : NhUser<TDivision, TDivisionUser, TDivisionUserRole, TDivisionRole, TDivisionRoleClaim, TUser>
    where TDivision : NhDivision<TDivisionUser, TDivisionUserRole, TDivisionRole, TDivisionRoleClaim, TDivision, TUser>
    where TDivisionRole : NhDivisionRole<TDivisionUserRole, TDivisionRoleClaim, TDivisionUser, TDivisionRole, TDivision,
        TUser>
    where TDivisionUser : NhDivisionUser<TDivisionUserRole, TDivisionUser, TDivisionRole, TDivisionRoleClaim, TDivision,
        TUser>
    where TDivisionUserRole : NhDivisionUserRole<TDivisionUser, TDivisionRole, TDivisionRoleClaim, TDivisionUserRole,
        TDivision, TUser>
    where TDivisionRoleClaim : NhDivisionRoleClaim
    where TUserViewModel : NhUserViewModel<TDivisionViewModel>
    where TDivisionViewModel : NhDivisionViewModel
    where TClaimViewModel : NhClaimViewModel
{
    private readonly List<IAuthenticationEndpoint> _endpoints = [];
    private readonly List<Type> _diEndpoints = [];

    /// <summary>
    /// Add endpoint for handling authentication
    /// </summary>
    /// <param name="pattern"></param>
    /// <param name="method"></param>
    /// <param name="handler"></param>
    /// <returns></returns>
    public NhAuthenticationConfigurationBuilder<
        TUser,
        TDivision,
        TDivisionUser,
        TDivisionRole,
        TDivisionUserRole,
        TDivisionRoleClaim,
        TUserViewModel,
        TDivisionViewModel,
        TClaimViewModel
    > AddAuthenticationEndpoint(string pattern, HttpMethod method,
        Delegate handler)
    {
        _endpoints.Add(new AuthenticationEndpoint { Pattern = pattern, Method = method, Handler = handler });

        return this;
    }

    /// <summary>
    /// Add endpoints for handling username password login flow
    /// </summary>
    /// <param name="enableRefreshToken"></param>
    /// <param name="enableImpersonate"></param>
    /// <returns></returns>
    public NhAuthenticationConfigurationBuilder<
        TUser,
        TDivision,
        TDivisionUser,
        TDivisionRole,
        TDivisionUserRole,
        TDivisionRoleClaim,
        TUserViewModel,
        TDivisionViewModel,
        TClaimViewModel
    > AddUserNamePasswordEndpoint(bool enableRefreshToken = true, bool enableImpersonate = true)
    {
        UseAuthenticationEndpoint<NhUserNamePasswordAuthenticationHandler>();
        if (enableRefreshToken)
        {
            UseAuthenticationEndpoint<NhRefreshTokenAuthenticationHandler>();
        }

        UseAuthenticationEndpoint<NhLogoutAuthenticationHandler>();
        if (enableImpersonate)
        {
            UseAuthenticationEndpoint<NhImpersonateAuthenticationHandler>();
            UseAuthenticationEndpoint<NhRevertImpersonateAuthenticationHandler>();
        }
        
        UseAuthenticationEndpoint<NhAccountInformationEndpointHandler<TUser, TDivision, TDivisionUser, TDivisionRole,
            TDivisionUserRole, TDivisionRoleClaim, TUserViewModel, TDivisionViewModel, TClaimViewModel>>();
        return this;
    }

    public NhAuthenticationConfigurationBuilder<
        TUser,
        TDivision,
        TDivisionUser,
        TDivisionRole,
        TDivisionUserRole,
        TDivisionRoleClaim,
        TUserViewModel,
        TDivisionViewModel,
        TClaimViewModel
    > AddMicrosoftOauthEndpoints()
    {
        UseAuthenticationEndpoint<NhLoginMethodHandler>();
        UseAuthenticationEndpoint<NhMicrosoftOauthAuthenticationGetUrlHandler>();
        UseAuthenticationEndpoint<NhMicrosoftOauthAuthenticationAuthorizeHandler<TUser>>();
        return this;
    }

    /// <summary>
    /// Adds the endpoints that complete a second-factor challenge and let signed-in users
    /// manage their two-factor settings. Requires <c>AddTwoFactor(...)</c> on the
    /// authentication builder.
    /// </summary>
    public NhAuthenticationConfigurationBuilder<
        TUser,
        TDivision,
        TDivisionUser,
        TDivisionRole,
        TDivisionUserRole,
        TDivisionRoleClaim,
        TUserViewModel,
        TDivisionViewModel,
        TClaimViewModel
    > AddTwoFactorEndpoints()
    {
        UseAuthenticationEndpoint<NhTwoFactorVerifyAuthenticationHandler>();
        UseAuthenticationEndpoint<NhTwoFactorEmailCodeAuthenticationHandler>();
        UseAuthenticationEndpoint<NhTwoFactorEnrollmentAuthenticatorSetupHandler>();
        UseAuthenticationEndpoint<NhTwoFactorEnrollmentAuthenticatorConfirmHandler>();
        UseAuthenticationEndpoint<NhTwoFactorEnrollmentEmailHandler>();
        UseAuthenticationEndpoint<NhTwoFactorEnrollmentEmailConfirmHandler>();
        UseAuthenticationEndpoint<NhTwoFactorStatusEndpointHandler<TUser>>();
        UseAuthenticationEndpoint<NhTwoFactorAuthenticatorSetupEndpointHandler<TUser>>();
        UseAuthenticationEndpoint<NhTwoFactorAuthenticatorConfirmEndpointHandler<TUser>>();
        UseAuthenticationEndpoint<NhTwoFactorEmailSetupEndpointHandler<TUser>>();
        UseAuthenticationEndpoint<NhTwoFactorEmailConfirmEndpointHandler<TUser>>();
        UseAuthenticationEndpoint<NhTwoFactorRecoveryCodesEndpointHandler<TUser>>();
        UseAuthenticationEndpoint<NhTwoFactorDisableEndpointHandler<TUser>>();
        UseAuthenticationEndpoint<NhTwoFactorForgetDevicesEndpointHandler<TUser>>();
        UseAuthenticationEndpoint<NhTwoFactorResetEndpointHandler<TUser>>();
        UseAuthenticationEndpoint<NhTwoFactorEnrollmentRemindersEndpointHandler<TUser>>();
        UseAuthenticationEndpoint<NhTwoFactorSessionRevocationEndpointHandler<TUser>>();
        return this;
    }

    /// <summary>
    /// Maps the passkey endpoints: passwordless sign-in, passkeys as a second factor, required
    /// enrollment with a passkey and passkey management for the signed-in user. Requires
    /// <c>EnablePasskeys()</c> in <c>AddTwoFactor(...)</c>.
    /// </summary>
    public NhAuthenticationConfigurationBuilder<
        TUser,
        TDivision,
        TDivisionUser,
        TDivisionRole,
        TDivisionUserRole,
        TDivisionRoleClaim,
        TUserViewModel,
        TDivisionViewModel,
        TClaimViewModel
    > AddPasskeyEndpoints()
    {
        UseAuthenticationEndpoint<NhPasskeySignInOptionsHandler>();
        UseAuthenticationEndpoint<NhPasskeySignInHandler>();
        UseAuthenticationEndpoint<NhTwoFactorPasskeyOptionsHandler>();
        UseAuthenticationEndpoint<NhTwoFactorPasskeyVerifyHandler>();
        UseAuthenticationEndpoint<NhTwoFactorEnrollmentPasskeyOptionsHandler>();
        UseAuthenticationEndpoint<NhTwoFactorEnrollmentPasskeyHandler>();
        UseAuthenticationEndpoint<NhPasskeysEndpointHandler<TUser>>();
        UseAuthenticationEndpoint<NhPasskeyRegistrationOptionsEndpointHandler<TUser>>();
        UseAuthenticationEndpoint<NhPasskeyRegistrationEndpointHandler<TUser>>();
        UseAuthenticationEndpoint<NhPasskeyRenameEndpointHandler<TUser>>();
        UseAuthenticationEndpoint<NhPasskeyRemoveEndpointHandler<TUser>>();
        return this;
    }
    
    /// <summary>
    /// Remove an endpoint
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public NhAuthenticationConfigurationBuilder<
        TUser,
        TDivision,
        TDivisionUser,
        TDivisionRole,
        TDivisionUserRole,
        TDivisionRoleClaim,
        TUserViewModel,
        TDivisionViewModel,
        TClaimViewModel
        > RemoveEndpoint<T>() where T : IAuthenticationEndpoint
    {
        _diEndpoints.Where(x => x == typeof(T)).ToList().ForEach(x => _diEndpoints.Remove(x));
        _endpoints.Where(x => x.GetType() == typeof(T)).ToList().ForEach(x => _endpoints.Remove(x));
        return this;
    }

    /// <summary>
    /// Add endpoints for handling refresh token login flow
    /// </summary>
    /// <returns></returns>
    public NhAuthenticationConfigurationBuilder<
        TUser,
        TDivision,
        TDivisionUser,
        TDivisionRole,
        TDivisionUserRole,
        TDivisionRoleClaim,
        TUserViewModel,
        TDivisionViewModel,
        TClaimViewModel
    > AddRefreshTokenEndpoint()
    {
        UseAuthenticationEndpoint<NhRefreshTokenAuthenticationHandler>();
        return this;
    }

    /// <summary>
    /// Add endpoint for handling authentication flow
    /// </summary>
    /// <typeparam name="TEndpoint"></typeparam>
    /// <returns></returns>
    public NhAuthenticationConfigurationBuilder<
        TUser,
        TDivision,
        TDivisionUser,
        TDivisionRole,
        TDivisionUserRole,
        TDivisionRoleClaim,
        TUserViewModel,
        TDivisionViewModel,
        TClaimViewModel
    > UseAuthenticationEndpoint<TEndpoint>()
        where TEndpoint : IAuthenticationEndpoint
    {
        _diEndpoints.Add(typeof(TEndpoint));
        return this;
    }

    /// <summary>
    /// Add endpoint for handling authentication flow
    /// </summary>
    /// <param name="endpoint"></param>
    /// <returns></returns>
    public NhAuthenticationConfigurationBuilder<
        TUser,
        TDivision,
        TDivisionUser,
        TDivisionRole,
        TDivisionUserRole,
        TDivisionRoleClaim,
        TUserViewModel,
        TDivisionViewModel,
        TClaimViewModel
    > UseAuthenticationEndpoint(IAuthenticationEndpoint endpoint)
    {
        _endpoints.Add(endpoint);
        return this;
    }


    /// <summary>
    /// Build the authentication configuration
    /// </summary>
    /// <param name="app"></param>
    /// <param name="services"></param>
    /// <exception cref="InvalidOperationException"></exception>
    public void Build(IApplicationBuilder app, IServiceProvider services)
    {
        app.UseEndpoints(endpoints =>
        {
            foreach (var type in _diEndpoints)
            {
                var endpoint = (IAuthenticationEndpoint?)services.GetService(type);
                if (endpoint == null)
                {
                    throw new InvalidOperationException(
                        $"The authentication endpoint {type.Name} is not registered. Enable the matching feature in " +
                        "AddAuthentication(...) before mapping its endpoints in UseNhAuthentication(...).");
                }

                ConfigureEndpoint(endpoint, endpoints);
            }

            foreach (var endpoint in _endpoints)
            {
                ConfigureEndpoint(endpoint, endpoints);
            }
        });
        return;

        void ConfigureEndpoint(IAuthenticationEndpoint endpoint, IEndpointRouteBuilder endpoints)
        {
            RouteHandlerBuilder routeHandlerBuilder;
            switch (endpoint.Method)
            {
                case HttpMethod.Get:
                    routeHandlerBuilder = endpoints.MapGet(endpoint.Pattern, endpoint.Handler);
                    break;
                case HttpMethod.Post:
                    routeHandlerBuilder = endpoints.MapPost(endpoint.Pattern, endpoint.Handler);
                    break;
                case HttpMethod.Put:
                    routeHandlerBuilder = endpoints.MapPut(endpoint.Pattern, endpoint.Handler);
                    break;
                case HttpMethod.Delete:
                    routeHandlerBuilder = endpoints.MapDelete(endpoint.Pattern, endpoint.Handler);
                    break;
                case HttpMethod.Patch:
                    routeHandlerBuilder = endpoints.MapPatch(endpoint.Pattern, endpoint.Handler);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Invalid HTTP method {endpoint.Method} for authentication");
            }

            if (endpoint is IConfigurableAuthenticationEndpoint configurableEndpoint)
            {
                configurableEndpoint.Configure(routeHandlerBuilder);
            }
        }
    }
}

public class AuthenticationEndpoint : IAuthenticationEndpoint
{
    public required string Pattern { get; init; }
    public HttpMethod Method { get; init; }
    public required Delegate Handler { get; init; }
}

public interface IAuthenticationEndpoint
{
    public string Pattern { get; }

    public HttpMethod Method { get; }

    public Delegate Handler { get; }
}

/// <summary>
/// Optional extension for an authentication endpoint that adds route metadata, such as
/// authorization, rate limiting or OpenAPI responses, after the endpoint is mapped.
/// </summary>
public interface IConfigurableAuthenticationEndpoint : IAuthenticationEndpoint
{
    void Configure(RouteHandlerBuilder builder);
}

public enum HttpMethod
{
    Get,
    Post,
    Put,
    Delete,
    Patch
}