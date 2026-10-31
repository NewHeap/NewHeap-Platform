using System.Data.Common;
using System.Diagnostics;
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

    internal static Task<int> RunAsync(
        string[] args,
        Stream input,
        Stream output,
        IDatabaseReadConnectionFactory connectionFactory,
        CancellationToken cancellationToken = default)
    {
        return RunAsync(args, input, output, connectionFactory, TimeProvider.System, cancellationToken);
    }

    internal static async Task<int> RunAsync(
        string[] args,
        Stream input,
        Stream output,
        IDatabaseReadConnectionFactory connectionFactory,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var invocation = new DatabaseReadInvocation();

        return await RunGuardedAsync(
            invocation,
            output,
            async () =>
            {
                var options = CliOptions.Parse(args);
                if (options.ShowHelp)
                {
                    var help = Encoding.UTF8.GetBytes(CliOptions.HelpText + Environment.NewLine);
                    await output.WriteAsync(help, cancellationToken);
                    return (int)DatabaseReadExitCode.Success;
                }

                if (options.Command == DatabaseReadCommand.Session)
                {
                    return await DatabaseReadSession.RunAsync(
                        options,
                        input,
                        output,
                        connectionFactory,
                        timeProvider,
                        cancellationToken);
                }

                await using var requestFileInput = OpenRequestFile(options.RequestFilePath);
                var request = options.DirectSchema?.CreateRequest(options.ProfileName) ??
                              await DatabaseReadJson.ReadRequestAsync(
                                  requestFileInput ?? input,
                                  cancellationToken);

                return await ExecuteRequestAsync(
                    invocation,
                    options.Command,
                    options.ProfileName ?? request.Profile,
                    request,
                    new DatabaseReadOneShotHost(options, connectionFactory),
                    output,
                    cancellationToken);
            },
            cancellationToken);
    }

    /// <summary>
    /// One request of a process that serves many: the pipeline of a one-shot command, with the same
    /// response on <paramref name="output"/> and the exit code the command would have ended with.
    /// A failed request ends nothing but itself.
    /// </summary>
    internal static Task<int> RunRequestAsync(
        DatabaseReadCommand command,
        string? commandLineProfile,
        Func<CancellationToken, Task<DatabaseReadRequest>> readRequest,
        IDatabaseReadRequestHost host,
        Stream output,
        CancellationToken cancellationToken)
    {
        var invocation = new DatabaseReadInvocation();

        return RunGuardedAsync(
            invocation,
            output,
            async () =>
            {
                var request = await readRequest(cancellationToken);

                return await ExecuteRequestAsync(
                    invocation,
                    command,
                    commandLineProfile ?? request.Profile,
                    request,
                    host,
                    output,
                    cancellationToken);
            },
            cancellationToken);
    }

    /// <summary>
    /// A refused request in the one error JSON of the tool, for what a process that serves many
    /// requests refuses before the pipeline.
    /// </summary>
    internal static Task WriteErrorAsync(
        Stream output,
        string code,
        string message,
        CancellationToken cancellationToken)
    {
        return WriteErrorAsync(output, Guid.NewGuid().ToString("N"), code, message, cancellationToken);
    }

    private static async Task<int> RunGuardedAsync(
        DatabaseReadInvocation invocation,
        Stream output,
        Func<Task<int>> run,
        CancellationToken cancellationToken)
    {
        try
        {
            return await run();
        }
        catch (OperationCanceledException)
        {
            await WriteErrorAsync(
                output,
                invocation.RequestId,
                "cancelled",
                "The database read operation was cancelled.",
                CancellationToken.None);
            return (int)DatabaseReadExitCode.Cancelled;
        }
        catch (JsonException)
        {
            await WriteErrorAsync(
                output,
                invocation.RequestId,
                "invalid-json",
                "The request is not valid JSON or contains unknown properties.",
                cancellationToken);
            return (int)DatabaseReadExitCode.InvalidRequest;
        }
        catch (DatabaseReadExpectedException exception)
        {
            await WriteErrorAsync(output, invocation.RequestId, exception.Code, exception.Message,
                null, exception.VerificationCheck is null ? null : invocation.ExecutionContext.GetResponseStage(),
                cancellationToken, exception.VerificationCheck);
            return (int)exception.ExitCode;
        }
        catch (DbException exception)
        {
            var failure = invocation.Provider?.ClassifyException(exception, invocation.ExecutionContext.Stage);
            if (failure?.Classification == "connection-failed")
            {
                invocation.Host?.OnConnectionFailed();
            }

            await WriteErrorAsync(
                output,
                invocation.RequestId,
                "database-query-failed",
                failure?.Message ?? "The database rejected or could not complete the diagnostic operation.",
                failure,
                invocation.ExecutionContext.GetResponseStage(),
                cancellationToken);
            return (int)DatabaseReadExitCode.DatabaseFailure;
        }
        catch
        {
            await WriteErrorAsync(
                output,
                invocation.RequestId,
                "unexpected-failure",
                "The database read tool failed unexpectedly.",
                cancellationToken);
            return (int)DatabaseReadExitCode.UnexpectedFailure;
        }
    }

    private static async Task<int> ExecuteRequestAsync(
        DatabaseReadInvocation invocation,
        DatabaseReadCommand command,
        string? profileName,
        DatabaseReadRequest request,
        IDatabaseReadRequestHost host,
        Stream output,
        CancellationToken cancellationToken)
    {
        invocation.Host = host;
        var profile = await host.LoadProfileAsync(profileName, cancellationToken);
        var validation = DatabaseReadRequestValidator.Validate(request, profile, command);
        var provider = DatabaseReadProviderFactory.Create(profile);
        invocation.Provider = provider;

        if (command == DatabaseReadCommand.Validate)
        {
            invocation.Stopwatch.Stop();
            await DatabaseReadJson.WriteAsync(
                output,
                CreateValidationResponse(invocation.RequestId, invocation.Stopwatch, profile, provider, validation.Limits),
                cancellationToken);
            return (int)DatabaseReadExitCode.Success;
        }

        var connectionString = host.ResolveConnectionString(profile);
        DatabaseQueryResultResponse? queryResult = null;
        DatabaseSchemaResultResponse? schemaResult = null;
        if (validation.Kind == DatabaseReadRequestKind.Query)
        {
            queryResult = await DatabaseReadQueryExecutor.ExecuteAsync(
                provider,
                host,
                invocation.ExecutionContext,
                connectionString,
                invocation.RequestId,
                request,
                validation.Limits,
                cancellationToken);
        }
        else
        {
            schemaResult = await DatabaseSchemaReader.ExecuteAsync(
                provider,
                host,
                invocation.ExecutionContext,
                connectionString,
                invocation.RequestId,
                validation.Schema!,
                validation.Limits,
                cancellationToken);
        }
        invocation.Stopwatch.Stop();

        var response = new DatabaseReadSuccessResponse
        {
            Operation = validation.Kind == DatabaseReadRequestKind.Query ? "query" : "schema",
            RequestId = invocation.RequestId,
            Target = CreateTarget(profile, provider, true),
            Result = queryResult,
            Schema = schemaResult,
            Timing = new DatabaseReadTimingResponse { ElapsedMilliseconds = invocation.Stopwatch.ElapsedMilliseconds }
        };
        if (response.Schema is not null)
        {
            response.Schema.EvidenceHash = new string('0', 64);
        }
        TrimToOutputLimit(response, validation.Limits.MaximumOutputBytes);
        if (response.Schema is not null)
        {
            response.Schema.EvidenceHash = DatabaseReadJson.ComputeEvidenceHash(response.Schema);
        }
        await DatabaseReadJson.WriteAsync(output, response, cancellationToken);

        return (int)DatabaseReadExitCode.Success;
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
            RemoveTrailingObjects(response, objects, maximumOutputBytes);
        }

        if (response.Schema?.CatalogObjects is List<DatabaseSchemaCatalogObjectResponse> catalogObjects)
        {
            RemoveTrailingObjects(response, catalogObjects, maximumOutputBytes);
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

    private static void RemoveTrailingObjects<TObject>(
        DatabaseReadSuccessResponse response,
        List<TObject> objects,
        int maximumOutputBytes)
    {
        while (DatabaseReadJson.Serialize(response).Length > maximumOutputBytes && objects.Count > 0)
        {
            objects.RemoveAt(objects.Count - 1);
            response.Schema!.Truncated = true;
        }
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
        CancellationToken cancellationToken,
        string? verificationCheck = null)
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
                    VerificationCheck = verificationCheck,
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

    /// <summary>
    /// What one request carries through the pipeline and what its failure needs to be reported.
    /// </summary>
    private sealed class DatabaseReadInvocation
    {
        public string RequestId { get; } = Guid.NewGuid().ToString("N");

        public Stopwatch Stopwatch { get; } = Stopwatch.StartNew();

        public DatabaseReadExecutionContext ExecutionContext { get; } = new();

        public IDatabaseReadProvider? Provider { get; set; }

        public IDatabaseReadRequestHost? Host { get; set; }
    }
}
