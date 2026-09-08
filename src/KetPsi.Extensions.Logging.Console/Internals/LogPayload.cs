namespace KetPsi.Extensions.Logging.Console.Internals;

internal readonly record struct LogPayload(byte[] RentedBuffer, int Length, bool IsErrorStream);
