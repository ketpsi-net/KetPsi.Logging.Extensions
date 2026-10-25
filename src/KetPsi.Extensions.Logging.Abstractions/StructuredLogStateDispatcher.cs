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
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryFormatTo<TState>(ref TState state, IBufferWriter<byte> writer)
    {
        var visitor = new FormatToVisitor(writer);
        return TryAccept(ref state, ref visitor);
    }

    private sealed class Dispatcher<TState> : StructuredLogStateDispatcher<TState> where TState : struct, IStructuredLogState, IDeferredUtf8Formatter
    {
        public override void Accept<TVisitor>(ref TState state, ref TVisitor visitor) => visitor.Visit(ref state);
    }

    private readonly struct FormatToVisitor(IBufferWriter<byte> writer) : IStructuredLogStateVisitor
    {
        public void Visit<TState>(ref TState state) where TState : struct, IStructuredLogState, IDeferredUtf8Formatter
            => state.FormatTo(writer);
    }
}
