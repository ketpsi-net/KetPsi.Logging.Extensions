using System.Collections.Immutable;

using Microsoft.CodeAnalysis;

namespace KetPsi.Extensions.Logging.Generator;

public partial class ZeroAllocLoggerGenerator
{
    private sealed class LogCallInfo
    {
        /// <summary>Fully qualified name of the type containing the call; calls are grouped into one file per type.</summary>
        public string ContainingTypeKey { get; set; } = string.Empty;
        public string InterceptsLocationAttribute { get; set; } = string.Empty;
        public string LogLevel { get; set; } = string.Empty;
        public string HandlerTypeName { get; set; } = string.Empty;
        public bool HasEventId { get; set; }
        public bool HasException { get; set; }
        public bool IsScope { get; set; }
        public string OriginalFormat { get; set; } = string.Empty;
        public List<LogPart> Parts { get; } = [];
    }

    private sealed class LogCallResult(LogCallInfo? call, ImmutableArray<Diagnostic> diagnostics)
    {
        public LogCallInfo? Call { get; } = call;
        public ImmutableArray<Diagnostic> Diagnostics { get; } = diagnostics;
    }
}
