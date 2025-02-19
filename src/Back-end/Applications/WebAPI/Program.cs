using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using NewHeap.Platform.AspNet;

namespace WebAPI;

public class Program
{
    public static void Main(string[] args)
    {
        CreateHostBuilder(args).Build().Run();
    }

    public static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .UseNhAspnetCommonConfiguration()
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.UseStartup<Startup>();
#if DEBUG
                webBuilder.UseKestrel(options =>
                {
                    options.Limits.MaxRequestBodySize = 99999999999999999;
                    options.Limits.MaxConcurrentConnections = 20000;
                    options.Limits.MaxConcurrentUpgradedConnections = 20000;
                });
#endif
            });
}