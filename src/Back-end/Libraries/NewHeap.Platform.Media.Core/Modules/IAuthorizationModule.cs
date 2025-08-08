namespace NewHeap.Media.Modules;

public interface IAuthorizationModule
{
    public Task IsAuthorizedAsync(AuthorizationContext context);
}

/// <summary>
/// Default implementation of <see cref="IAuthorizationModule"/>.
/// Always allows access.
/// </summary>
public class DefaultAuthorizationModule : IAuthorizationModule
{
    public Task IsAuthorizedAsync(AuthorizationContext context)
    {
        context.Authorized = true;
        return Task.CompletedTask;
    }
}

public class AuthorizationContext
{
    public string? Path { get; set; }
    public string? FileName { get; set; }
    public string? Language { get; set; }
    public required ActionType Action { get; set; }
    public bool Authorized { get; set; }
    
    internal AuthorizationContext()
    {
        
    }
}

public enum ActionType
{
    Read,
    Create,
    Update,
    Delete
}