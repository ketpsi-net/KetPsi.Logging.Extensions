using System.Buffers;

namespace KetPsi.Extensions.Logging.Abstractions;

/// <summary>
/// A buffer writer that exposes how many bytes it holds, so generated code can record where each
/// parameter value lands in a rendered message (sinks reuse those bytes instead of formatting again).
/// </summary>
public interface IPositionedBufferWriter : IBufferWriter<byte>
{
    int WrittenCount { get; }
}
