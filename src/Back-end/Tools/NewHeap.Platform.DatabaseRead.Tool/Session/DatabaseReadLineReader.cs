namespace NewHeap.Platform.DatabaseRead;

/// <summary>
/// One line of input. An oversized line has no bytes: the reader dropped them as they came in.
/// </summary>
internal sealed record DatabaseReadLine(ReadOnlyMemory<byte> Bytes, bool IsOversized);

/// <summary>
/// Reads newline-separated requests with a bound on what it holds in memory, which a
/// <see cref="StreamReader"/> does not have. A line over the bound is read to its end and reported,
/// so the next line starts where it should.
/// </summary>
internal sealed class DatabaseReadLineReader(Stream input, int maximumLineBytes)
{
    private static readonly byte[] Utf8ByteOrderMark = [0xEF, 0xBB, 0xBF];

    private readonly byte[] _chunk = new byte[64 * 1024];
    private readonly MemoryStream _line = new();
    private int _chunkStart;
    private int _chunkEnd;
    private bool _firstLine = true;

    /// <summary>
    /// The next line, or <see langword="null"/> at the end of the stream. A last line without a
    /// newline still counts.
    /// </summary>
    public async Task<DatabaseReadLine?> ReadLineAsync(CancellationToken cancellationToken)
    {
        _line.SetLength(0);
        var oversized = false;

        while (true)
        {
            if (_chunkStart == _chunkEnd)
            {
                _chunkStart = 0;
                _chunkEnd = await input.ReadAsync(_chunk, cancellationToken);
                if (_chunkEnd == 0)
                {
                    return oversized || _line.Length > 0 ? CompleteLine(oversized) : null;
                }
            }

            var available = _chunk.AsMemory(_chunkStart, _chunkEnd - _chunkStart);
            var newline = available.Span.IndexOf((byte)'\n');
            var length = newline < 0 ? available.Length : newline;

            if (_line.Length + length > maximumLineBytes)
            {
                oversized = true;
                _line.SetLength(0);
            }

            if (!oversized)
            {
                await _line.WriteAsync(available[..length], cancellationToken);
            }

            _chunkStart += newline < 0 ? length : length + 1;
            if (newline >= 0)
            {
                return CompleteLine(oversized);
            }
        }
    }

    private DatabaseReadLine CompleteLine(bool oversized)
    {
        var bytes = _line.GetBuffer().AsMemory(0, (int)_line.Length);
        if (_firstLine && bytes.Span.StartsWith(Utf8ByteOrderMark))
        {
            bytes = bytes[Utf8ByteOrderMark.Length..];
        }

        _firstLine = false;

        return new DatabaseReadLine(bytes, oversized);
    }
}
