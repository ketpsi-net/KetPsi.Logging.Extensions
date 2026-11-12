using System;
using System.Buffers;
using System.Text;

using KetPsi.Extensions.Logging.Console.Internals.Formatters;

using Microsoft.Extensions.Logging;

namespace Console.Formatters;

public class ExceptionFormattingTests
{
    private static Exception Thrown(string message)
    {
        try { throw new InvalidOperationException(message, new ArgumentException("inner-cause")); }
        catch (Exception ex) { return ex; }
    }

    private static string Render(Action<IBufferWriter<byte>> write)
    {
        var buffer = new ArrayBufferWriter<byte>(64);
        write(buffer);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    [Fact]
    public void Systemd_WritesExceptionOnTheSameLine()
    {
        var ex = Thrown("boom-message");

        string line = Render(b => new SystemdConsoleFormatter().Format(
            b, "Cat", LogLevel.Error, default, "failed order 7", ex, static (s, _) => s));

        Assert.StartsWith("<3>Cat: failed order 7 System.InvalidOperationException: boom-message", line);
        Assert.Contains("inner-cause", line);
        Assert.Contains(" at ", line);
        // One journald entry: the only line break is the terminator.
        Assert.Equal(line.Length - 1, line.IndexOfAny(['\r', '\n']));
        Assert.EndsWith("\n", line);
    }

    [Fact]
    public void Systemd_WithoutException_IsUnchanged()
    {
        string line = Render(b => new SystemdConsoleFormatter().Format(
            b, "Cat", LogLevel.Information, default, "hello", null, static (s, _) => s));

        Assert.Equal("<6>Cat: hello\n", line);
    }

    [Fact]
    public void Write_MatchesExceptionToString()
    {
        var ex = Thrown("boom-message");

        Assert.Equal(ex.ToString(), Render(b => ExceptionWriter.Write(b, ex)));
    }

    [Fact]
    public void WriteSingleLine_ReplacesEachLineBreakWithOneSpace()
    {
        var ex = new InvalidOperationException("a\r\nb\nc\rd");

        Assert.Equal("System.InvalidOperationException: a b c d", Render(b => ExceptionWriter.WriteSingleLine(b, ex)));
    }

    [Fact]
    public void Write_LongNonAsciiText_TranscodesAcrossChunks()
    {
        // Longer than one 1024-char chunk, with surrogate pairs that straddle chunk boundaries.
        string message = string.Concat(System.Linq.Enumerable.Repeat("ab😀é", 700));
        var ex = new InvalidOperationException(message);

        Assert.Equal(ex.ToString(), Render(b => ExceptionWriter.Write(b, ex)));
    }
}
