namespace KetPsi.Extensions.Logging.Abstractions;

public interface IStructuredLogState
{
    ReadOnlySpan<char> OriginalFormat { get; }
    void WriteParameters<TWriter>(ref TWriter writer) where TWriter : struct, ILogParameterWriter;
}
