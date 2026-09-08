namespace KetPsi.Extensions.Logging.Generator;

public partial class ZeroAllocLoggerGenerator
{
    private sealed class LogPart
    {
        public bool IsLiteral { get; set; }
        public string Text { get; set; } = string.Empty;
        public string Expression { get; set; } = string.Empty;
        public string TypeName { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? Format { get; set; }
        public int Index { get; set; }
    }
}