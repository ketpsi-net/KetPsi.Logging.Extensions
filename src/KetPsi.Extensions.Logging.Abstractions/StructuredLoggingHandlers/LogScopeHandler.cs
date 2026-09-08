using System.Buffers;
using System.Runtime.CompilerServices;

namespace Microsoft.Extensions.Logging;

[InterpolatedStringHandler]
public ref struct StartScopeHandler
{
    public byte[]? Bytes;
    public object?[]? References;
    public int ByteOffset;
    public int RefOffset;

    public StartScopeHandler(int literalLength, int formattedCount, ILogger logger, out bool isEnabled)
    {
        isEnabled = logger != null;
        if (!isEnabled) { Bytes = null; References = null; ByteOffset = 0; RefOffset = 0; return; }

        Bytes = ArrayPool<byte>.Shared.Rent(formattedCount * 32);
        References = ArrayPool<object?>.Shared.Rent(formattedCount);
        ByteOffset = 0;
        RefOffset = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AppendLiteral(string s) { }

    // ADDED: alignment and format optional parameters
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AppendFormatted<T>(T value, int alignment = 0, string? format = null)
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            References![RefOffset++] = value;
        }
        else
        {
            Unsafe.WriteUnaligned(ref Bytes![ByteOffset], value);
            ByteOffset += Unsafe.SizeOf<T>();
        }
    }
}