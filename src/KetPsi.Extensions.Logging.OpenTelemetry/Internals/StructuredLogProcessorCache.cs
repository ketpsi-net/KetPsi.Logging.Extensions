using System.Reflection;

using KetPsi.Extensions.Logging.Abstractions;

using Microsoft.Extensions.Logging;

namespace KetPsi.Extensions.Logging.OpenTelemetry.Internals;

// 1. The Generic Delegate Cache
internal static class StructuredLogProcessorCache<TState>
{
    public delegate void ProcessDelegate(ZeroAllocOtlpLogger logger, LogLevel level, ref TState state, Exception? exception);

    public static readonly ProcessDelegate? Process;

    static StructuredLogProcessorCache()
    {
        Type stateType = typeof(TState);

        // Ensure it's a struct and implements our target interface
        if (stateType.IsValueType && typeof(IStructuredLogState).IsAssignableFrom(stateType))
        {
            MethodInfo method = typeof(ZeroAllocOtlpLogger).GetMethod(
                nameof(ZeroAllocOtlpLogger.ProcessStructured),
                BindingFlags.Instance | BindingFlags.NonPublic)!;

            MethodInfo genericMethod = method.MakeGenericMethod(stateType);

            // Creates a native, unboxed execution bridge to the generic method
            Process = (ProcessDelegate)Delegate.CreateDelegate(typeof(ProcessDelegate), genericMethod);
        }
    }
}