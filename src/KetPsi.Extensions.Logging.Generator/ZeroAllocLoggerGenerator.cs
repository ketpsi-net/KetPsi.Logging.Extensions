using System.Collections.Immutable;
using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace KetPsi.Extensions.Logging.Generator;

[Generator]
public partial class ZeroAllocLoggerGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var logCallPipeline = context.SyntaxProvider.CreateSyntaxProvider(
            predicate: static (node, _) =>
                node is InvocationExpressionSyntax invocation &&
                invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
                (memberAccess.Name.Identifier.Text.StartsWith("Log") || memberAccess.Name.Identifier.Text == "StartScope"),
            transform: static (ctx, _) => GetLogCallInfo(ctx))
            .Where(static x => x is not null);

        var jsonContextPipeline = context.SyntaxProvider.CreateSyntaxProvider(
            predicate: static (node, _) =>
                node is GenericNameSyntax genericName && genericName.Identifier.Text == "UseSerializationContext",
            transform: static (ctx, _) =>
            {
                var genericName = (GenericNameSyntax)ctx.Node;
                var typeArg = genericName.TypeArgumentList.Arguments.FirstOrDefault();
                if (typeArg == null) return null;
                var symbol = ctx.SemanticModel.GetSymbolInfo(typeArg).Symbol as ITypeSymbol;
                return symbol?.ToDisplayString();
            })
            .Where(static x => x is not null);

        var combined = logCallPipeline.Collect().Combine(jsonContextPipeline.Collect());

        context.RegisterSourceOutput(combined, static (ctx, source) =>
            EmitInterceptors(ctx, source.Left, source.Right));
    }

    private static LogCallInfo? GetLogCallInfo(GeneratorSyntaxContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        var memberAccess = (MemberAccessExpressionSyntax)invocation.Expression;
        var arg = invocation.ArgumentList.Arguments.FirstOrDefault(a => a.Expression is InterpolatedStringExpressionSyntax);
        if (arg?.Expression is not InterpolatedStringExpressionSyntax interpolatedString) return null;

        var semanticModel = context.SemanticModel;
        if (semanticModel.GetSymbolInfo(invocation).Symbol is not IMethodSymbol) return null;

        // Extract the Class Name for file grouping
        var typeDecl = invocation.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        string className = typeDecl?.Identifier.Text ?? "GlobalLogs";

        var callInfo = new LogCallInfo
        {
            ClassName = className,
            MethodName = memberAccess.Name.Identifier.Text,
            FilePath = invocation.SyntaxTree.FilePath,
            Line = memberAccess.Name.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            Character = memberAccess.Name.GetLocation().GetLineSpan().StartLinePosition.Character + 1,
            LogLevel = GetLogLevel(memberAccess.Name.Identifier.Text),
            HasException = invocation.ArgumentList.Arguments.IndexOf(arg) > 0,
            OriginalFormatBuilder = new StringBuilder(),
            IsScope = memberAccess.Name.Identifier.Text == "StartScope",
        };

        int argIndex = 0;
        foreach (var content in interpolatedString.Contents)
        {
            if (content is InterpolatedStringTextSyntax textSyntax)
            {
                callInfo.Parts.Add(new LogPart { IsLiteral = true, Text = textSyntax.TextToken.ValueText });
                callInfo.OriginalFormatBuilder.Append(textSyntax.TextToken.ValueText);
            }
            else if (content is InterpolationSyntax interpolation)
            {
                var typeInfo = semanticModel.GetTypeInfo(interpolation.Expression);
                string name = $"arg{argIndex}";
                string? format = null;

                if (interpolation.FormatClause != null)
                {
                    string rawFormat = interpolation.FormatClause.FormatStringToken.Text;
                    if (rawFormat.StartsWith("@"))
                    {
                        var split = rawFormat.Split(new[] { ':' }, 2);
                        name = split[0].Substring(1);
                        if (split.Length > 1) format = split[1];
                    }
                    else
                    {
                        format = rawFormat;
                        name = interpolation.Expression.ToString().Split('.').Last();
                    }
                }

                callInfo.Parts.Add(new LogPart
                {
                    IsLiteral = false,
                    Expression = interpolation.Expression.ToString(),
                    TypeName = typeInfo.Type?.ToDisplayString() ?? "object",
                    Name = name,
                    Format = format,
                    Index = argIndex
                });

                callInfo.OriginalFormatBuilder.Append($"{{{name}}}");
                argIndex++;
            }
        }
        return callInfo;
    }

    private static void EmitInterceptors(SourceProductionContext context, ImmutableArray<LogCallInfo?> logCalls, ImmutableArray<string> jsonContexts)
    {
        if (logCalls.IsDefaultOrEmpty) return;

        // 1. Emit the Interceptor Attribute ONCE independently to prevent duplication errors
        string attributeCode = @"// <auto-generated/>
namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    sealed class InterceptsLocationAttribute : Attribute
    {
        public InterceptsLocationAttribute(string filePath, int line, int character) { }
    }
}";
        context.AddSource("InterceptsLocationAttribute.g.cs", SourceText.From(attributeCode, Encoding.UTF8));

        // 2. Group by ClassName to emit one file per class
        var groups = logCalls.Where(c => c is not null).Cast<LogCallInfo>().GroupBy(c => c.ClassName);
        string? resolvedJsonContext = jsonContexts.FirstOrDefault();
        int structId = 1;

        foreach (var group in groups)
        {
            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated/>");
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Buffers;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using Microsoft.Extensions.Logging;");
            sb.AppendLine("using KetPsi.Extensions.Logging;");
            sb.AppendLine("using KetPsi.Extensions.Logging.Abstractions;");
            sb.AppendLine();
            sb.AppendLine("namespace KetPsi.Extensions.Logging");
            sb.AppendLine("{");
            sb.AppendLine($"    public static class {group.Key}_LogInterceptors");
            sb.AppendLine("    {");

            foreach (var call in group)
            {
                EmitStructAndInterceptor(sb, call, structId, resolvedJsonContext);
                structId++;
            }

            sb.AppendLine("    }");
            sb.AppendLine("}");

            context.AddSource($"{group.Key}_LogInterceptors.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
        }
    }

    private static void EmitStructAndInterceptor(StringBuilder sb, LogCallInfo call, int id, string? jsonContext)
    {
        var args = call.Parts.Where(p => !p.IsLiteral).ToList();
        string structName = $"LogState_{id}";

        // 1. GENERATE STRUCT
        sb.AppendLine($"        internal readonly struct {structName} : IDeferredUtf8Formatter, IStructuredLogState");
        sb.AppendLine("        {");

        foreach (var arg in args) sb.AppendLine($"            private readonly LogParameter<{GetWrapperType(arg)}> _arg{arg.Index};");

        sb.Append($"            public {structName}(");
        sb.Append(string.Join(", ", args.Select(a => $"LogParameter<{GetWrapperType(a)}> arg{a.Index}")));
        sb.AppendLine(")");
        sb.AppendLine("            {");
        foreach (var arg in args) sb.AppendLine($"                _arg{arg.Index} = arg{arg.Index};");
        sb.AppendLine("            }");
        sb.AppendLine();

        // --> NEW: Expose the message template natively
        sb.AppendLine($"            public ReadOnlySpan<char> OriginalFormat => \"{call.OriginalFormatBuilder}\";");
        sb.AppendLine();

        sb.AppendLine("            public void FormatTo(IBufferWriter<byte> writer)");
        sb.AppendLine("            {");
        foreach (var part in call.Parts)
        {
            if (part.IsLiteral) sb.AppendLine($"                writer.Write(\"{part.Text}\"u8);");
            else sb.AppendLine($"                _arg{part.Index}.FormatTo(writer);");
        }
        sb.AppendLine("            }");
        sb.AppendLine();

        sb.AppendLine("            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
        sb.AppendLine("            public void WriteParameters<TWriter>(ref TWriter writer) where TWriter : struct, ILogParameterWriter");
        sb.AppendLine("            {");
        foreach (var arg in args)
        {
            sb.AppendLine($"                writer.Write(in _arg{arg.Index});");
        }
        sb.AppendLine("            }");
        sb.AppendLine();

        sb.AppendLine("            public override string ToString() => \"" + call.OriginalFormatBuilder.ToString() + "\";");

        sb.AppendLine("        }");
        sb.AppendLine();

        // 2. GENERATE INTERCEPTOR
        string exceptionParam = call.HasException ? "System.Exception? exception, " : "";
        string exceptionArg = call.HasException ? "exception" : "null";

        // Support for StartScope
        string returnType = call.IsScope ? "System.IDisposable?" : "void";
        string actualHandlerName = call.IsScope ? "StartScopeHandler" : $"Log{call.LogLevel}Handler";

        sb.AppendLine($"        [System.Runtime.CompilerServices.InterceptsLocation(@\"{call.FilePath}\", {call.Line}, {call.Character})]");
        sb.AppendLine($"        public static {returnType} Intercept_{id}(this ILogger logger, {exceptionParam}ref {actualHandlerName} handler)");
        sb.AppendLine("        {");

        // Fast exit & Safety Cleanup for disabled logs
        if (call.IsScope)
        {
            sb.AppendLine("            if (handler.Bytes == null && handler.References == null) return null;");
        }
        else
        {
            sb.AppendLine($"            if (!logger.IsEnabled(LogLevel.{call.LogLevel}))");
            sb.AppendLine("            {");
            sb.AppendLine("                if (handler.Bytes != null) System.Buffers.ArrayPool<byte>.Shared.Return(handler.Bytes);");
            sb.AppendLine("                if (handler.References != null) System.Buffers.ArrayPool<object?>.Shared.Return(handler.References, clearArray: true);");
            sb.AppendLine("                return;");
            sb.AppendLine("            }");
        }

        sb.AppendLine("            int __byteOffset = 0;");
        sb.AppendLine("            int __refOffset = 0;");

        var paramList = new List<string>();
        foreach (var arg in args)
        {
            sb.AppendLine($"            {arg.TypeName} __{arg.Name};");
            sb.AppendLine($"            if (System.Runtime.CompilerServices.RuntimeHelpers.IsReferenceOrContainsReferences<{arg.TypeName}>())");
            sb.AppendLine("            {");
            sb.AppendLine($"                __{arg.Name} = ({arg.TypeName})handler.References![__refOffset++];");
            sb.AppendLine("            }");
            sb.AppendLine("            else");
            sb.AppendLine("            {");
            sb.AppendLine($"                __{arg.Name} = System.Runtime.CompilerServices.Unsafe.ReadUnaligned<{arg.TypeName}>(ref handler.Bytes![__byteOffset]);");
            sb.AppendLine($"                __byteOffset += System.Runtime.CompilerServices.Unsafe.SizeOf<{arg.TypeName}>();");
            sb.AppendLine("            }");

            string valueExpr = $"__{arg.Name}";
            string formatArg = arg.Format != null ? $"\"{arg.Format}\"" : "null";

            if (arg.Format == "json")
            {
                if (jsonContext != null) valueExpr = $"new JsonLogValue<{arg.TypeName}>({valueExpr}, (System.Text.Json.Serialization.Metadata.JsonTypeInfo<{arg.TypeName}>){jsonContext}.Default.GetTypeInfo(typeof({arg.TypeName})))";
                else valueExpr = $"new JsonLogValue<{arg.TypeName}>({valueExpr}, null)";
                formatArg = "null";
            }
            else if (arg.Format != null && arg.Format.StartsWith("mask"))
            {
                string startIdx = "0";
                string endIdx = "^4";

                var maskParts = arg.Format.Split(':');
                if (maskParts.Length >= 2)
                {
                    var rangeParts = maskParts[1].Split([".."], StringSplitOptions.None);
                    if (rangeParts.Length == 2)
                    {
                        startIdx = string.IsNullOrWhiteSpace(rangeParts[0]) ? "0" : rangeParts[0];
                        endIdx = string.IsNullOrWhiteSpace(rangeParts[1]) ? "^0" : rangeParts[1];
                    }
                }
                valueExpr = $"new MaskedSpanFormattable<{arg.TypeName}>({valueExpr}, {startIdx}, {endIdx})";
                formatArg = "null";
            }

            paramList.Add($"new LogParameter<{GetWrapperType(arg)}>(\"{arg.Name}\", {valueExpr}, {formatArg})");
        }

        string structInstantiation = $"new {structName}({string.Join(", ", paramList)})";

        if (call.IsScope)
        {
            sb.AppendLine($"            System.IDisposable? __scope = logger.BeginScope({structInstantiation});");

            sb.AppendLine("            if (handler.Bytes != null) System.Buffers.ArrayPool<byte>.Shared.Return(handler.Bytes);");
            sb.AppendLine("            if (handler.References != null) System.Buffers.ArrayPool<object?>.Shared.Return(handler.References, clearArray: true);");

            sb.AppendLine("            return __scope;");
        }
        else
        {
            string formatTemplate = call.OriginalFormatBuilder.ToString();
            sb.AppendLine($"            logger.Log(LogLevel.{call.LogLevel}, 0, {structInstantiation}, {exceptionArg}, static (s, _) => \"{formatTemplate}\");");

            sb.AppendLine("            if (handler.Bytes != null) System.Buffers.ArrayPool<byte>.Shared.Return(handler.Bytes);");
            sb.AppendLine("            if (handler.References != null) System.Buffers.ArrayPool<object?>.Shared.Return(handler.References, clearArray: true);");
        }

        sb.AppendLine("        }");
        sb.AppendLine();
    }

    private static string GetWrapperType(LogPart arg)
    {
        if (arg.Format == "json") return $"JsonLogValue<{arg.TypeName}>";
        if (arg.Format != null && arg.Format.StartsWith("mask")) return $"MaskedSpanFormattable<{arg.TypeName}>";
        return arg.TypeName;
    }

    private static string GetLogLevel(string methodName) => methodName switch
    {
        "LogTrace" => "Trace",
        "LogDebug" => "Debug",
        "LogWarning" => "Warning",
        "LogError" => "Error",
        "LogCritical" => "Critical",
        _ => "Information"
    };
}