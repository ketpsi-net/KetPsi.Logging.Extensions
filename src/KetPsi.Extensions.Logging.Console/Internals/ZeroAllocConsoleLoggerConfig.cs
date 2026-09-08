using System.Text.Json.Serialization;

using KetPsi.Extensions.Logging.Abstractions;

namespace KetPsi.Extensions.Logging.Console.Internals
{
    internal class ZeroAllocConsoleLoggerConfig() : IZeroAllocConsoleLoggerConfig
    {
        public IZeroAllocConsoleLoggerConfig UseSerializationContext<T>(T jsonSerializationContext) where T : JsonSerializerContext
        {
            JsonLogValueSerializationContext.Context = jsonSerializationContext;
            return this;
        }
    }
}
