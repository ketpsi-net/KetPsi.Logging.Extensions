using System;
using System.Buffers;
using System.Runtime.CompilerServices;

using KetPsi.Extensions.Logging.OpenTelemetry.Internals;

namespace KetPsi.Extensions.Logging.OpenTelemetry;

public sealed class OtlpResourceBuilder : IDisposable
{
    private byte[]? _rentedBuffer;
    private int _offset;

    internal byte[] ResourceBytes { get; private set; } = Array.Empty<byte>();

    internal OtlpResourceBuilder(int initialCapacity = 4096)
    {
        _rentedBuffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
        _offset = 0;
    }

    public OtlpResourceBuilder AddAttribute<T>(string key, T value)
    {
        if (_rentedBuffer == null)
            throw new ObjectDisposedException(nameof(OtlpResourceBuilder));

        ReadOnlySpan<char> nameSpan = key.AsSpan();
        Type type = typeof(T);
        int size;

        if (type == typeof(long))
            size = OtlpAttributeFormatter.CalculateInt64Size(nameSpan, Unsafe.As<T, long>(ref value));
        else if (type == typeof(int))
            size = OtlpAttributeFormatter.CalculateInt64Size(nameSpan, Unsafe.As<T, int>(ref value));
        else if (type == typeof(short))
            size = OtlpAttributeFormatter.CalculateInt64Size(nameSpan, Unsafe.As<T, short>(ref value));
        else if (type == typeof(byte))
            size = OtlpAttributeFormatter.CalculateInt64Size(nameSpan, Unsafe.As<T, byte>(ref value));
        else if (type == typeof(double))
            size = OtlpAttributeFormatter.CalculateDoubleSize(nameSpan, Unsafe.As<T, double>(ref value));
        else if (type == typeof(float))
            size = OtlpAttributeFormatter.CalculateDoubleSize(nameSpan, Unsafe.As<T, float>(ref value));
        else if (type == typeof(bool))
            size = OtlpAttributeFormatter.CalculateBoolSize(nameSpan, Unsafe.As<T, bool>(ref value));
        else if (type == typeof(string))
        {
            string? s = Unsafe.As<T, string>(ref value);
            size = OtlpAttributeFormatter.CalculateStringSize(nameSpan, s.AsSpan());
        }
        else
        {
            size = OtlpAttributeFormatter.CalculateStringSize(
                nameSpan,
                value is null ? ReadOnlySpan<char>.Empty : value.ToString().AsSpan());
        }

        EnsureCapacity(size);

        var span = _rentedBuffer.AsSpan(_offset);
        var writer = new ZeroAllocProtobufWriter(span);

        if (type == typeof(long))
            OtlpAttributeFormatter.WriteInt64(ref writer, nameSpan, Unsafe.As<T, long>(ref value));
        else if (type == typeof(int))
            OtlpAttributeFormatter.WriteInt64(ref writer, nameSpan, Unsafe.As<T, int>(ref value));
        else if (type == typeof(short))
            OtlpAttributeFormatter.WriteInt64(ref writer, nameSpan, Unsafe.As<T, short>(ref value));
        else if (type == typeof(byte))
            OtlpAttributeFormatter.WriteInt64(ref writer, nameSpan, Unsafe.As<T, byte>(ref value));
        else if (type == typeof(double))
            OtlpAttributeFormatter.WriteDouble(ref writer, nameSpan, Unsafe.As<T, double>(ref value));
        else if (type == typeof(float))
            OtlpAttributeFormatter.WriteDouble(ref writer, nameSpan, Unsafe.As<T, float>(ref value));
        else if (type == typeof(bool))
            OtlpAttributeFormatter.WriteBool(ref writer, nameSpan, Unsafe.As<T, bool>(ref value));
        else if (type == typeof(string))
        {
            string? s = Unsafe.As<T, string>(ref value);
            OtlpAttributeFormatter.WriteString(ref writer, nameSpan, s.AsSpan());
        }
        else
        {
            OtlpAttributeFormatter.WriteString(
                ref writer,
                nameSpan,
                value is null ? ReadOnlySpan<char>.Empty : value.ToString().AsSpan());
        }

        _offset += writer.BytesWritten;
        return this;
    }

    public OtlpResourceBuilder AddService(
        string serviceName,
        string version = "1.0.0",
        string? serviceNamespace = null,
        string? serviceInstanceId = null)
    {
        AddAttribute("service.name", serviceName);
        AddAttribute("service.version", version);

        if (!string.IsNullOrEmpty(serviceNamespace))
        {
            AddAttribute("service.namespace", serviceNamespace);
        }

        if (!string.IsNullOrEmpty(serviceInstanceId))
        {
            AddAttribute("service.instance.id", serviceInstanceId);
        }

        return this;
    }

    private OtlpResourceBuilder Default() => new OtlpResourceBuilder().AddService("unknow-service");
    /// <summary>
    /// Returns ONLY the raw attributes (repeated KeyValue).
    /// Do NOT wrap with Tag(1,2) here – that is done later in StitchNetworkPayload.
    /// </summary>
    public byte[] Build()
    {
        int attributesPayloadSize = _offset;

        // Build Resource Message (Field 1)
        int headerSize = 1 + ProtobufMath.GetVarIntSize((ulong)attributesPayloadSize);
        ResourceBytes = new byte[headerSize + attributesPayloadSize];
        var writer = new ZeroAllocProtobufWriter(ResourceBytes);

        writer.WriteTag(1, 2);
        writer.WriteVarInt((ulong)attributesPayloadSize);

        _rentedBuffer.AsSpan(0, _offset).CopyTo(ResourceBytes.AsSpan(writer.BytesWritten));

        System.Buffers.ArrayPool<byte>.Shared.Return(_rentedBuffer);
        return ResourceBytes;
    }

    private void EnsureCapacity(int requiredSize)
    {
        if (_offset + requiredSize <= _rentedBuffer!.Length)
            return;

        int newSize = Math.Max(_rentedBuffer.Length * 2, _offset + requiredSize);
        byte[] newBuffer = ArrayPool<byte>.Shared.Rent(newSize);
        _rentedBuffer.AsSpan(0, _offset).CopyTo(newBuffer);
        ArrayPool<byte>.Shared.Return(_rentedBuffer);
        _rentedBuffer = newBuffer;
    }

    public void Dispose()
    {
        if (_rentedBuffer != null)
        {
            ArrayPool<byte>.Shared.Return(_rentedBuffer);
            _rentedBuffer = null;
        }
    }
}