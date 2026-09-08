using System.Buffers;

namespace KetPsi.Extensions.Logging.Console.Internals;

internal sealed class ScopeNode : IDisposable
{
    // Reusable byte buffer for the pre-rendered scope
    public readonly ArrayBufferWriter<byte> Buffer = new(256);
    public ScopeNode? Parent;
    private int _isDisposed;

    public ReadOnlySpan<byte> WrittenSpan => Buffer.WrittenSpan;

    public void Dispose()
    {
        // Ensure thread-safe single disposal
        if (Interlocked.Exchange(ref _isDisposed, 1) == 0)
        {
            // Restore execution context to parent scope
            ScopeContext.Current = Parent;
            ScopeNodePool.Return(this);
        }
    }

    public void Reset()
    {
        Buffer.Clear(); // Only resets the internal pointer - does NOT deallocate the byte array
        Parent = null;
        _isDisposed = 0;
    }
}

internal static class ScopeContext
{
    // Preserves scope hierarchy across async/await boundaries
    private static readonly AsyncLocal<ScopeNode?> s_current = new();

    public static ScopeNode? Current
    {
        get => s_current.Value;
        set => s_current.Value = value;
    }
}