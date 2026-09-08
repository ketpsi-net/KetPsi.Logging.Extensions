using Microsoft.Extensions.Logging.Console;

namespace KetPsi.Extensions.Logging.Console.Internals.ZeroAllocLogger
{
    internal class ZeroAllocConsoleFormatterOptions : SimpleConsoleFormatterOptions
    {
        public bool IncludeCategory { get; set; } = false;
        public bool IncludeEventId { get; set; } = false;

    }
}
