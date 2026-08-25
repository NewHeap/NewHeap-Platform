namespace NewHeap.Platform.DatabaseRead;

internal sealed class DatabaseReadExpectedException(
    string code,
    string message,
    DatabaseReadExitCode exitCode) : Exception(message)
{
    public string Code { get; } = code;

    public DatabaseReadExitCode ExitCode { get; } = exitCode;
}

internal enum DatabaseReadExitCode
{
    Success = 0,
    UnexpectedFailure = 1,
    InvalidRequest = 2,
    InvalidProfile = 3,
    PolicyRejected = 4,
    DatabaseFailure = 5,
    Cancelled = 130
}
