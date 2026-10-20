namespace NewHeap.Platform.Tests.Relational;

/// <summary>
/// The relational providers that NewHeap's provider tests run on.
/// </summary>
/// <remarks>
/// A provider test is a theory with the provider as a parameter, so the provider's name is part of
/// the test's display name: <c>npm run test:affected</c> runs the SQL Server cases
/// (<c>DisplayName~SqlServer</c>) in a process of their own next to the other tests, and each
/// process starts only the server its tests use.
/// </remarks>
public enum RelationalTestProvider
{
    SqlServer = 0,
    PostgreSql = 1
}
