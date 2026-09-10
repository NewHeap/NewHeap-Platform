namespace NewHeap.Platform.AspNet.Proxy.Sqlite;

/// <summary>SQLite-owned configuration. The host must place the database outside its webroot.</summary>
public sealed class NhProxySqliteOptions
{
    public const string ConfigurationSectionName = "Sqlite";

    /// <summary>Relative to the host content root; production containers require persistent local storage.</summary>
    public string DatabasePath { get; set; } = "App_Data/newheap-proxy.db";
    public TimeSpan BusyTimeout { get; set; } = TimeSpan.FromSeconds(5);
}
