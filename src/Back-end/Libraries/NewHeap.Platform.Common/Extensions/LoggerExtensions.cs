using NewHeap.Platform.Common.Exceptions;
using NewHeap.Platform.Common.Models;
using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using System.Reflection;

namespace Microsoft.Extensions.Logging;

public static partial class NhLoggerExtensions
{
    /// <summary>
    /// Formats and writes a debug log message.
    /// </summary>
    /// <param name="logger">The <see cref="ILogger"/> to write to.</param>
    /// <param name="eventId">The event id associated with the log.</param>
    /// <param name="taskResult">The taskResult to log.</param>
    /// <param name="message">Format string of the log message in message template format. Example: <c>"User {User} logged in from {Address}"</c>.</param>
    /// <param name="args">An object array that contains zero or more objects to format.</param>
    /// <example>
    /// <code language="csharp">
    /// logger.LogDebug(0, taskResult, "Error while processing request from {Address}", address)
    /// </code>
    /// </example>
    public static void LogDebug(this ILogger logger, EventId eventId, TaskResult? taskResult, string? message, params object?[] args)
    {
        logger.Log(LogLevel.Debug, eventId, taskResult, message, args);
    }

    /// <summary>
    /// Formats and writes a debug log message.
    /// </summary>
    /// <param name="logger">The <see cref="ILogger"/> to write to.</param>
    /// <param name="taskResult">The taskResult to log.</param>
    /// <param name="message">Format string of the log message in message template format. Example: <c>"User {User} logged in from {Address}"</c>.</param>
    /// <param name="args">An object array that contains zero or more objects to format.</param>
    /// <example>
    /// <code language="csharp">
    /// logger.LogDebug(taskResult, "Error while processing request from {Address}", address)
    /// </code>
    /// </example>
    public static void LogDebug(this ILogger logger, TaskResult? taskResult, string? message, params object?[] args)
    {
        logger.Log(LogLevel.Debug, taskResult, message, args);
    }


    /// <summary>
    /// Formats and writes a trace log message.
    /// </summary>
    /// <param name="logger">The <see cref="ILogger"/> to write to.</param>
    /// <param name="eventId">The event id associated with the log.</param>
    /// <param name="taskResult">The taskResult to log.</param>
    /// <param name="message">Format string of the log message in message template format. Example: <c>"User {User} logged in from {Address}"</c>.</param>
    /// <param name="args">An object array that contains zero or more objects to format.</param>
    /// <example>
    /// <code language="csharp">
    /// logger.LogTrace(0, taskResult, "Error while processing request from {Address}", address)
    /// </code>
    /// </example>
    public static void LogTrace(this ILogger logger, EventId eventId, TaskResult? taskResult, string? message, params object?[] args)
    {
        logger.Log(LogLevel.Trace, eventId, taskResult, message, args);
    }

    /// <summary>
    /// Formats and writes a trace log message.
    /// </summary>
    /// <param name="logger">The <see cref="ILogger"/> to write to.</param>
    /// <param name="taskResult">The taskResult to log.</param>
    /// <param name="message">Format string of the log message in message template format. Example: <c>"User {User} logged in from {Address}"</c>.</param>
    /// <param name="args">An object array that contains zero or more objects to format.</param>
    /// <example>
    /// <code language="csharp">
    /// logger.LogTrace(taskResult, "Error while processing request from {Address}", address)
    /// </code>
    /// </example>
    public static void LogTrace(this ILogger logger, TaskResult? taskResult, string? message, params object?[] args)
    {
        logger.Log(LogLevel.Trace, taskResult, message, args);
    }


    /// <summary>
    /// Formats and writes an informational log message.
    /// </summary>
    /// <param name="logger">The <see cref="ILogger"/> to write to.</param>
    /// <param name="eventId">The event id associated with the log.</param>
    /// <param name="taskResult">The taskResult to log.</param>
    /// <param name="message">Format string of the log message in message template format. Example: <c>"User {User} logged in from {Address}"</c>.</param>
    /// <param name="args">An object array that contains zero or more objects to format.</param>
    /// <example>
    /// <code language="csharp">
    /// logger.LogInformation(0, taskResult, "Error while processing request from {Address}", address)
    /// </code>
    /// </example>
    public static void LogInformation(this ILogger logger, EventId eventId, TaskResult? taskResult, string? message, params object?[] args)
    {
        logger.Log(LogLevel.Information, eventId, taskResult, message, args);
    }

    /// <summary>
    /// Formats and writes an informational log message.
    /// </summary>
    /// <param name="logger">The <see cref="ILogger"/> to write to.</param>
    /// <param name="taskResult">The taskResult to log.</param>
    /// <param name="message">Format string of the log message in message template format. Example: <c>"User {User} logged in from {Address}"</c>.</param>
    /// <param name="args">An object array that contains zero or more objects to format.</param>
    /// <example>
    /// <code language="csharp">
    /// logger.LogInformation(taskResult, "Error while processing request from {Address}", address)
    /// </code>
    /// </example>
    public static void LogInformation(this ILogger logger, TaskResult? taskResult, string? message, params object?[] args)
    {
        logger.Log(LogLevel.Information, taskResult, message, args);
    }

    /// <summary>
    /// Formats and writes a warning log message.
    /// </summary>
    /// <param name="logger">The <see cref="ILogger"/> to write to.</param>
    /// <param name="eventId">The event id associated with the log.</param>
    /// <param name="taskResult">The taskResult to log.</param>
    /// <param name="message">Format string of the log message in message template format. Example: <c>"User {User} logged in from {Address}"</c>.</param>
    /// <param name="args">An object array that contains zero or more objects to format.</param>
    /// <example>
    /// <code language="csharp">
    /// logger.LogWarning(0, taskResult, "Error while processing request from {Address}", address)
    /// </code>
    /// </example>
    public static void LogWarning(this ILogger logger, EventId eventId, TaskResult? taskResult, string? message, params object?[] args)
    {
        logger.Log(LogLevel.Warning, eventId, taskResult, message, args);
    }

    /// <summary>
    /// Formats and writes a warning log message.
    /// </summary>
    /// <param name="logger">The <see cref="ILogger"/> to write to.</param>
    /// <param name="taskResult">The taskResult to log.</param>
    /// <param name="message">Format string of the log message in message template format. Example: <c>"User {User} logged in from {Address}"</c>.</param>
    /// <param name="args">An object array that contains zero or more objects to format.</param>
    /// <example>
    /// <code language="csharp">
    /// logger.LogWarning(taskResult, "Error while processing request from {Address}", address)
    /// </code>
    /// </example>
    public static void LogWarning(this ILogger logger, TaskResult? taskResult, string? message, params object?[] args)
    {
        logger.Log(LogLevel.Warning, taskResult, message, args);
    }

    /// <summary>
    /// Formats and writes an error log message.
    /// </summary>
    /// <param name="logger">The <see cref="ILogger"/> to write to.</param>
    /// <param name="eventId">The event id associated with the log.</param>
    /// <param name="taskResult">The taskResult to log.</param>
    /// <param name="message">Format string of the log message in message template format. Example: <c>"User {User} logged in from {Address}"</c>.</param>
    /// <param name="args">An object array that contains zero or more objects to format.</param>
    /// <example>
    /// <code language="csharp">
    /// logger.LogError(0, taskResult, "Error while processing request from {Address}", address)
    /// </code>
    /// </example>
    public static void LogError(this ILogger logger, EventId eventId, TaskResult? taskResult, string? message, params object?[] args)
    {
        logger.Log(LogLevel.Error, eventId, taskResult, message, args);
    }

    /// <summary>
    /// Formats and writes an error log message.
    /// </summary>
    /// <param name="logger">The <see cref="ILogger"/> to write to.</param>
    /// <param name="taskResult">The taskResult to log.</param>
    /// <param name="message">Format string of the log message in message template format. Example: <c>"User {User} logged in from {Address}"</c>.</param>
    /// <param name="args">An object array that contains zero or more objects to format.</param>
    /// <example>
    /// <code language="csharp">
    /// logger.LogError(taskResult, "Error while processing request from {Address}", address)
    /// </code>
    /// </example>
    public static void LogError(this ILogger logger, TaskResult? taskResult, string? message, params object?[] args)
    {
        logger.Log(LogLevel.Error, taskResult, message, args);
    }

    /// <summary>
    /// Formats and writes a critical log message.
    /// </summary>
    /// <param name="logger">The <see cref="ILogger"/> to write to.</param>
    /// <param name="eventId">The event id associated with the log.</param>
    /// <param name="taskResult">The taskResult to log.</param>
    /// <param name="message">Format string of the log message in message template format. Example: <c>"User {User} logged in from {Address}"</c>.</param>
    /// <param name="args">An object array that contains zero or more objects to format.</param>
    /// <example>
    /// <code language="csharp">
    /// logger.LogCritical(0, taskResult, "Error while processing request from {Address}", address)
    /// </code>
    /// </example>
    public static void LogCritical(this ILogger logger, EventId eventId, TaskResult? taskResult, string? message, params object?[] args)
    {
        logger.Log(LogLevel.Critical, eventId, taskResult, message, args);
    }

    /// <summary>
    /// Formats and writes a critical log message.
    /// </summary>
    /// <param name="logger">The <see cref="ILogger"/> to write to.</param>
    /// <param name="taskResult">The taskResult to log.</param>
    /// <param name="message">Format string of the log message in message template format. Example: <c>"User {User} logged in from {Address}"</c>.</param>
    /// <param name="args">An object array that contains zero or more objects to format.</param>
    /// <example>
    /// <code language="csharp">
    /// logger.LogCritical(taskResult, "Error while processing request from {Address}", address)
    /// </code>
    /// </example>
    public static void LogCritical(this ILogger logger, TaskResult? taskResult, string? message, params object?[] args)
    {
        logger.Log(LogLevel.Critical, taskResult, message, args);
    }

    /// <summary>
    /// Formats and writes a log message at the specified log level.
    /// </summary>
    /// <param name="logger">The <see cref="ILogger"/> to write to.</param>
    /// <param name="logLevel">Entry will be written on this level.</param>
    /// <param name="taskResult">The taskResult to log.</param>
    /// <param name="message">Format string of the log message.</param>
    /// <param name="args">An object array that contains zero or more objects to format.</param>
    public static void Log(this ILogger logger, LogLevel logLevel, TaskResult? taskResult, string? message, params object?[] args)
    {
        logger.Log(logLevel, 0, taskResult, message, args);
    }

    /// <summary>
    /// Formats and writes a log message at the specified log level.
    /// </summary>
    /// <param name="logger">The <see cref="ILogger"/> to write to.</param>
    /// <param name="eventId">The event id associated with the log.</param>
    /// <param name="logLevel">Entry will be written on this level.</param>
    /// <param name="taskResult">The taskResult to log.</param>
    /// <param name="message">Format string of the log message.</param>
    /// <param name="args">An object array that contains zero or more objects to format.</param>
    public static void Log(this ILogger logger, LogLevel logLevel, EventId eventId, TaskResult? taskResult, string? message, params object?[] args)
    {
        if (taskResult == null)
        {
            logger.Log(logLevel, eventId, (Exception?)null, message, args);
            return;
        }
        else if (taskResult.Success)
        {
            logger.Log(logLevel, eventId, (Exception?)null, message, args);
            return;
        }
        else if (!taskResult.Success)
        {
            var taskResultException = new TaskResultException(taskResult);
            logger.Log(logLevel, eventId, taskResultException, message, args);
        }
        else
        { 
            throw new Exception("TaskResult is null or has an unexpected state.");
        }
    }

    #region Generic
    //------------------------------------------DEBUG------------------------------------------//

    public static void LogDebug<T>(this ILogger logger, EventId eventId, TaskResult<T>? taskResult, string? message, params object?[] args)
    {
        logger.Log(LogLevel.Debug, eventId, taskResult, message, args);
    }

    public static void LogDebug<T>(this ILogger logger, TaskResult<T>? taskResult, string? message, params object?[] args)
    {
        logger.Log(LogLevel.Debug, taskResult, message, args);
    }

    //------------------------------------------TRACE------------------------------------------//

    public static void LogTrace<T>(this ILogger logger, EventId eventId, TaskResult<T>? taskResult, string? message, params object?[] args)
    {
        logger.Log(LogLevel.Trace, eventId, taskResult, message, args);
    }

    public static void LogTrace<T>(this ILogger logger, TaskResult<T>? taskResult, string? message, params object?[] args)
    {
        logger.Log(LogLevel.Trace, taskResult, message, args);
    }

    //------------------------------------------INFORMATION------------------------------------------//

    public static void LogInformation<T>(this ILogger logger, EventId eventId, TaskResult<T>? taskResult, string? message, params object?[] args)
    {
        logger.Log(LogLevel.Information, eventId, taskResult, message, args);
    }

    public static void LogInformation<T>(this ILogger logger, TaskResult<T>? taskResult, string? message, params object?[] args)
    {
        logger.Log(LogLevel.Information, taskResult, message, args);
    }

    //------------------------------------------WARNING------------------------------------------//

    public static void LogWarning<T>(this ILogger logger, EventId eventId, TaskResult<T>? taskResult, string? message, params object?[] args)
    {
        logger.Log(LogLevel.Warning, eventId, taskResult, message, args);
    }

    public static void LogWarning<T>(this ILogger logger, TaskResult<T>? taskResult, string? message, params object?[] args)
    {
        logger.Log(LogLevel.Warning, taskResult, message, args);
    }

    //------------------------------------------ERROR------------------------------------------//

    public static void LogError<T>(this ILogger logger, EventId eventId, TaskResult<T>? taskResult, string? message, params object?[] args)
    {
        logger.Log(LogLevel.Error, eventId, taskResult, message, args);
    }

    public static void LogError<T>(this ILogger logger, TaskResult<T>? taskResult, string? message, params object?[] args)
    {
        logger.Log(LogLevel.Error, taskResult, message, args);
    }

    //------------------------------------------CRITICAL------------------------------------------//

    public static void LogCritical<T>(this ILogger logger, EventId eventId, TaskResult<T>? taskResult, string? message, params object?[] args)
    {
        logger.Log(LogLevel.Critical, eventId, taskResult, message, args);
    }

    public static void LogCritical<T>(this ILogger logger, TaskResult<T>? taskResult, string? message, params object?[] args)
    {
        logger.Log(LogLevel.Critical, taskResult, message, args);
    }

    //------------------------------------------GENERIC LOG------------------------------------------//

    /// <summary>
    /// Formats and writes a log message at the specified level, inclusief TaskResult&lt;T&gt;.
    /// </summary>
    public static void Log<T>(this ILogger logger, LogLevel logLevel, TaskResult<T>? taskResult, string? message, params object?[] args)
    {
        logger.Log(logLevel, 0, taskResult, message, args);
    }

    /// <summary>
    /// Formats and writes a log message at the specified level and eventId, inclusief TaskResult&lt;T&gt;,
    /// waarbij bij null of Success=true geen exception wordt gelogd, anders een TaskResultException&lt;T&gt; wordt gebruikt.
    /// </summary>
    public static void Log<T>(this ILogger logger, LogLevel logLevel, EventId eventId, TaskResult<T>? taskResult, string? message, params object?[] args)
    {
        if (taskResult == null)
        {
            logger.Log(logLevel, eventId, (Exception?)null, message, args);
            return;
        }
        else if (taskResult.Success)
        {
            logger.Log(logLevel, eventId, (Exception?)null, message, args);
            return;
        }
        else if (!taskResult.Success)
        {
            var taskResultException = new TaskResultException<T>(taskResult);
            logger.Log(logLevel, eventId, taskResultException, message, args);
        }
        else
        {
            throw new Exception("TaskResult is null or has an unexpected state.");
        }
    }
    #endregion
}