using System.Buffers;
using System.Runtime.CompilerServices;

namespace KetPsi.Extensions.Logging.Abstractions;

/// <summary>
/// Shared storage logic for the interpolated string handlers.
/// </summary>
internal static class HandlerBuffer
{
    /// <summary>
    /// Appends an unmanaged value to the pooled byte buffer, growing it through <see cref="ArrayPool{T}"/>
    /// when the initial 32-bytes-per-hole estimate is exceeded (large structs).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteUnmanaged<T>(ref byte[]? bytes, ref int offset, T value)
    {
        int size = Unsafe.SizeOf<T>();
        if (bytes is null || offset + size > bytes.Length) Grow(ref bytes, offset, size);
        Unsafe.WriteUnaligned(ref bytes![offset], value);
        offset += size;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Grow(ref byte[]? bytes, int used, int needed)
    {
        int newSize = Math.Max(used + needed, (bytes?.Length ?? 0) * 2);
        byte[] grown = ArrayPool<byte>.Shared.Rent(newSize);
        if (bytes != null)
        {
            bytes.AsSpan(0, used).CopyTo(grown);
            ArrayPool<byte>.Shared.Return(bytes);
        }
        bytes = grown;
    }
}
