namespace NewHeap.Media.Modules;

public interface IAuthorizationModule
{
    public Task IsAuthorized(AuthorizationContext context);
}

public class DefaultAuthorizationModule : IAuthorizationModule
{
    public Task IsAuthorized(AuthorizationContext context)
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