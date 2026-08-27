using System.Text.Json;
using System.Text.Json.Serialization;

namespace NewHeap.Platform.DatabaseRead;

internal static class DatabaseReadJson
{
    public const int MaximumRequestBytes = 2 * 1024 * 1024;

    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task<DatabaseReadRequest> ReadRequestAsync(
        Stream input,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];

        while (true)
        {
            var count = await input.ReadAsync(chunk, cancellationToken);
            if (count == 0)
            {
                break;
            }

            if (buffer.Length + count > MaximumRequestBytes)
            {
                throw new DatabaseReadExpectedException(
                    "request-too-large",
                    $"The JSON request exceeds the hard limit of {MaximumRequestBytes} bytes.",
                    DatabaseReadExitCode.InvalidRequest);
            }

            await buffer.WriteAsync(chunk.AsMemory(0, count), cancellationToken);
        }

        if (buffer.Length == 0)
        {
            throw new DatabaseReadExpectedException(
                "empty-request",
                "The JSON request on stdin is empty.",
                DatabaseReadExitCode.InvalidRequest);
        }

        buffer.Position = 0;
        var request = await JsonSerializer.DeserializeAsync<DatabaseReadRequest>(
            buffer,
            Options,
            cancellationToken);

        return request ?? throw new DatabaseReadExpectedException(
            "invalid-request",
            "The JSON request must be an object.",
            DatabaseReadExitCode.InvalidRequest);
    }

    public static async Task WriteAsync(
        Stream output,
        object response,
        CancellationToken cancellationToken)
    {
        await JsonSerializer.SerializeAsync(output, response, response.GetType(), Options, cancellationToken);
        await output.WriteAsync("\n"u8.ToArray(), cancellationToken);
        await output.FlushAsync(cancellationToken);
    }

    public static byte[] Serialize(object response)
    {
        return JsonSerializer.SerializeToUtf8Bytes(response, response.GetType(), Options);
    }
}
