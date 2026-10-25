using Microsoft.Extensions.Logging;

// Two classes with the same simple name: their generated interceptor files must not collide.
namespace Generator.NsA
{
    internal static class Duplicate
    {
        public static void Log(ILogger logger) => logger.LogInformation($"from {"A":@Source}");
    }
}

namespace Generator.NsB
{
    internal static class Duplicate
    {
        public static void Log(ILogger logger) => logger.LogInformation($"from {"B":@Source}");
    }
}
