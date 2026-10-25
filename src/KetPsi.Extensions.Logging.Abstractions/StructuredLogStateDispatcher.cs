using System.Buffers;
using System.Runtime.CompilerServices;

namespace KetPsi.Extensions.Logging.Abstractions;

/// <summary>
/// Callback a sink implements (as a struct) to receive a generated log state with its concrete type,
/// so it can call <see cref="IStructuredLogState"/> / <see cref="IDeferredUtf8Formatter"/> members without boxing.
/// </summary>
public interface IStructuredLogStateVisitor
{
    void Visit<TState>(ref TState state) where TState : struct, IStructuredLogState, IDeferredUtf8Formatter;
}

/// <summary>
/// Bridges an unconstrained <typeparamref name="TState"/> (as received by <c>ILogger.Log&lt;TState&gt;</c>) to a
/// constrained visitor call. Instances are registered by generated code, replacing reflection-based
/// <c>MakeGenericMethod</c> lookups that fail under NativeAOT.
/// </summary>
public abstract class StructuredLogStateDispatcher<TState>
{
    /// <summary>The dispatcher for <typeparamref name="TState"/>, or <see langword="null"/> when it is not a generated KetPsi state.</summary>
    public static StructuredLogStateDispatcher<TState>? Instance { get; internal set; }

    public abstract void Accept<TVisitor>(ref TState state, ref TVisitor visitor) where TVisitor : struct, IStructuredLogStateVisitor;
}

public static class StructuredLogStateDispatcher
{
    /// <summary>Registers a generated state type. Called from generated module initializers.</summary>
    public static void Register<TState>() where TState : struct, IStructuredLogState, IDeferredUtf8Formatter
        => StructuredLogStateDispatcher<TState>.Instance ??= new Dispatcher<TState>();

    /// <summary>
    /// Invokes <paramref name="visitor"/> with <paramref name="state"/> when it is a registered generated state.
    /// Returns <see langword="false"/> (without touching the visitor) otherwise.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryAccept<TState, TVisitor>(ref TState state, ref TVisitor visitor) where TVisitor : struct, IStructuredLogStateVisitor
    {
        var dispatcher = StructuredLogStateDispatcher<TState>.Instance;
        if (dispatcher is null) return false;
        dispatcher.Accept(ref state, ref visitor);
        return true;
    }

    /// <summary>Renders the message of a generated state as UTF-8. Returns <see langword="false"/> for any other state type.</summary>
    /// <remarks>
    /// The hot path is a static field read plus a delegate call. The visitor double dispatch (a generic virtual
    /// call) runs once per state type, in <see cref="InitializeAndFormatTo{TState}"/>, to populate the cache.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryFormatTo<TState>(ref TState state, IBufferWriter<byte> writer)
    {
        var formatter = FormatToCache<TState>.Formatter;
        if (formatter != null)
        {
            formatter(ref state, writer);
            return true;
        }

        // Already probed and not a generated KetPsi state: don't probe again.
        if (Volatile.Read(ref FormatToCache<TState>.IsInitialized))
            return TryFormatToCached(ref state, writer);

        return InitializeAndFormatTo(ref state, writer);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool InitializeAndFormatTo<TState>(ref TState state, IBufferWriter<byte> writer)
    {
        var populator = new FormatToPopulatorVisitor();
        TryAccept(ref state, ref populator);

        // The visitor only runs for generated states; mark other types too so they are never probed again.
        Volatile.Write(ref FormatToCache<TState>.IsInitialized, true);

        return TryFormatToCached(ref state, writer);
    }

    // Re-read with acquire semantics: another thread may have populated the cache since the hot-path check.
    private static bool TryFormatToCached<TState>(ref TState state, IBufferWriter<byte> writer)
    {
        var formatter = Volatile.Read(ref FormatToCache<TState>.Formatter);
        if (formatter is null) return false;

        formatter(ref state, writer);
        return true;
    }

    private sealed class Dispatcher<TState> : StructuredLogStateDispatcher<TState> where TState : struct, IStructuredLogState, IDeferredUtf8Formatter
    {
        public override void Accept<TVisitor>(ref TState state, ref TVisitor visitor) => visitor.Visit(ref state);
    }

    /// <summary>Per-state-type cache for <see cref="TryFormatTo{TState}"/>.</summary>
    private static class FormatToCache<TState>
    {
        public delegate void FormatToDelegate(ref TState state, IBufferWriter<byte> writer);

        public static FormatToDelegate? Formatter;

        /// <summary>True once the type has been probed, so non-KetPsi states skip the probe afterwards.</summary>
        public static bool IsInitialized;
    }

    /// <summary>Captures the concrete state type once and caches a constrained (boxing-free) FormatTo call for it.</summary>
    private readonly struct FormatToPopulatorVisitor : IStructuredLogStateVisitor
    {
        public void Visit<TActualState>(ref TActualState state) where TActualState : struct, IStructuredLogState, IDeferredUtf8Formatter
        {
            // Static lambda: no captured state, so the delegate is allocated once per state type and cached.
            Volatile.Write(
                ref FormatToCache<TActualState>.Formatter,
                static (ref TActualState s, IBufferWriter<byte> w) => s.FormatTo(w));
            Volatile.Write(ref FormatToCache<TActualState>.IsInitialized, true);
        }
    }
}
