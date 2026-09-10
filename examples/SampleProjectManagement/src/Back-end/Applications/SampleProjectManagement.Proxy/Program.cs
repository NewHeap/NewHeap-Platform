using Microsoft.AspNetCore.Identity;
using NewHeap.Platform.AspNet.Proxy;
using NewHeap.Platform.AspNet.Proxy.Sqlite;

// SPM-238: generate an ASP.NET Identity hash locally; no plaintext credential is stored by the proxy.
if (args.Contains("--hash-password"))
{
    Console.Error.WriteLine("Enter the administrator password (input is hidden):");
    var password = "";
    if (Console.IsInputRedirected)
    {
        password = Console.ReadLine() ?? "";
    }
    else
    {
        ConsoleKeyInfo key;
        while ((key = Console.ReadKey(intercept: true)).Key != ConsoleKey.Enter)
        {
            if (key.Key == ConsoleKey.Backspace && password.Length > 0)
            {
                password = password[..^1];
            }
            else if (!char.IsControl(key.KeyChar))
            {
                password += key.KeyChar;
            }
        }
    }

    if (string.IsNullOrWhiteSpace(password))
    {
        throw new ArgumentException("The password must not be empty.");
    }

    Console.WriteLine(new PasswordHasher<string>().HashPassword("info@newheap.com", password));
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
var demo = builder.Configuration.GetValue<bool>("ProxyDemo");
if (demo && !builder.Environment.IsDevelopment())
{
    throw new InvalidOperationException("ProxyDemo is available only in Development.");
}

var section = builder.Configuration.GetSection(NhProxyOptions.ConfigurationSectionName);
builder.Services.AddNewHeapProxy(options =>
{
    section.Bind(options);
    if (demo)
    {
        // These overrides belong to the opt-in sample, never to the library defaults.
        options.Administrator.UserName = "info@newheap.com";
        options.Administrator.Password = "NewHeap123!";
        options.Administrator.PasswordHash = "";
        options.IpAllowlist.Enabled = true;
        options.IpAllowlist.Entries = ["127.0.0.1", "::1"];
    }
}, storage =>
{
    section.GetSection(NhProxySqliteOptions.ConfigurationSectionName).Bind(storage);
    if (demo)
    {
        storage.DatabasePath = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "proxy-demo.db");
    }
});

await using var app = builder.Build();
app.MapDefaultEndpoints();
app.UseNewHeapProxy();
app.MapGet("/", () => Results.Redirect(NhProxyOptions.AdministrationPath))
    .WithSummary("Open proxy administration")
    .WithDescription("Opens the embedded administration panel, which requires an administrator login.")
    .Produces(StatusCodes.Status302Found).AllowAnonymous();
app.MapGet("/projects", () => "The project overview has moved here.")
    .WithSummary("Open the redirect destination")
    .WithDescription("A neutral landing page for testing a saved /old-projects redirect.")
    .Produces<string>().AllowAnonymous();
if (demo)
{
    await app.StartAsync();
    var configuration = app.Services.GetRequiredService<INhProxyConfigurationService>();
    var current = await configuration.GetRedirectsAsync();
    if (current.Revision == 0 && current.Rules.IsEmpty)
    {
        var saved = await configuration.SaveRedirectsAsync(new(current.Revision,
        [
            new NhProxyRedirectRule
            {
                Id = Guid.NewGuid(), Name = "Moved project overview",
                Match = new NhProxyRedirectMatch { Path = "/old-projects" }, Target = "/projects?source=proxy"
            }
        ]));
        if (!saved.Success)
        {
            Console.Error.WriteLine("The demo redirect could not be initialized. Review the proxy configuration before restarting.");
            Environment.ExitCode = 1;
            await app.StopAsync();
            return;
        }
    }

    Console.WriteLine("Proxy demo ready. Open /newheap-proxy. Test username: administrator | Test password: NewHeap123!");
    Console.WriteLine("Try /old-projects?campaign=demo. Rules persist in App_Data/proxy-demo.db.");
    await app.WaitForShutdownAsync();
}
else
{
    await app.RunAsync();
}
