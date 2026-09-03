using System.Data.Common;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NewHeap.Platform.DatabaseRead;

public static class NewHeapDatabaseReadApplication
{
    public static async Task<int> RunAsync(
        string[] args,
        Stream input,
        Stream output,
        CancellationToken cancellationToken = default)
    {
        return await RunAsync(
            args,
            input,
            output,
            DatabaseReadConnectionFactory.Instance,
            cancellationToken);
    }

    internal static async Task<int> RunAsync(
        string[] args,
        Stream input,
        Stream output,
        IDatabaseReadConnectionFactory connectionFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(connectionFactory);

        var requestId = Guid.NewGuid().ToString("N");
        var stopwatch = Stopwatch.StartNew();
        IDatabaseReadProvider? provider = null;
        var executionContext = new DatabaseReadExecutionContext();

        try
        {
            var options = CliOptions.Parse(args);
            if (options.ShowHelp)
            {
                var help = Encoding.UTF8.GetBytes(CliOptions.HelpText + Environment.NewLine);
                await output.WriteAsync(help, cancellationToken);
                return (int)DatabaseReadExitCode.Success;
            }

            await using var requestFileInput = OpenRequestFile(options.RequestFilePath);
            var request = options.DirectSchema?.CreateRequest(options.ProfileName) ??
                          await DatabaseReadJson.ReadRequestAsync(
                              requestFileInput ?? input,
                              cancellationToken);

            var profile = await DatabaseReadProfileLoader.LoadAsync(
                options.ProfileName ?? request.Profile,
                options.EnvironmentName,
                options.ProfilesPath,
                cancellationToken);
            var validation = DatabaseReadRequestValidator.Validate(request, profile, options.Command);
            provider = DatabaseReadProviderFactory.Create(profile.Provider);

            if (options.Command == DatabaseReadCommand.Validate)
            {
                stopwatch.Stop();
                await DatabaseReadJson.WriteAsync(
                    output,
                    CreateValidationResponse(requestId, stopwatch, profile, provider, validation.Limits),
                    cancellationToken);
                return (int)DatabaseReadExitCode.Success;
            }

            var connectionString = NewHeapConnectionStringResolver.Resolve(profile);
            DatabaseQueryResultResponse? queryResult = null;
            DatabaseSchemaResultResponse? schemaResult = null;
            if (validation.Kind == DatabaseReadRequestKind.Query)
            {
                queryResult = await DatabaseReadQueryExecutor.ExecuteAsync(
                    provider,
                    connectionFactory,
                    executionContext,
                    connectionString,
                    requestId,
                    request,
                    validation.Limits,
                    cancellationToken);
            }
            else
            {
                schemaResult = await DatabaseSchemaReader.ExecuteAsync(
                    provider,
                    connectionFactory,
                    executionContext,
                    connectionString,
                    requestId,
                    validation.Schema!,
                    validation.Limits,
                    cancellationToken);
            }
            stopwatch.Stop();

            var response = new DatabaseReadSuccessResponse
            {
                Operation = validation.Kind == DatabaseReadRequestKind.Query ? "query" : "schema",
                RequestId = requestId,
                Target = CreateTarget(profile, provider, true),
                Result = queryResult,
                Schema = schemaResult,
                Timing = new DatabaseReadTimingResponse { ElapsedMilliseconds = stopwatch.ElapsedMilliseconds }
            };
            if (response.Schema is not null)
            {
                response.Schema.EvidenceHash = new string('0', 64);
            }
            TrimToOutputLimit(response, validation.Limits.MaximumOutputBytes);
            if (response.Schema is not null)
            {
                response.Schema.EvidenceHash = null;
                response.Schema.EvidenceHash = Convert.ToHexString(
                        SHA256.HashData(DatabaseReadJson.Serialize(response.Schema)))
                    .ToLowerInvariant();
            }
            await DatabaseReadJson.WriteAsync(output, response, cancellationToken);

            return (int)DatabaseReadExitCode.Success;
        }
        catch (OperationCanceledException)
        {
            await WriteErrorAsync(
                output,
                requestId,
                "cancelled",
                "The database read operation was cancelled.",
                CancellationToken.None);
            return (int)DatabaseReadExitCode.Cancelled;
        }
        catch (JsonException)
        {
            await WriteErrorAsync(
                output,
                requestId,
                "invalid-json",
                "The request is not valid JSON or contains unknown properties.",
                cancellationToken);
            return (int)DatabaseReadExitCode.InvalidRequest;
        }
        catch (DatabaseReadExpectedException exception)
        {
            await WriteErrorAsync(output, requestId, exception.Code, exception.Message, cancellationToken);
            return (int)exception.ExitCode;
        }
        catch (DbException exception)
        {
            var failure = provider?.ClassifyException(exception, executionContext.Stage);
            await WriteErrorAsync(
                output,
                requestId,
                "database-query-failed",
                failure?.Message ?? "The database rejected or could not complete the diagnostic operation.",
                failure,
                executionContext.GetResponseStage(),
                cancellationToken);
            return (int)DatabaseReadExitCode.DatabaseFailure;
        }
        catch
        {
            await WriteErrorAsync(
                output,
                requestId,
                "unexpected-failure",
                "The database read tool failed unexpectedly.",
                cancellationToken);
            return (int)DatabaseReadExitCode.UnexpectedFailure;
        }
    }

    private static FileStream? OpenRequestFile(string? requestFilePath)
    {
        if (requestFilePath is null)
        {
            return null;
        }

        try
        {
            return new FileStream(
                requestFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 8192,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new DatabaseReadExpectedException(
                "request-file-unavailable",
                "The database read request file is unavailable.",
                DatabaseReadExitCode.InvalidRequest);
        }
    }

    private static DatabaseReadSuccessResponse CreateValidationResponse(
        string requestId,
        Stopwatch stopwatch,
        ResolvedDatabaseReadProfile profile,
        IDatabaseReadProvider provider,
        DatabaseReadLimits limits)
    {
        return new DatabaseReadSuccessResponse
        {
            Operation = "validate",
            RequestId = requestId,
            Target = CreateTarget(profile, provider, null),
            RequiredCapabilities = ["outbound-network"],
            Validation = new DatabaseReadValidationResponse
            {
                EffectiveLimits = new DatabaseReadLimitsResponse
                {
                    MaximumRows = limits.MaximumRows,
                    TimeoutSeconds = limits.TimeoutSeconds,
                    MaximumOutputBytes = limits.MaximumOutputBytes,
                    MaximumCellBytes = limits.MaximumCellBytes
                }
            },
            Timing = new DatabaseReadTimingResponse { ElapsedMilliseconds = stopwatch.ElapsedMilliseconds }
        };
    }

    private static DatabaseReadTargetResponse CreateTarget(
        ResolvedDatabaseReadProfile profile,
        IDatabaseReadProvider provider,
        bool? readOnlyVerified)
    {
        return new DatabaseReadTargetResponse
        {
            Profile = profile.Name,
            Provider = provider.Name,
            Environment = profile.Environment,
            ReadOnlyVerified = readOnlyVerified
        };
    }

    private static void TrimToOutputLimit(
        DatabaseReadSuccessResponse response,
        int maximumOutputBytes)
    {
        if (response.Result is not null &&
            DatabaseReadJson.Serialize(response).Length > maximumOutputBytes)
        {
            throw new DatabaseReadExpectedException(
                "result-output-limit-exceeded",
                $"The query result exceeded the requested maximum output size of {maximumOutputBytes} bytes. No partial result was returned. Narrow the query or request a permitted higher limit.",
                DatabaseReadExitCode.PolicyRejected);
        }

        if (response.Schema?.Objects is List<DatabaseSchemaObjectSummaryResponse> objects)
        {
            while (DatabaseReadJson.Serialize(response).Length > maximumOutputBytes && objects.Count > 0)
            {
                objects.RemoveAt(objects.Count - 1);
                response.Schema.Truncated = true;
            }
        }

        if (DatabaseReadJson.Serialize(response).Length <= maximumOutputBytes)
        {
            return;
        }

        throw new DatabaseReadExpectedException(
            response.Schema is null ? "output-limit-too-small" : "schema-output-too-large",
            response.Schema is null
                ? "The profile output limit is too small for the response metadata."
                : "The described schema object exceeds the profile output limit.",
            DatabaseReadExitCode.InvalidProfile);
    }

    private static Task WriteErrorAsync(
        Stream output,
        string requestId,
        string code,
        string message,
        CancellationToken cancellationToken)
    {
        return WriteErrorAsync(output, requestId, code, message, null, null, cancellationToken);
    }

    private static Task WriteErrorAsync(
        Stream output,
        string requestId,
        string code,
        string message,
        DatabaseReadProviderFailure? providerFailure,
        string? stage,
        CancellationToken cancellationToken)
    {
        return DatabaseReadJson.WriteAsync(
            output,
            new DatabaseReadErrorResponse
            {
                RequestId = requestId,
                Error = new DatabaseReadError
                {
                    Code = code,
                    Message = message,
                    Classification = providerFailure?.Classification,
                    Provider = providerFailure?.Provider,
                    ProviderCode = providerFailure?.ProviderCode,
                    Transient = providerFailure?.Transient,
                    Stage = stage,
                    RetryHint = providerFailure?.RetryHint
                }
            },
            cancellationToken);
    }
}