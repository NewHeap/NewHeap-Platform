using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NewHeap.Platform.Common;

namespace NewHeap.Platform.DatabaseRead;

internal static class NewHeapConnectionStringResolver
{
    public static string Resolve(ResolvedDatabaseReadProfile profile)
    {
        try
        {
            var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
            {
                ContentRootPath = profile.ConfigurationPath,
                EnvironmentName = profile.Environment
            });
            builder.UseNhCommonConfiguration(Array.Empty<string>());

            var connectionString = builder.Configuration.GetConnectionString(profile.ConnectionStringName);
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new DatabaseReadExpectedException(
                    "connection-string-not-found",
                    $"Connection string '{profile.ConnectionStringName}' is not available through the selected profile.",
                    DatabaseReadExitCode.InvalidProfile);
            }

            return connectionString;
        }
        catch (DatabaseReadExpectedException)
        {
            throw;
        }
        catch
        {
            throw new DatabaseReadExpectedException(
                "configuration-load-failed",
                "The selected profile configuration could not be loaded through the NewHeap configuration flow.",
                DatabaseReadExitCode.InvalidProfile);
        }
    }
}
