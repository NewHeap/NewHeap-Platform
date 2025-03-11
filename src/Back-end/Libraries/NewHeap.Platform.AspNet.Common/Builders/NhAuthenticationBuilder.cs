using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AspNet.Common.Authentication;
using NewHeap.Platform.AspNet.Common.Services;

namespace NewHeap.Platform.AspNet.Common.Builders;

public class NhAuthenticationBuilder
{
    private Type AuthenticationServiceType { get; set; } = typeof(NhAuthenticationService);
    
    private UserNamePasswordOptions UserNamePasswordOptionsValue { get; set; } = new();
    
    
    internal NhAuthenticationBuilder()
    {
    }

    public NhAuthenticationBuilder WithAuthenticationService<T>() where T : INhAuthenticationService
    {
        AuthenticationServiceType = typeof(T);
        return this;
    }

    public NhAuthenticationBuilder AddUserNamePasswordAuthentication(Action<UserNamePasswordOptions>? configure = null)
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
    }
    
    internal void Build(IServiceCollection services)
    {
        services.AddAuthentication(opt =>
        {
            
        });
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
        services.AddTransient<NhRefreshTokenAuthenticationHandler>(s =>
        {
            var httpContextAccessor = s.GetRequiredService<IHttpContextAccessor>();
            var handler = new NhRefreshTokenAuthenticationHandler(httpContextAccessor);

            if (UserNamePasswordOptionsValue.RefreshTokenEndpoint != null)
            {
                handler.Pattern = UserNamePasswordOptionsValue.RefreshTokenEndpoint;
            }

            if (!string.IsNullOrWhiteSpace(UserNamePasswordOptionsValue.AccessTokenCookieName))
            {
                handler.TokenCookieName = UserNamePasswordOptionsValue.AccessTokenCookieName;
            }

            if (!string.IsNullOrWhiteSpace(UserNamePasswordOptionsValue.RefreshTokenCookieName))
            {
                handler.RefreshTokenCookieName = UserNamePasswordOptionsValue.RefreshTokenCookieName;
            }
                    
            return handler;
        });
    }
    
    private void AddUserNameLoginHandler(IServiceCollection services)
    {
        services.AddTransient<NhUserNamePasswordAuthenticationHandler>(s =>
        {
            var httpContextAccessor = s.GetRequiredService<IHttpContextAccessor>();
            var handler = new NhUserNamePasswordAuthenticationHandler(httpContextAccessor)
            {
                EnableRefreshToken = UserNamePasswordOptionsValue.EnableRefreshToken,
            };

            if (UserNamePasswordOptionsValue.Endpoint != null)
            {
                handler.Pattern = UserNamePasswordOptionsValue.Endpoint;
            }

            if (!string.IsNullOrWhiteSpace(UserNamePasswordOptionsValue.AccessTokenCookieName))
            {
                handler.TokenCookieName = UserNamePasswordOptionsValue.AccessTokenCookieName;
            }

            if (!string.IsNullOrWhiteSpace(UserNamePasswordOptionsValue.RefreshTokenCookieName))
            {
                handler.RefreshTokenCookieName = UserNamePasswordOptionsValue.RefreshTokenCookieName;
            }
                    
            return handler;
        });
        
        services.AddTransient<NhLogoutAuthenticationHandler>(s =>
        {
            var handler = new NhLogoutAuthenticationHandler();

            if (!string.IsNullOrWhiteSpace(UserNamePasswordOptionsValue.LogoutEndpoint))
            {
                handler.Pattern = UserNamePasswordOptionsValue.LogoutEndpoint;
            }

            if (!string.IsNullOrWhiteSpace(UserNamePasswordOptionsValue.AccessTokenCookieName))
            {
                handler.TokenCookieName = UserNamePasswordOptionsValue.AccessTokenCookieName;
            }

            if (!string.IsNullOrWhiteSpace(UserNamePasswordOptionsValue.RefreshTokenCookieName))
            {
                handler.RefreshTokenCookieName = UserNamePasswordOptionsValue.RefreshTokenCookieName;
            }
                    
            return handler;
        });
    }
}