using System.Buffers;

namespace KetPsi.Extensions.Logging.OpenTelemetry.Internals;

/// <summary>
/// Minimal IBufferWriter over ArrayPool for one-shot UTF-8 rendering. Not thread-safe.
/// </summary>
internal sealed class PooledByteBufferWriter : IBufferWriter<byte>, KetPsi.Extensions.Logging.Abstractions.IPositionedBufferWriter, IDisposable
{
    private byte[]? _buffer;
    private int _index;

    public PooledByteBufferWriter(int initialCapacity = 256)
    {
        _buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
        _index = 0;
    }

    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _index);

    public int WrittenCount => _index;

    internal Span<byte> GetWrittenSpanMutable() => _buffer.AsSpan(0, _index);

    public void Advance(int count)
    {
        if (_buffer is null) throw new ObjectDisposedException(nameof(PooledByteBufferWriter));
        if (count < 0 || _index + count > _buffer.Length)
            throw new ArgumentOutOfRangeException(nameof(count));
        _index += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        CheckAndResize(sizeHint);
        return _buffer.AsMemory(_index);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        CheckAndResize(sizeHint);
        return _buffer.AsSpan(_index);
    }

    public void Reset() => _index = 0;

    private const int MaxCachedSize = 64 * 1024;

    /// <summary>
    /// Takes the writer cached in <paramref name="slot"/> (creating one if empty) and clears the slot,
    /// so a nested user on the same thread gets its own writer instead of corrupting this one.
    /// </summary>
    public static PooledByteBufferWriter Rent(ref PooledByteBufferWriter? slot)
    {
        var writer = slot ?? new PooledByteBufferWriter(256);
        slot = null;
        writer.Reset();
        return writer;
    }

    /// <summary>Puts <paramref name="writer"/> back into <paramref name="slot"/>; oversized buffers go back to the pool instead.</summary>
    public static void Return(ref PooledByteBufferWriter? slot, PooledByteBufferWriter writer)
    {
        if (writer._buffer is { Length: <= MaxCachedSize }) slot = writer;
        else writer.Dispose();
    }

    public void Dispose()
    {
        if (_buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = null;
        }
    }

    private void CheckAndResize(int sizeHint)
    {
        if (_buffer is null) throw new ObjectDisposedException(nameof(PooledByteBufferWriter));
        // IBufferWriter contract: GetSpan(0) must still return a non-empty span.
        if (sizeHint <= 0) sizeHint = 1;
        int available = _buffer.Length - _index;
        if (sizeHint <= available) return;

        int grow = Math.Max(_buffer.Length * 2, _index + Math.Max(sizeHint, 256));
        byte[] next = ArrayPool<byte>.Shared.Rent(grow);
        _buffer.AsSpan(0, _index).CopyTo(next);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = next;
    }
}
