using Microsoft.Extensions.Options;
using NewHeap.Platform.Common.Models;
using System.Diagnostics.CodeAnalysis;

namespace NewHeap.Platform.AspNet.Common.Authentication;

public class AuthenticationMethodPickerService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly AuthenticationMethodPickerOptions _options;

    public AuthenticationMethodPickerService(IOptions<AuthenticationMethodPickerOptions> options, IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        _options = options.Value!;
    }

    public async Task<TaskResult<string>> GetAuthMethod(string username)
    {
        var method = "";

        if (string.IsNullOrWhiteSpace(username))
        {
            return TaskResult<string>.Failed("Username is required.");
        }
        
        foreach (var candidate in _options.Checks)
        {
            if (candidate(username, ref method, _serviceProvider))
            {
                return method;
            }
        }
        return TaskResult<string>.Failed("No authentication method found");
    }
}

public class AuthenticationMethodPickerOptions
{
    public delegate bool CheckMethod(string username, [NotNullWhen(true)] ref string? methodName, IServiceProvider services);

    private readonly List<CheckMethod> _checks = [];

    public IEnumerable<CheckMethod> Checks => _checks;

    public AuthenticationMethodPickerOptions AddCheck(CheckMethod check)
    {
        _checks.Add(check);
        return this;
    }
}