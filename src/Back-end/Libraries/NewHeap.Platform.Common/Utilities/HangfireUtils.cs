using Hangfire;
using System.Linq.Expressions;

namespace NewHeap.Platform.Common.Utilities;

public static class NhHangfireUtil
{
    public static string? OVERRIDE_GET_QUEUE_NAME = null;

    public static string GetQueueName()
    {
        if(!string.IsNullOrWhiteSpace(OVERRIDE_GET_QUEUE_NAME))
        {
            return OVERRIDE_GET_QUEUE_NAME;
        }

        var queue = "default";
        if ((Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "").Equals("development",
                StringComparison.InvariantCultureIgnoreCase))
        {
            var name = $"DEV-{Environment.MachineName}".ToLower().Trim().SafeMaxStringLength(50);
            queue = $"QUE-{name}".ToLower();
        }

        return queue;
    }

    public static string GetQueueName(string queue)
    {
        return string.IsNullOrWhiteSpace(queue)
            ? GetQueueName()
            : queue;
    }

    public static class BackgroundJob
    {
        public static string Enqueue(Expression<Action> methodCall)
        {
            return Enqueue(GetQueueName(), methodCall);
        }

        public static string Enqueue(string queue, Expression<Action> methodCall)
        {
            queue = GetQueueName(queue);
            return Hangfire.BackgroundJob.Enqueue(queue, methodCall);
        }

        public static string Enqueue(Expression<Func<Task>> methodCall)
        {
            return Enqueue(methodCall);
        }

        public static string Enqueue(string queue, Expression<Func<Task>> methodCall)
        {
            queue = GetQueueName(queue);
            return Hangfire.BackgroundJob.Enqueue(queue, methodCall);
        }

        public static string Enqueue<T>(Expression<Action<T>> methodCall)
        {
            return Enqueue(methodCall);
        }

        public static string Enqueue<T>(string queue, Expression<Action<T>> methodCall)
        {
            queue = GetQueueName(queue);
            return Hangfire.BackgroundJob.Enqueue(queue, methodCall);
        }

        public static string Enqueue<T>(Expression<Func<T, Task>> methodCall)
        {
            return Enqueue(GetQueueName(), methodCall);
        }

        public static string Enqueue<T>(string queue, Expression<Func<T, Task>> methodCall)
        {
            queue = GetQueueName(queue);
            return Hangfire.BackgroundJob.Enqueue(queue, methodCall);
        }

        public static string Schedule(Expression<Action> methodCall, TimeSpan delay)
        {
            return Schedule(GetQueueName(), methodCall, delay);
        }

        public static string Schedule(string queue, Expression<Action> methodCall, TimeSpan delay)
        {
            queue = GetQueueName(queue);
            return Hangfire.BackgroundJob.Schedule(queue, methodCall, delay);
        }

        public static string Schedule(Expression<Func<Task>> methodCall, TimeSpan delay)
        {
            return Schedule(GetQueueName(), methodCall, delay);
        }

        public static string Schedule(string queue, Expression<Func<Task>> methodCall, TimeSpan delay)
        {
            queue = GetQueueName(queue);
            return Hangfire.BackgroundJob.Schedule(queue, methodCall, delay);
        }

        public static string Schedule(Expression<Action> methodCall, DateTimeOffset enqueueAt)
        {
            return Schedule(GetQueueName(), methodCall, enqueueAt);
        }

        public static string Schedule(string queue, Expression<Action> methodCall, DateTimeOffset enqueueAt)
        {
            queue = GetQueueName(queue);
            return Hangfire.BackgroundJob.Schedule(queue, methodCall, enqueueAt);
        }

        public static string Schedule(Expression<Func<Task>> methodCall, DateTimeOffset enqueueAt)
        {
            return Schedule(GetQueueName(), methodCall, enqueueAt);
        }

        public static string Schedule(string queue, Expression<Func<Task>> methodCall, DateTimeOffset enqueueAt)
        {
            queue = GetQueueName(queue);
            return Hangfire.BackgroundJob.Schedule(queue, methodCall, enqueueAt);
        }

        public static string Schedule<T>(Expression<Action<T>> methodCall, TimeSpan delay)
        {
            return Schedule(GetQueueName(), methodCall, delay);
        }

        public static string Schedule<T>(string queue, Expression<Action<T>> methodCall, TimeSpan delay)
        {
            queue = GetQueueName(queue);
            return Hangfire.BackgroundJob.Schedule(queue, methodCall, delay);
        }

        public static string Schedule<T>(Expression<Func<T, Task>> methodCall, TimeSpan delay)
        {
            return Schedule(GetQueueName(), methodCall, delay);
        }

        public static string Schedule<T>(string queue, Expression<Func<T, Task>> methodCall, TimeSpan delay)
        {
            queue = GetQueueName(queue);
            return Hangfire.BackgroundJob.Schedule(queue, methodCall, delay);
        }

        public static string Schedule<T>(Expression<Action<T>> methodCall, DateTimeOffset enqueueAt)
        {
            return Schedule(GetQueueName(), methodCall, enqueueAt);
        }

        public static string Schedule<T>(string queue, Expression<Action<T>> methodCall, DateTimeOffset enqueueAt)
        {
            queue = GetQueueName(queue);
            return Hangfire.BackgroundJob.Schedule(queue, methodCall, enqueueAt);
        }

        public static string Schedule<T>(Expression<Func<T, Task>> methodCall, DateTimeOffset enqueueAt)
        {
            return Schedule(GetQueueName(), methodCall, enqueueAt);
        }

        public static string Schedule<T>(string queue, Expression<Func<T, Task>> methodCall, DateTimeOffset enqueueAt)
        {
            queue = GetQueueName(queue);
            return Hangfire.BackgroundJob.Schedule(queue, methodCall, enqueueAt);
        }
    }

    public static class RecurringJob
    {
        public static void AddOrUpdate(string recurringJobId, Expression<Action> methodCall, Func<string> cronExpression)
        {
            AddOrUpdate(recurringJobId, methodCall, cronExpression, new RecurringJobOptions());
        }

        public static void AddOrUpdate(string recurringJobId, Expression<Action> methodCall, Func<string> cronExpression, RecurringJobOptions options)
        {
            AddOrUpdate(recurringJobId, methodCall, cronExpression(), options);
        }

        public static void AddOrUpdate(string recurringJobId, string queue, Expression<Action> methodCall, Func<string> cronExpression)
        {
            queue = GetQueueName(queue);
            AddOrUpdate(recurringJobId, queue, methodCall, cronExpression, new RecurringJobOptions());
        }

        public static void AddOrUpdate(string recurringJobId, string queue, Expression<Action> methodCall, Func<string> cronExpression, RecurringJobOptions options)
        {
            queue = GetQueueName(queue);
            AddOrUpdate(recurringJobId, queue, methodCall, cronExpression(), options);
        }

        public static void AddOrUpdate<T>(string recurringJobId, Expression<Action<T>> methodCall, Func<string> cronExpression)
        {
            AddOrUpdate(recurringJobId, methodCall, cronExpression, new RecurringJobOptions());
        }

        public static void AddOrUpdate<T>(string recurringJobId, Expression<Action<T>> methodCall, Func<string> cronExpression, RecurringJobOptions options)
        {
            AddOrUpdate(recurringJobId, methodCall, cronExpression(), options);
        }

        public static void AddOrUpdate<T>(string recurringJobId, string queue, Expression<Action<T>> methodCall, Func<string> cronExpression)
        {
            queue = GetQueueName(queue);
            AddOrUpdate(recurringJobId, queue, methodCall, cronExpression, new RecurringJobOptions());
        }

        public static void AddOrUpdate<T>(string recurringJobId, string queue, Expression<Action<T>> methodCall, Func<string> cronExpression, RecurringJobOptions options)
        {
            queue = GetQueueName(queue);
            AddOrUpdate(recurringJobId, queue, methodCall, cronExpression(), options);
        }

        public static void AddOrUpdate(string recurringJobId, Expression<Action> methodCall, string cronExpression)
        {
            AddOrUpdate(recurringJobId, methodCall, cronExpression, new RecurringJobOptions());
        }

        public static void AddOrUpdate(string recurringJobId, Expression<Action> methodCall, string cronExpression, RecurringJobOptions options)
        {
            AddOrUpdate(recurringJobId, GetQueueName(), methodCall, cronExpression, options);
        }

        public static void AddOrUpdate(string recurringJobId, string queue, Expression<Action> methodCall, string cronExpression)
        {
            queue = GetQueueName(queue);
            AddOrUpdate(recurringJobId, queue, methodCall, cronExpression, new RecurringJobOptions());
        }

        public static void AddOrUpdate(string recurringJobId, string queue, Expression<Action> methodCall, string cronExpression, RecurringJobOptions options)
        {
            queue = GetQueueName(queue);
            Hangfire.RecurringJob.AddOrUpdate(recurringJobId, queue, methodCall, cronExpression, options);
        }

        public static void AddOrUpdate<T>(string recurringJobId, Expression<Action<T>> methodCall, string cronExpression)
        {
            AddOrUpdate(recurringJobId, methodCall, cronExpression, new RecurringJobOptions());
        }

        public static void AddOrUpdate<T>(string recurringJobId, Expression<Action<T>> methodCall, string cronExpression, RecurringJobOptions options)
        {
            AddOrUpdate(recurringJobId, GetQueueName(), methodCall, cronExpression, options);
        }

        public static void AddOrUpdate<T>(string recurringJobId, string queue, Expression<Action<T>> methodCall, string cronExpression)
        {
            queue = GetQueueName(queue);
            AddOrUpdate(recurringJobId, queue, methodCall, cronExpression, new RecurringJobOptions());
        }

        public static void AddOrUpdate<T>(string recurringJobId, string queue, Expression<Action<T>> methodCall, string cronExpression, RecurringJobOptions options)
        {
            queue = GetQueueName(queue);
            Hangfire.RecurringJob.AddOrUpdate(recurringJobId, queue, methodCall, cronExpression, options);
        }

        public static void AddOrUpdate(string recurringJobId, Expression<Func<Task>> methodCall, Func<string> cronExpression)
        {
            AddOrUpdate(recurringJobId, methodCall, cronExpression, new RecurringJobOptions());
        }

        public static void AddOrUpdate(string recurringJobId, Expression<Func<Task>> methodCall, Func<string> cronExpression, RecurringJobOptions options)
        {
            AddOrUpdate(recurringJobId, methodCall, cronExpression(), options);
        }

        public static void AddOrUpdate(string recurringJobId, string queue, Expression<Func<Task>> methodCall, Func<string> cronExpression)
        {
            queue = GetQueueName(queue);
            AddOrUpdate(recurringJobId, queue, methodCall, cronExpression, new RecurringJobOptions());
        }

        public static void AddOrUpdate(string recurringJobId, string queue, Expression<Func<Task>> methodCall, Func<string> cronExpression, RecurringJobOptions options)
        {
            queue = GetQueueName(queue);
            AddOrUpdate(recurringJobId, queue, methodCall, cronExpression(), options);
        }

        public static void AddOrUpdate<T>(string recurringJobId, Expression<Func<T, Task>> methodCall, Func<string> cronExpression)
        {
            AddOrUpdate(recurringJobId, methodCall, cronExpression, new RecurringJobOptions());
        }

        public static void AddOrUpdate<T>(string recurringJobId, Expression<Func<T, Task>> methodCall, Func<string> cronExpression, RecurringJobOptions options)
        {
            AddOrUpdate(recurringJobId, methodCall, cronExpression(), options);
        }

        public static void AddOrUpdate<T>(string recurringJobId, string queue, Expression<Func<T, Task>> methodCall, Func<string> cronExpression)
        {
            queue = GetQueueName(queue);
            AddOrUpdate(recurringJobId, queue, methodCall, cronExpression, new RecurringJobOptions());
        }

        public static void AddOrUpdate<T>(string recurringJobId, string queue, Expression<Func<T, Task>> methodCall, Func<string> cronExpression, RecurringJobOptions options)
        {
            queue = GetQueueName(queue);
            AddOrUpdate(recurringJobId, queue, methodCall, cronExpression(), options);
        }

        public static void AddOrUpdate(string recurringJobId, Expression<Func<Task>> methodCall, string cronExpression)
        {
            AddOrUpdate(recurringJobId, methodCall, cronExpression, new RecurringJobOptions());
        }

        public static void AddOrUpdate(string recurringJobId, Expression<Func<Task>> methodCall, string cronExpression, RecurringJobOptions options)
        {
            AddOrUpdate(recurringJobId, GetQueueName(), methodCall, cronExpression, options);
        }

        public static void AddOrUpdate(string recurringJobId, string queue, Expression<Func<Task>> methodCall, string cronExpression)
        {
            queue = GetQueueName(queue);
            AddOrUpdate(recurringJobId, queue, methodCall, cronExpression, new RecurringJobOptions());
        }

        public static void AddOrUpdate(string recurringJobId, string queue, Expression<Func<Task>> methodCall, string cronExpression, RecurringJobOptions options)
        {
            queue = GetQueueName(queue);
            Hangfire.RecurringJob.AddOrUpdate(recurringJobId, queue, methodCall, cronExpression, options);
        }

        public static void AddOrUpdate<T>(string recurringJobId, Expression<Func<T, Task>> methodCall, string cronExpression)
        {
            AddOrUpdate(recurringJobId, methodCall, cronExpression, new RecurringJobOptions());
        }

        public static void AddOrUpdate<T>(string recurringJobId, Expression<Func<T, Task>> methodCall, string cronExpression, RecurringJobOptions options)
        {
            AddOrUpdate(recurringJobId, GetQueueName(), methodCall, cronExpression, options);
        }

        public static void AddOrUpdate<T>(string recurringJobId, string queue, Expression<Func<T, Task>> methodCall, string cronExpression)
        {
            queue = GetQueueName(queue);
            AddOrUpdate(recurringJobId, queue, methodCall, cronExpression, new RecurringJobOptions());
        }

        public static void AddOrUpdate<T>(string recurringJobId, string queue, Expression<Func<T, Task>> methodCall, string cronExpression, RecurringJobOptions options)
        {
            queue = GetQueueName(queue);
            Hangfire.RecurringJob.AddOrUpdate<T>(recurringJobId, queue, methodCall, cronExpression, options);
        }
    }

}