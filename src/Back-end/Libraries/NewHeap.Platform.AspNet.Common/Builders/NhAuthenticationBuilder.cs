using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AspNet.Common.Authentication;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Services;

namespace NewHeap.Platform.AspNet.Common.Builders;

public class NhAuthenticationBuilder<
    TUser,
    TDivision,
    TDivisionUser,
    TDivisionRole,
    TDivisionUserRole,
    TDivisionRoleClaim
    >
    where TUser : NhUser<TDivision, TDivisionUser, TDivisionUserRole, TDivisionRole, TDivisionRoleClaim, TUser>
    where TDivision : NhDivision<TDivisionUser, TDivisionUserRole, TDivisionRole, TDivisionRoleClaim, TDivision, TUser>
    where TDivisionRole : NhDivisionRole<TDivisionUserRole, TDivisionRoleClaim, TDivisionUser, TDivisionRole, TDivision, TUser>
    where TDivisionUser : NhDivisionUser<TDivisionUserRole, TDivisionUser, TDivisionRole, TDivisionRoleClaim, TDivision, TUser>
    where TDivisionUserRole : NhDivisionUserRole<TDivisionUser, TDivisionRole, TDivisionRoleClaim, TDivisionUserRole, TDivision, TUser>
    where TDivisionRoleClaim : NhDivisionRoleClaim
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
    TDivisionRoleClaim
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
    TDivisionRoleClaim
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
            CookieName = UserNamePasswordOptionsValue.AccessTokenCookieName,
            RefreshCookieName = UserNamePasswordOptionsValue.RefreshTokenCookieName,
            RefreshTokenEndpoint = UserNamePasswordOptionsValue.RefreshTokenEndpoint,
            AuthenticationEndpoint = UserNamePasswordOptionsValue.Endpoint,
            AccountInformationEndpoint = UserNamePasswordOptionsValue.AccountInformationEndpoint
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
        }
    }

    private void AddRefreshTokenHandler(IServiceCollection services)
    {
        services.AddTransient<NhRefreshTokenAuthenticationHandler>();
    }
    
    private void AddUserNameLoginHandler(IServiceCollection services)
    {
        services.AddTransient<NhUserNamePasswordAuthenticationHandler>();
        services.AddTransient<NhAccountInformationEndpointHandler<
            TUser,
            TDivision,
            TDivisionUser,
            TDivisionRole,
            TDivisionUserRole,
            TDivisionRoleClaim
        >>();
        services.AddTransient<NhLogoutAuthenticationHandler>();
    }
}