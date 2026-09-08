using System.Text;

namespace KetPsi.Extensions.Logging.Generator;

public partial class ZeroAllocLoggerGenerator
{
    private sealed class LogCallInfo
    {
        public string ClassName { get; set; } = string.Empty;
        public string MethodName { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public int Line { get; set; }
        public int Character { get; set; }
        public string LogLevel { get; set; } = string.Empty;
        public bool HasException { get; set; }
        public List<LogPart> Parts { get; } = [];
        public StringBuilder OriginalFormatBuilder { get; set; } = new();
        public bool IsScope { get; set; } = false;
    }
}