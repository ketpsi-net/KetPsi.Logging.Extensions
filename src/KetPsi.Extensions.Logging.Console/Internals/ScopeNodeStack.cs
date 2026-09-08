namespace KetPsi.Extensions.Logging.Console.Internals;

[System.Runtime.CompilerServices.InlineArray(16)]
internal struct ScopeNodeStack
{
    // The compiler replicates this field 16 times in memory on the stack
    private ScopeNode? _element0;
}
