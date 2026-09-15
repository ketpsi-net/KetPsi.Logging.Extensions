using System.Runtime.CompilerServices;

namespace KetPsi.Extensions.Logging.OpenTelemetry.Internals;

internal static class ProtobufMath
{
    /// <summary>
    /// Calculates the exact number of bytes required to store a VarInt.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetVarIntSize(ulong value)
    {
        int size = 1;
        while (value >= 0x80)
        {
            size++;
            value >>= 7;
        }
        return size;
    }

    /// <summary>
    /// Calculates the exact number of bytes required to store a Tag.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetTagSize(uint fieldNumber)
    {
        // WireType is always 3 bits (values 0-5), so the shift is constant.
        uint tag = fieldNumber << 3;
        return GetVarIntSize(tag);
    }
}