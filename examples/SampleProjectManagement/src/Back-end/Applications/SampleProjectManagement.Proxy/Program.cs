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

    Console.WriteLine(new PasswordHasher<string>().HashPassword("administrator", password));
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddNewHeapProxy(builder.Configuration.GetSection(NhProxyOptions.ConfigurationSectionName));

var app = builder.Build();
app.UseNewHeapProxy();
app.MapGet("/projects", () => "The project overview has moved here.")
    .WithSummary("Open the redirect destination")
    .WithDescription("A neutral landing page for testing a saved /old-projects redirect.")
    .Produces<string>().AllowAnonymous();
app.Run();
