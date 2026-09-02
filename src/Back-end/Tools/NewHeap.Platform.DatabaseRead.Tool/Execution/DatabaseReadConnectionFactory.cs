using System.Data.Common;

namespace NewHeap.Platform.DatabaseRead;

internal static class DatabaseReadConnectionFactory
{
    public static DbConnection Create(
        IDatabaseReadProvider provider,
        string connectionString,
        string requestId,
        DatabaseReadLimits limits)
    {
        try
        {
            return provider.CreateConnection(connectionString, requestId, limits);
        }
        catch (Exception exception) when (
            exception is ArgumentException or FormatException or InvalidOperationException)
        {
            throw new DatabaseReadExpectedException(
                "connection-configuration-invalid",
                "The selected environment's resolved connection string is not valid for the configured provider.",
                DatabaseReadExitCode.InvalidProfile);
        }
    }
}
