using System.Collections;
using System.Data.Common;

namespace NewHeap.Platform.AspNet.Common.DAL.Bulk;

internal sealed class BulkUpsertDataReader<TEntity> : DbDataReader
    where TEntity : class
{
    private readonly IEnumerator<TEntity> _enumerator;
    private readonly IReadOnlyList<BulkUpsertProperty<TEntity>> _properties;
    private readonly string? _sourceOrdinalColumnName;
    private readonly List<TEntity>? _stagedEntities;
    private readonly bool _hasRows;
    private TEntity? _buffered;
    private TEntity? _current;
    private bool _started;
    private bool _closed;
    private long _sourceOrdinal = -1;

    public BulkUpsertDataReader(
        IEnumerable<TEntity> entities,
        IReadOnlyList<BulkUpsertProperty<TEntity>> properties,
        string? sourceOrdinalColumnName = null)
    {
        _properties = properties;
        _sourceOrdinalColumnName = sourceOrdinalColumnName;
        _stagedEntities = sourceOrdinalColumnName is null ? null : [];
        _enumerator = entities.GetEnumerator();
        try
        {
            _hasRows = _enumerator.MoveNext();
            if (_hasRows)
            {
                _buffered = _enumerator.Current
                    ?? throw new InvalidOperationException("Bulk upsert input cannot contain null entities.");
            }
        }
        catch
        {
            _enumerator.Dispose();
            throw;
        }
    }

    internal IReadOnlyList<TEntity> StagedEntities => _stagedEntities
        ?? throw new InvalidOperationException("Source ordinals were not enabled for this data reader.");

    public override int FieldCount => _properties.Count + (_sourceOrdinalColumnName is null ? 0 : 1);

    public override bool HasRows => _hasRows;

    public override bool IsClosed => _closed;

    public override int RecordsAffected => -1;

    public override int Depth => 0;

    public override object this[int ordinal] => GetValue(ordinal);

    public override object this[string name] => GetValue(GetOrdinal(name));

    public override bool Read()
    {
        if (!_started)
        {
            _started = true;
            _current = _buffered;
            _buffered = null;
            return PositionOnCurrent();
        }

        if (!_enumerator.MoveNext())
        {
            _current = null;
            return false;
        }

        _current = _enumerator.Current
            ?? throw new InvalidOperationException("Bulk upsert input cannot contain null entities.");
        return PositionOnCurrent();
    }

    public override Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Read());
    }

    public override object GetValue(int ordinal)
    {
        EnsureCurrent();
        if (ordinal == _properties.Count && _sourceOrdinalColumnName is not null)
        {
            return _sourceOrdinal;
        }

        return _properties[ordinal].GetProviderValue(_current!) ?? DBNull.Value;
    }

    public override int GetValues(object[] values)
    {
        var count = Math.Min(values.Length, FieldCount);
        for (var ordinal = 0; ordinal < count; ordinal++)
        {
            values[ordinal] = GetValue(ordinal);
        }

        return count;
    }

    public override bool IsDBNull(int ordinal) => GetValue(ordinal) is DBNull;

    public override string GetName(int ordinal) =>
        ordinal == _properties.Count && _sourceOrdinalColumnName is not null
            ? _sourceOrdinalColumnName
            : _properties[ordinal].ColumnName;

    public override string GetDataTypeName(int ordinal) =>
        ordinal == _properties.Count && _sourceOrdinalColumnName is not null
            ? "bigint"
            : _properties[ordinal].StoreTypeName;

    public override Type GetFieldType(int ordinal) =>
        ordinal == _properties.Count && _sourceOrdinalColumnName is not null
            ? typeof(long)
            : _properties[ordinal].ProviderClrType;

    public override int GetOrdinal(string name)
    {
        for (var ordinal = 0; ordinal < FieldCount; ordinal++)
        {
            if (string.Equals(GetName(ordinal), name, StringComparison.OrdinalIgnoreCase))
            {
                return ordinal;
            }
        }

        throw new IndexOutOfRangeException($"Column '{name}' was not found.");
    }

    public override bool GetBoolean(int ordinal) => (bool)GetValue(ordinal);

    public override byte GetByte(int ordinal) => (byte)GetValue(ordinal);

    public override char GetChar(int ordinal) => (char)GetValue(ordinal);

    public override DateTime GetDateTime(int ordinal) => (DateTime)GetValue(ordinal);

    public override decimal GetDecimal(int ordinal) => (decimal)GetValue(ordinal);

    public override double GetDouble(int ordinal) => (double)GetValue(ordinal);

    public override float GetFloat(int ordinal) => (float)GetValue(ordinal);

    public override Guid GetGuid(int ordinal) => (Guid)GetValue(ordinal);

    public override short GetInt16(int ordinal) => (short)GetValue(ordinal);

    public override int GetInt32(int ordinal) => (int)GetValue(ordinal);

    public override long GetInt64(int ordinal) => (long)GetValue(ordinal);

    public override string GetString(int ordinal) => (string)GetValue(ordinal);

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
    {
        var value = (byte[])GetValue(ordinal);
        if (buffer is null)
        {
            return value.Length;
        }

        var count = Math.Min(length, value.Length - (int)dataOffset);
        Array.Copy(value, dataOffset, buffer, bufferOffset, count);
        return count;
    }

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        var value = GetString(ordinal).ToCharArray();
        if (buffer is null)
        {
            return value.Length;
        }

        var count = Math.Min(length, value.Length - (int)dataOffset);
        Array.Copy(value, dataOffset, buffer, bufferOffset, count);
        return count;
    }

    public override bool NextResult() => false;

    public override Task<bool> NextResultAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(false);
    }

    public override IEnumerator GetEnumerator() => new DbEnumerator(this, closeReader: false);

    public override void Close()
    {
        DisposeReader();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeReader();
        }
    }

    private void EnsureCurrent()
    {
        if (_current is null)
        {
            throw new InvalidOperationException("The data reader is not positioned on a row.");
        }
    }

    private bool PositionOnCurrent()
    {
        if (_current is null)
        {
            return false;
        }

        if (_stagedEntities is not null)
        {
            _sourceOrdinal++;
            _stagedEntities.Add(_current);
        }

        return true;
    }

    private void DisposeReader()
    {
        if (_closed)
        {
            return;
        }

        _enumerator.Dispose();
        _closed = true;
    }
}
