using System.Text.Json.Serialization;

namespace KetPsi.Extensions.Logging.Console
{
    public interface IZeroAllocConsoleLoggerConfig
    {
        IZeroAllocConsoleLoggerConfig UseSerializationContext<T>(T jsonSerializationContext) where T : JsonSerializerContext;
    }
}
