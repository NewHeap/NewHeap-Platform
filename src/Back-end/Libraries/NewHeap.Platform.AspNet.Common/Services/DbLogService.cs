using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models.Options;
using NewHeap.Platform.Common;
using System.Globalization;

namespace NewHeap.Platform.AspNet.Common.Services;

public partial class DbLogService
{
    protected readonly IHttpContextAccessor _httpContextAccessor;
    protected readonly IStringLocalizer<DbLogService> _logLocalizer;
    protected readonly IRepository<Log> _logRepository;
    protected readonly DbLogServiceSettings _logSettings;
    protected readonly NewHeapAspNetCommonSettings _settings;

    public DbLogService(
        IOptions<DbLogServiceSettings> logSettings,
        IRepository<Log> logRepository,
        IHttpContextAccessor httpContextAccessor,
        IStringLocalizer<DbLogService> logLocalizer,
        IOptions<NewHeapAspNetCommonSettings> settings
    )
    {
        _logSettings = logSettings.Value;
        _logRepository = logRepository;
        _logLocalizer = logLocalizer;
        _httpContextAccessor = httpContextAccessor;
        _settings = settings.Value;
    }

    public virtual IQueryable<Log> GetQueryable()
    {
        return _logRepository.GetAll();
    }

    public virtual async Task LogAsync(
        string message,
        LogType type = LogType.Unknown,
        string? tag = null,
        string?[]? messageArguments = null,
        LogAction action = LogAction.Unknown,
        LogSource source = LogSource.Unknown,
        string? objectType = null,
        string? objectTypeFull = null,
        string? objectId = null,
        Guid? userId = null,
        (string name, Stream contentStream)[]? files = null,
        DateTimeOffset? overrideCreationDateTime = null,
        bool doSaveChanges = true,
        NhIdentityDbContext? dbContext = null,
        CancellationToken cancellationToken = default
    )
    {
        dbContext ??= _logRepository.Context;

        Log log = new()
        {
            Message = message,
            Tag = tag,
            ObjectId = objectId,
            ObjectType = objectType,
            ObjectTypeFull = objectTypeFull,
            Action = action,
            Type = type,
            UserId = userId,
            Source = LogSource.Internal,
            DivisionId = _httpContextAccessor?.HttpContext?.Request?.GetActiveDivisionId()
        };

        if (overrideCreationDateTime.HasValue)
        {
            log.CreationDateTime = overrideCreationDateTime.Value;
        }

        await dbContext.Logs.AddAsync(log);

        if (messageArguments?.Any() == true)
        {
            for (var i = 0; i < messageArguments.Length; i++)
            {
                LogMessageArgument logMessageArgument = new() { Log = log, Index = i, Value = messageArguments[i] };

                if (!string.IsNullOrWhiteSpace(logMessageArgument.Value))
                {
                    logMessageArgument.Value = logMessageArgument.StringGuidelineMaxLength(x => x.Value);
                }

                await dbContext.LogMessageArguments.AddAsync(logMessageArgument);
            }
        }

        var cultures = _settings.SupportedCultures.ToList();
        if (!cultures.Any())
        {
            cultures.Add(
                string.IsNullOrWhiteSpace(_settings.DefaultCulture) ? "en-US" : _settings.DefaultCulture
            );
        }

        var originCulture = CultureInfo.CurrentCulture;
        var originUICulture = CultureInfo.CurrentUICulture;

        foreach (var culture in cultures)
        {
            LocalizedString? localizedMessage = null;

            CultureInfo cultureInfo = new(culture);
            CultureInfo.CurrentCulture = cultureInfo;
            CultureInfo.CurrentUICulture = cultureInfo;

            try
            {
                var inputMsg = log.StringGuidelineMaxLength(x => x.Message);
                localizedMessage = _logLocalizer.GetString(inputMsg??"", [..messageArguments!]);
            }
            catch
            {
                //Ignore
            }

            await dbContext.LogMessageTranslateds.AddAsync(new LogMessageTranslated
            {
                Log = log,
                Culture = culture,
                Message = localizedMessage ?? log.StringGuidelineMaxLength(x => x.Message)!
            });
        }

        CultureInfo.CurrentCulture = originCulture;
        CultureInfo.CurrentUICulture = originUICulture;

        log.Message = log.StringGuidelineMaxLength(x => x.Message)!;

        if (doSaveChanges)
        {
            await SaveChanges();
        }

        if (files?.Any() == true)
        {
            if (string.IsNullOrWhiteSpace(_logSettings.RootDirectory))
            {
                throw new Exception("The log directory is not specified.");
            }

            if (!Directory.Exists(_logSettings.RootDirectory))
            {
                throw new Exception("The log directory does not exist.");
            }

            try
            {
                var logDirectory = Path.Combine(_logSettings.RootDirectory, log.Id.ToString());
                Directory.CreateDirectory(logDirectory);

                var addedCount = 0;
                foreach (var (name, contentStream) in files)
                {
                    //Make 10 attempts of file naming and saving.
                    for (var i = 0; i < 10; i++)
                    {
                        var filePath = Path.Combine(logDirectory, $"{GetRandomString(5)}_{name}");
                        if (File.Exists(filePath))
                        {
                            continue;
                        }

                        using (var fileStream = File.Create(filePath))
                        {
                            contentStream.Seek(0, SeekOrigin.Begin);
                            contentStream.CopyTo(fileStream);
                        }

                        LogFile logFile = new() { OriginalFileName = name, FilePath = filePath, LogId = log.Id };

                        await dbContext.LogFiles.AddAsync(logFile);

                        addedCount++;
                        break;
                    }
                }

                if (addedCount > 0)
                {
                    await dbContext.SaveChangesAsync();
                }
            }
            catch
            {
                //Ignore
            }
        }
    }

    public virtual async Task SaveChanges()
    {
        await _logRepository.SaveChangesAsync();
    }

    protected static string GetRandomString(int length)
    {
        Random random = new();
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        return new string(Enumerable.Repeat(chars, length)
            .Select(s => s[random.Next(s.Length)]).ToArray());
    }
}