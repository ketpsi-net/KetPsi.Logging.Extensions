namespace KetPsi.Extensions.Logging.Generator;

public partial class ZeroAllocLoggerGenerator
{
    private enum LogPartKind
    {
        Value,
        Json,
        JsonIndexed,
        Mask,
    }

    private sealed class LogPart
    {
        public bool IsLiteral { get; set; }
        public string Text { get; set; } = string.Empty;

        /// <summary>Fully qualified type name, or <c>object</c> for reference types generated code cannot name.</summary>
        public string TypeName { get; set; } = string.Empty;
        /// <summary>True when <see cref="TypeName"/> is the type's own name (so it can be used with a serializer context).</summary>
        public bool IsTypeNameExact { get; set; }
        public string Name { get; set; } = string.Empty;
        public LogPartKind Kind { get; set; }
        /// <summary>Standard .NET format string, applied before masking for <see cref="LogPartKind.Mask"/>.</summary>
        public string? Format { get; set; }
        public string MaskStart { get; set; } = "0";
        public string MaskEnd { get; set; } = "^4";
        public int Alignment { get; set; }
        public int Index { get; set; }

        /// <summary><c>LogValueFormatter.Register*</c> calls (without the class prefix) needed for this part's value type.</summary>
        public List<string> Registrations { get; } = [];
    }
}
