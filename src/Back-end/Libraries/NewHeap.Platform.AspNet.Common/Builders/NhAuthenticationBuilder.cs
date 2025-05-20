using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AspNet.Common.Authentication;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models.View;
using NewHeap.Platform.AspNet.Common.Services;
using System.Security.Claims;

namespace NewHeap.Platform.AspNet.Common.Builders;

public class NhAuthenticationBuilder<
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
    where TDivisionRole : NhDivisionRole<TDivisionUserRole, TDivisionRoleClaim, TDivisionUser, TDivisionRole, TDivision, TUser>
    where TDivisionUser : NhDivisionUser<TDivisionUserRole, TDivisionUser, TDivisionRole, TDivisionRoleClaim, TDivision, TUser>
    where TDivisionUserRole : NhDivisionUserRole<TDivisionUser, TDivisionRole, TDivisionRoleClaim, TDivisionUserRole, TDivision, TUser>
    where TDivisionRoleClaim : NhDivisionRoleClaim
    where TUserViewModel : NhUserViewModel<TDivisionViewModel>
    where TDivisionViewModel : NhDivisionViewModel
    where TClaimViewModel : NhClaimViewModel
{
    private Type AuthenticationServiceType { get; set; } = typeof(NhAuthenticationService<TUser, TDivision, TDivisionUser, TDivisionRole, TDivisionUserRole, TDivisionRoleClaim>);
    
    private UserNamePasswordOptions UserNamePasswordOptionsValue { get; set; } = new();
    
    
    internal NhAuthenticationBuilder()
    {
    }

    public NhAuthenticationBuilder<
    TUser,
    TDivision,
    TDivisionUser,
    TDivisionRole,
    TDivisionUserRole,
    TDivisionRoleClaim,
    TUserViewModel,
    TDivisionViewModel,
    TClaimViewModel
    > WithAuthenticationService<T>() where T : INhAuthenticationService
    {
        AuthenticationServiceType = typeof(T);
        return this;
    }

    public NhAuthenticationBuilder<
    TUser,
    TDivision,
    TDivisionUser,
    TDivisionRole,
    TDivisionUserRole,
    TDivisionRoleClaim,
    TUserViewModel,
    TDivisionViewModel,
    TClaimViewModel
    > AddUserNamePasswordAuthentication(Action<UserNamePasswordOptions>? configure = null)
    {
        var options = new UserNamePasswordOptions();
        configure?.Invoke(this.UserNamePasswordOptionsValue);

        return this;
    }

    public class UserNamePasswordOptions
    {
        internal UserNamePasswordOptions()
        {
        }
        
        public bool EnableRefreshToken { get; set; } = true;
        public bool Enabled { get; set; } = true;
        public string? Endpoint { get; set; }
        public string? RefreshTokenEndpoint { get; set; }

        public string? AccessTokenCookieName { get; set; }
        public string? RefreshTokenCookieName { get; set; }

        public string? LogoutEndpoint { get; set; }

        public string? AccountInformationEndpoint { get; set; }
        public bool EnableDivisions { get; set; } = false;
        public bool EnableImpersonate { get; set; } = false;
        public string? AuthenticationServiceKey { get; set; }
        public List<Claim> AuthenticateRequiredClaims { get; set; } = new List<Claim>();
    }

    internal void Build(IServiceCollection services)
    {
        services.AddAuthentication(opt =>
        {
            
        });
        
        var authConfig = new AuthenticationConfiguration
        {
            RefreshTokenEnabled = UserNamePasswordOptionsValue.EnableRefreshToken,
            DivisionsEnabled = UserNamePasswordOptionsValue.EnableDivisions,
            ImpersonateEnabled = UserNamePasswordOptionsValue.EnableImpersonate,
            CookieName = UserNamePasswordOptionsValue.AccessTokenCookieName,
            RefreshCookieName = UserNamePasswordOptionsValue.RefreshTokenCookieName,
            RefreshTokenEndpoint = UserNamePasswordOptionsValue.RefreshTokenEndpoint,
            AuthenticationEndpoint = UserNamePasswordOptionsValue.Endpoint,
            AccountInformationEndpoint = UserNamePasswordOptionsValue.AccountInformationEndpoint,
            AuthenticationServiceKey = UserNamePasswordOptionsValue.AuthenticationServiceKey,
            AuthenticateRequiredClaims = UserNamePasswordOptionsValue.AuthenticateRequiredClaims,
        };
        services.AddSingleton(authConfig);
        
        services.AddScoped(typeof(INhAuthenticationService), AuthenticationServiceType);
        if (UserNamePasswordOptionsValue.Enabled)
        {
            AddUserNameLoginHandler(services);
            if (UserNamePasswordOptionsValue.EnableRefreshToken)
            {
                AddRefreshTokenHandler(services);
            }

            if (UserNamePasswordOptionsValue.EnableImpersonate)
            { 
                AddImpersonateHandler(services);
            }
        }
    }

    private void AddRefreshTokenHandler(IServiceCollection services)
    {
        services.AddSingleton<NhRefreshTokenAuthenticationHandler>();
    }

    private void AddImpersonateHandler(IServiceCollection services)
    {
        services.AddSingleton<NhImpersonateAuthenticationHandler>();
        services.AddSingleton<NhRevertImpersonateAuthenticationHandler>();
    }

    private void AddUserNameLoginHandler(IServiceCollection services)
    {
        services.AddSingleton<NhUserNamePasswordAuthenticationHandler>();
        services.AddSingleton<NhAccountInformationEndpointHandler<
            TUser,
            TDivision,
            TDivisionUser,
            TDivisionRole,
            TDivisionUserRole,
            TDivisionRoleClaim,
            TUserViewModel,
            TDivisionViewModel,
            TClaimViewModel
        >>();
        services.AddSingleton<NhLogoutAuthenticationHandler>();
    }
}