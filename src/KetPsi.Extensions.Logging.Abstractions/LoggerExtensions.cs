using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Microsoft.Extensions.Logging
{
    /// <summary>
    /// Provides extension methods for <see cref="ILogger"/> to support advanced interpolated string logging.
    /// </summary>
    /// <remarks>
    /// This class enables passing interpolated strings to standard log level methods (e.g., <c>logger.LogInformation($"...")</c>).
    /// <br/><br/>
    /// <b>Supported Custom Interpolation Formats:</b>
    /// <list type="bullet">
    /// <item><description><c>{value:@name}</c> : Overrides the structured log property name.</description></item>
    /// <item><description><c>{value:format}</c> : Applies standard C# string formatting (e.g., <c>C2</c>, <c>yyyy-MM-dd</c>).</description></item>
    /// <item><description><c>{value:@name:length}</c> : Overrides the property name and sets alignment/length.</description></item>
    /// <item><description><c>{value:@name:format:length}</c> : Overrides the property name, applies standard format, and alignment.</description></item>
    /// <item><description><c>{value:@name:mask}</c> : Overrides the property name and replaces the entire value with <c>***</c>.</description></item>
    /// <item><description><c>{value:@name:mask:4}</c> : Overrides the property name and masks all but the last 4 characters.</description></item>
    /// <item><description><c>{value:@name:format:mask:4}</c> : Combines property name override, standard formatting, and partial masking.</description></item>
    /// </list>
    /// </remarks>
    public static class LoggerExtensions
    {
        /// <summary>
        /// Formats and writes a trace log message.
        /// </summary>
        /// <param name="logger">The <see cref="ILogger"/> to write to.</param>
        /// <param name="handler">The interpolated string handler containing the structured template and arguments.</param>
        public static void LogTrace(this ILogger logger,
                                    [InterpolatedStringHandlerArgument("logger")] ref LogTraceHandler handler)
        {
            CleanUp(ref handler.Bytes, ref handler.References);
        }

        /// <summary>
        /// Formats and writes a trace log message with an exception.
        /// </summary>
        /// <param name="logger">The <see cref="ILogger"/> to write to.</param>
        /// <param name="exception">The exception to log.</param>
        /// <param name="handler">The interpolated string handler containing the structured template and arguments.</param>
        public static void LogTrace(this ILogger logger,
                                    Exception? exception,
                                    [InterpolatedStringHandlerArgument("logger")] ref LogTraceHandler handler)
        {
            CleanUp(ref handler.Bytes, ref handler.References);
        }

        /// <summary>
        /// Formats and writes a debug log message.
        /// </summary>
        /// <param name="logger">The <see cref="ILogger"/> to write to.</param>
        /// <param name="handler">The interpolated string handler containing the structured template and arguments.</param>
        public static void LogDebug(this ILogger logger,
                                    [InterpolatedStringHandlerArgument("logger")] ref LogDebugHandler handler)
        {
            CleanUp(ref handler.Bytes, ref handler.References);
        }

        /// <summary>
        /// Formats and writes a debug log message with an exception.
        /// </summary>
        /// <param name="logger">The <see cref="ILogger"/> to write to.</param>
        /// <param name="exception">The exception to log.</param>
        /// <param name="handler">The interpolated string handler containing the structured template and arguments.</param>
        public static void LogDebug(this ILogger logger,
                                    Exception? exception,
                                    [InterpolatedStringHandlerArgument("logger")] ref LogDebugHandler handler)
        {
            CleanUp(ref handler.Bytes, ref handler.References);
        }

        ///// <summary>
        ///// Formats and writes an information log message.
        ///// </summary>
        ///// <param name="logger">The <see cref="ILogger"/> to write to.</param>
        ///// <param name="handler">The interpolated string handler containing the structured template and arguments.</param>
        public static void LogInformation(this ILogger logger,
                                          [InterpolatedStringHandlerArgument("logger")] ref LogInformationHandler handler)
        {
            CleanUp(ref handler.Bytes, ref handler.References);
        }

        /// <summary>
        /// Formats and writes an information log message with an exception.
        /// </summary>
        /// <param name="logger">The <see cref="ILogger"/> to write to.</param>
        /// <param name="exception">The exception to log.</param>
        /// <param name="handler">The interpolated string handler containing the structured template and arguments.</param>
        public static void LogInformation(this ILogger logger,
                                          Exception? exception,
                                          [InterpolatedStringHandlerArgument("logger")] ref LogInformationHandler handler)
        {
            CleanUp(ref handler.Bytes, ref handler.References);
        }

        /// <summary>
        /// Formats and writes a warning log message.
        /// </summary>
        /// <param name="logger">The <see cref="ILogger"/> to write to.</param>
        /// <param name="handler">The interpolated string handler containing the structured template and arguments.</param>
        public static void LogWarning(this ILogger logger,
                                      [InterpolatedStringHandlerArgument("logger")] ref LogWarningHandler handler)
        {
            CleanUp(ref handler.Bytes, ref handler.References);
        }

        /// <summary>
        /// Formats and writes a warning log message with an exception.
        /// </summary>
        /// <param name="logger">The <see cref="ILogger"/> to write to.</param>
        /// <param name="exception">The exception to log.</param>
        /// <param name="handler">The interpolated string handler containing the structured template and arguments.</param>
        public static void LogWarning(this ILogger logger,
                                      Exception? exception,
                                      [InterpolatedStringHandlerArgument("logger")] ref LogWarningHandler handler)
        {
            CleanUp(ref handler.Bytes, ref handler.References);
        }

        /// <summary>
        /// Formats and writes an error log message.
        /// </summary>
        /// <param name="logger">The <see cref="ILogger"/> to write to.</param>
        /// <param name="handler">The interpolated string handler containing the structured template and arguments.</param>
        public static void LogError(this ILogger logger,
                                    [InterpolatedStringHandlerArgument("logger")] ref LogErrorHandler handler)
        {
            CleanUp(ref handler.Bytes, ref handler.References);
        }

        /// <summary>
        /// Formats and writes an error log message with an exception.
        /// </summary>
        /// <param name="logger">The <see cref="ILogger"/> to write to.</param>
        /// <param name="exception">The exception to log.</param>
        /// <param name="handler">The interpolated string handler containing the structured template and arguments.</param>
        public static void LogError(this ILogger logger,
                                    Exception? exception,
                                    [InterpolatedStringHandlerArgument("logger")] ref LogErrorHandler handler)
        {
            CleanUp(ref handler.Bytes, ref handler.References);
        }

        /// <summary>
        /// Formats and writes a critical log message.
        /// </summary>
        /// <param name="logger">The <see cref="ILogger"/> to write to.</param>
        /// <param name="handler">The interpolated string handler containing the structured template and arguments.</param>
        public static void LogCritical(this ILogger logger,
                                       [InterpolatedStringHandlerArgument("logger")] ref LogCriticalHandler handler)
        {
            CleanUp(ref handler.Bytes, ref handler.References);
        }

        /// <summary>
        /// Formats and writes a critical log message with an exception.
        /// </summary>
        /// <param name="logger">The <see cref="ILogger"/> to write to.</param>
        /// <param name="exception">The exception to log.</param>
        /// <param name="handler">The interpolated string handler containing the structured template and arguments.</param>
        public static void LogCritical(this ILogger logger,
                                       Exception? exception,
                                       [InterpolatedStringHandlerArgument("logger")] ref LogCriticalHandler handler)
        {
            CleanUp(ref handler.Bytes, ref handler.References);
        }

        /// <summary>
        /// Formats the message and creates a logical operation scope using advanced interpolated string logging.
        /// </summary>
        /// <remarks>
        /// Properties defined in this scope will be automatically attached to all subsequent log entries 
        /// created within the lifetime of the returned <see cref="IDisposable"/> object.
        /// <br/><br/>
        /// <b>Supported Custom Interpolation Formats:</b>
        /// <list type="bullet">
        /// <item><description><c>{value:@name}</c> : Overrides the structured log property name.</description></item>
        /// <item><description><c>{value:format}</c> : Applies standard C# string formatting (e.g., <c>C2</c>, <c>yyyy-MM-dd</c>).</description></item>
        /// <item><description><c>{value:@name:length}</c> : Overrides the property name and sets alignment/length.</description></item>
        /// <item><description><c>{value:@name:format:length}</c> : Overrides the property name, applies standard format, and alignment.</description></item>
        /// <item><description><c>{value:@name:mask}</c> : Overrides the property name and replaces the entire value with <c>***</c>.</description></item>
        /// <item><description><c>{value:@name:mask:4}</c> : Overrides the property name and masks all but the last 4 characters.</description></item>
        /// <item><description><c>{value:@name:format:mask:4}</c> : Combines property name override, standard formatting, and partial masking.</description></item>
        /// </list>
        /// </remarks>
        /// <param name="logger">The <see cref="ILogger"/> to create the scope in.</param>
        /// <param name="handler">The interpolated string handler containing the structured template and arguments.</param>
        /// <returns>An <see cref="IDisposable"/> that ends the logical operation scope on dispose.</returns>
        public static IDisposable? StartScope(this ILogger logger,
                                              [InterpolatedStringHandlerArgument("logger")] ref StartScopeHandler handler)
        {
            CleanUp(ref handler.Bytes, ref handler.References);
            return null;
        }

        public static void UseSerializationContext<T>(this ILogger logger)
             where T : JsonSerializerContext
        {
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void CleanUp(ref byte[]? bytes, ref object?[]? references)
        {
            if (bytes != null) ArrayPool<byte>.Shared.Return(bytes);
            if (references != null) ArrayPool<object?>.Shared.Return(references);
        }
    }
}
