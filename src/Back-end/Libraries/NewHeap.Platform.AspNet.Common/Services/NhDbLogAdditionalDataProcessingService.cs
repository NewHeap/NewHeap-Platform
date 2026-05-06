using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models.Options;

namespace NewHeap.Platform.AspNet.Common.Services;

public class NhDbLogAdditionalDataProcessingService<
    TLog,
    TUser,
    TLogMessageArgument,
    TLogMessageTranslated,
    TLogFile,
    TDivision,
    TDivisionUser,
    TDivisionRole,
    TDivisionUserRole,
    TDivisionRoleClaim>
    where TLog : NhLog<TUser, TLogMessageArgument, TLogMessageTranslated, TLogFile, TDivision, TDivisionUser, TDivisionRole, TDivisionUserRole, TDivisionRoleClaim>, new()
    where TUser : NhUser<TDivision, TDivisionUser, TDivisionUserRole, TDivisionRole, TDivisionRoleClaim, TUser>
    where TLogMessageArgument : NhLogMessageArgument, new()
    where TLogMessageTranslated : NhLogMessageTranslated, new()
    where TLogFile : NhLogFile, new()
    where TDivision : NhDivision<TDivisionUser, TDivisionUserRole, TDivisionRole, TDivisionRoleClaim, TDivision, TUser>
    where TDivisionRole : NhDivisionRole<TDivisionUserRole, TDivisionRoleClaim, TDivisionUser, TDivisionRole, TDivision, TUser>
    where TDivisionUser : NhDivisionUser<TDivisionUserRole, TDivisionUser, TDivisionRole, TDivisionRoleClaim, TDivision, TUser>
    where TDivisionUserRole : NhDivisionUserRole<TDivisionUser, TDivisionRole, TDivisionRoleClaim, TDivisionUserRole, TDivision, TUser>
    where TDivisionRoleClaim : NhDivisionRoleClaim
{
    private readonly IRepository<TLog> _logRepository;

    public NhDbLogAdditionalDataProcessingService(
        IRepository<TLog> logRepository)
    {
        _logRepository = logRepository;
    }

    public virtual async Task<int> ProcessBatchAsync(
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        batchSize = Math.Max(1, batchSize);

        var logs = await _logRepository
            .GetAll()
            .Where(x => x.Version >= 2)
            .Where(x => !x.AdditionalDataProcessed)
            .Where(x => x.AdditionalData != null)
            .OrderBy(x => x.CreationDateTime)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        if (!logs.Any())
        {
            return 0;
        }

        var messageArguments = new List<TLogMessageArgument>();
        var messageTranslateds = new List<TLogMessageTranslated>();
        var files = new List<TLogFile>();

        foreach (var log in logs)
        {
            if (log.AdditionalData == null)
            {
                log.AdditionalDataProcessed = true;
                log.AdditionalData = null;
                continue;
            }

            foreach (var messageArgument in log.AdditionalData.MessageArguments ?? [])
            {
                messageArgument.LogId = log.Id;
                messageArguments.Add(messageArgument);
            }

            foreach (var messageTranslated in log.AdditionalData.MessageTranslateds ?? [])
            {
                messageTranslated.LogId = log.Id;
                messageTranslateds.Add(messageTranslated);
            }

            foreach (var file in log.AdditionalData.Files ?? [])
            {
                file.LogId = log.Id;
                files.Add(file);
            }

            log.AdditionalDataProcessed = true;
            log.AdditionalData = null;
        }

        if (messageArguments.Any())
        {
            await _logRepository.AddRangeAsync(messageArguments, cancellationToken);
        }

        if (messageTranslateds.Any())
        {
            await _logRepository.AddRangeAsync(messageTranslateds, cancellationToken);
        }

        if (files.Any())
        {
            await _logRepository.AddRangeAsync(files, cancellationToken);
        }

        await _logRepository.SaveChangesAsync(cancellationToken);

        return logs.Count;
    }
}

internal class NhDbLogAdditionalDataHostedService<
    TLog,
    TUser,
    TLogMessageArgument,
    TLogMessageTranslated,
    TLogFile,
    TDivision,
    TDivisionUser,
    TDivisionRole,
    TDivisionUserRole,
    TDivisionRoleClaim> : BackgroundService
    where TLog : NhLog<TUser, TLogMessageArgument, TLogMessageTranslated, TLogFile, TDivision, TDivisionUser, TDivisionRole, TDivisionUserRole, TDivisionRoleClaim>, new()
    where TUser : NhUser<TDivision, TDivisionUser, TDivisionUserRole, TDivisionRole, TDivisionRoleClaim, TUser>
    where TLogMessageArgument : NhLogMessageArgument, new()
    where TLogMessageTranslated : NhLogMessageTranslated, new()
    where TLogFile : NhLogFile, new()
    where TDivision : NhDivision<TDivisionUser, TDivisionUserRole, TDivisionRole, TDivisionRoleClaim, TDivision, TUser>
    where TDivisionRole : NhDivisionRole<TDivisionUserRole, TDivisionRoleClaim, TDivisionUser, TDivisionRole, TDivision, TUser>
    where TDivisionUser : NhDivisionUser<TDivisionUserRole, TDivisionUser, TDivisionRole, TDivisionRoleClaim, TDivision, TUser>
    where TDivisionUserRole : NhDivisionUserRole<TDivisionUser, TDivisionRole, TDivisionRoleClaim, TDivisionUserRole, TDivision, TUser>
    where TDivisionRoleClaim : NhDivisionRoleClaim
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<NhDbLogAdditionalDataHostedService<TLog, TUser, TLogMessageArgument, TLogMessageTranslated, TLogFile, TDivision, TDivisionUser, TDivisionRole, TDivisionUserRole, TDivisionRoleClaim>> _logger;
    private DbLogServiceSettings _settings;

    public NhDbLogAdditionalDataHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<NhDbLogAdditionalDataHostedService<TLog, TUser, TLogMessageArgument, TLogMessageTranslated, TLogFile, TDivision, TDivisionUser, TDivisionRole, TDivisionUserRole, TDivisionRoleClaim>> logger,
        IOptionsMonitor<DbLogServiceSettings> settingsOptionsMonitor)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _settings = settingsOptionsMonitor.CurrentValue;
        settingsOptionsMonitor.OnChange(updated =>
        {
            _settings = updated;
        });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Db log additional data processor started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_settings.AdditionalDataProcessingEnabled)
                {
                    using var scope = _scopeFactory.CreateScope();
                    var processor = scope.ServiceProvider.GetRequiredService<NhDbLogAdditionalDataProcessingService<
                        TLog,
                        TUser,
                        TLogMessageArgument,
                        TLogMessageTranslated,
                        TLogFile,
                        TDivision,
                        TDivisionUser,
                        TDivisionRole,
                        TDivisionUserRole,
                        TDivisionRoleClaim>>();

                    await processor.ProcessBatchAsync(_settings.AdditionalDataProcessingBatchSize, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception occured during db log additional data processing");
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _settings.AdditionalDataProcessingIntervalInSeconds)), stoppingToken);
        }
    }
}
