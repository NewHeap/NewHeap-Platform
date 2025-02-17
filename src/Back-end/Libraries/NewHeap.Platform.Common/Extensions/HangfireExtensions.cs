using Hangfire.Console;
using Hangfire.Server;

namespace NewHeap.Platform.Common.Extensions;
public static partial class HangfireExtensions
{
    public static PerformContext WriteError(this PerformContext context, string text)
    {
        return context.WriteColor(ConsoleTextColor.Red, text);
    }

    public static PerformContext WriteWarning(this PerformContext context, string text)
    {
        return context.WriteColor(ConsoleTextColor.DarkYellow, text);
    }

    public static PerformContext WriteSuccess(this PerformContext context, string text)
    {
        return context.WriteColor(ConsoleTextColor.Green, text);
    }

    public static PerformContext WriteColor(this PerformContext context, ConsoleTextColor color, string text)
    {
        context.SetTextColor(ConsoleTextColor.Green);
        context.WriteLine(text);
        context.ResetTextColor();

        return context;
    }
}
