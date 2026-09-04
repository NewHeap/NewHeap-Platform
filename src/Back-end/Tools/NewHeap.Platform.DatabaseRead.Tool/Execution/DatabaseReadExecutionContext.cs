namespace NewHeap.Platform.DatabaseRead;

internal enum DatabaseReadExecutionStage
{
    None,
    ConnectionOpen,
    ReadOnlyVerification,
    QueryExecution,
    SchemaExecution
}

internal sealed class DatabaseReadExecutionContext
{
    public DatabaseReadExecutionStage Stage
    {
        get; private set;
    }

    public void Enter(DatabaseReadExecutionStage stage)
    {
        Stage = stage;
    }

    public string? GetResponseStage()
    {
        return Stage switch
        {
            DatabaseReadExecutionStage.None => null,
            DatabaseReadExecutionStage.ConnectionOpen => "connection-open",
            DatabaseReadExecutionStage.ReadOnlyVerification => "readonly-verification",
            DatabaseReadExecutionStage.QueryExecution => "query-execution",
            DatabaseReadExecutionStage.SchemaExecution => "schema-execution",
            _ => throw new ArgumentOutOfRangeException(nameof(Stage), Stage, null)
        };
    }
}