namespace KetPsi.Extensions.Logging.Abstractions;

public interface ILogParameterWriter
{
    // The sink decides what to do with the Name and the strongly-typed Value
    void Write<T>(in LogParameter<T> parameter);
}