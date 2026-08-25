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
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        var requestId = Guid.NewGuid().ToString("N");
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var options = CliOptions.Parse(args);
            if (options.ShowHelp)
            {
                var help = Encoding.UTF8.GetBytes(CliOptions.HelpText + Environment.NewLine);
                await output.WriteAsync(help, cancellationToken);
                return (int)DatabaseReadExitCode.Success;
            }

            var request = await DatabaseReadJson.ReadRequestAsync(input, cancellationToken);
            if (string.IsNullOrWhiteSpace(request.Profile))
            {
                throw new DatabaseReadExpectedException(
                    "missing-profile",
                    "The request profile is required.",
                    DatabaseReadExitCode.InvalidRequest);
            }

            var profile = await DatabaseReadProfileLoader.LoadAsync(
                request.Profile,
                options.ProfilesPath,
                cancellationToken);
            var effectiveLimits = DatabaseReadRequestValidator.Validate(request, profile);
            var provider = DatabaseReadProviderFactory.Create(profile.Provider);

            if (options.Command == DatabaseReadCommand.Validate)
            {
                stopwatch.Stop();
                await DatabaseReadJson.WriteAsync(
                    output,
                    CreateValidationResponse(requestId, stopwatch, profile, provider, effectiveLimits),
                    cancellationToken);
                return (int)DatabaseReadExitCode.Success;
            }

            var connectionString = NewHeapConnectionStringResolver.Resolve(profile);
            var result = await DatabaseReadQueryExecutor.ExecuteAsync(
                provider,
                connectionString,
                requestId,
                request,
                effectiveLimits,
                cancellationToken);
            stopwatch.Stop();

            var response = new DatabaseReadSuccessResponse
            {
                Operation = "query",
                RequestId = requestId,
                Target = CreateTarget(profile, provider, true),
                Result = result,
                Timing = new DatabaseReadTimingResponse { ElapsedMilliseconds = stopwatch.ElapsedMilliseconds }
            };
            TrimRowsToOutputLimit(response, effectiveLimits.MaximumOutputBytes);
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
        catch (DbException)
        {
            await WriteErrorAsync(
                output,
                requestId,
                "database-query-failed",
                "The database rejected or could not complete the diagnostic query.",
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

    private static void TrimRowsToOutputLimit(
        DatabaseReadSuccessResponse response,
        int maximumOutputBytes)
    {
        if (response.Result is null)
        {
            return;
        }

        while (DatabaseReadJson.Serialize(response).Length > maximumOutputBytes &&
               response.Result.Rows.Count > 0)
        {
            response.Result.Rows.RemoveAt(response.Result.Rows.Count - 1);
            response.Result.Truncated = true;
        }

        if (DatabaseReadJson.Serialize(response).Length > maximumOutputBytes)
        {
            throw new DatabaseReadExpectedException(
                "output-limit-too-small",
                "The profile output limit is too small for the response metadata.",
                DatabaseReadExitCode.InvalidProfile);
        }
    }

    private static Task WriteErrorAsync(
        Stream output,
        string requestId,
        string code,
        string message,
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
                    Message = message
                }
            },
            cancellationToken);
    }
}
