namespace NewHeap.Platform.DatabaseRead;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();

        ConsoleCancelEventHandler cancellationHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        Console.CancelKeyPress += cancellationHandler;

        try
        {
            return await NewHeapDatabaseReadApplication.RunAsync(
                args,
                Console.OpenStandardInput(),
                Console.OpenStandardOutput(),
                cancellation.Token);
        }
        finally
        {
            Console.CancelKeyPress -= cancellationHandler;
        }
    }
}
