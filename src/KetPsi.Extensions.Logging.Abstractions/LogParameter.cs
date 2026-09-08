using System.Buffers;
using System.Text;

namespace KetPsi.Extensions.Logging.Abstractions;

public readonly struct LogParameter<T>(string? name, T value, string? format = null) : IDeferredUtf8Formatter
{
    public readonly string? Name = name;
    public readonly T Value = value;
    public readonly string? Format = format;

    public void FormatTo(IBufferWriter<byte> writer)
    {
        if (LogParameterResolver<T>.Utf8Formatter != null)
        {
            Span<byte> destination = writer.GetSpan(256);
            if (LogParameterResolver<T>.Utf8Formatter(Value, destination, out int bytesWritten, Format, null))
            {
                writer.Advance(bytesWritten);
                return;
            }
        }

        if (LogParameterResolver<T>.CharFormatter != null)
        {
            Span<char> charDest = stackalloc char[256];
            if (LogParameterResolver<T>.CharFormatter(Value, charDest, out int charsWritten, Format, null))
            {
                int maxBytes = Encoding.UTF8.GetMaxByteCount(charsWritten);
                int bytesWritten = Encoding.UTF8.GetBytes(charDest[..charsWritten], writer.GetSpan(maxBytes));
                writer.Advance(bytesWritten);
                return;
            }
        }

        // 3. String / Object Fallback
        string stringValue = Value?.ToString() ?? string.Empty;
        if (!string.IsNullOrEmpty(stringValue))
        {
            int maxBytes = Encoding.UTF8.GetMaxByteCount(stringValue.Length);
            int bytesWritten = Encoding.UTF8.GetBytes(stringValue, writer.GetSpan(maxBytes));
            writer.Advance(bytesWritten);
        }
    }
    public KeyValuePair<string, object?> ToKeyValuePair() => new(Name!, Value);



}