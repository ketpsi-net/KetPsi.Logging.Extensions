using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace KetPsi.Extensions.Logging.Generator;

[Generator]
public partial class ZeroAllocLoggerGenerator : IIncrementalGenerator
{
    private const string AbstractionsNs = "global::KetPsi.Extensions.Logging.Abstractions";
    private const string LoggingNs = "global::Microsoft.Extensions.Logging";
    private const string AbstractionsAssembly = "KetPsi.Extensions.Logging.Abstractions";

    private static readonly Regex MaskBound = new(@"^\^?\d+$", RegexOptions.CultureInvariant);

#pragma warning disable RS2008 // Analyzer release tracking
    private static readonly DiagnosticDescriptor InaccessibleValueType = new(
        id: "KPL001",
        title: "Log argument type is not accessible to generated code",
        messageFormat: "KetPsi cannot intercept this log call: the type '{0}' of '{1}' is not accessible from generated code. Make it internal or public, or log one of its members instead.",
        category: "KetPsi.Logging",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidMaskRange = new(
        id: "KPL002",
        title: "Invalid mask range",
        messageFormat: "Mask range '{0}' is not valid. Use 'mask', 'mask:N', or 'mask:start..end' with integer or ^integer bounds. Masking all but the last 4 characters instead.",
        category: "KetPsi.Logging",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor NotInterceptable = new(
        id: "KPL003",
        title: "Log call cannot be intercepted",
        messageFormat: "KetPsi cannot intercept this log call ({0}), so it would log nothing at runtime",
        category: "KetPsi.Logging",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
#pragma warning restore RS2008

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var logCallPipeline = context.SyntaxProvider.CreateSyntaxProvider(
            predicate: static (node, _) =>
                node is InvocationExpressionSyntax { ArgumentList.Arguments.Count: > 0 } invocation &&
                invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
                (memberAccess.Name.Identifier.ValueText.StartsWith("Log", StringComparison.Ordinal) ||
                 memberAccess.Name.Identifier.ValueText == "StartScope"),
            transform: static (ctx, ct) => GetLogCall(ctx, ct))
            .Where(static x => x is not null)
            .Select(static (x, _) => x!);

        // Both `logger.UseSerializationContext<TContext>()` and
        // `config.UseSerializationContext(TContext.Default)` (type argument inferred) are recognized.
        var jsonContextPipeline = context.SyntaxProvider.CreateSyntaxProvider(
            predicate: static (node, _) =>
                node is InvocationExpressionSyntax invocation &&
                GetInvokedName(invocation) == "UseSerializationContext",
            transform: static (ctx, ct) =>
            {
                if (ctx.SemanticModel.GetSymbolInfo(ctx.Node, ct).Symbol is not IMethodSymbol { TypeArguments.Length: 1 } method)
                    return null;
                var contextType = method.TypeArguments[0];
                return IsNameable(contextType, ctx.SemanticModel.Compilation)
                    ? contextType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                    : null;
            })
            .Where(static x => x is not null)
            .Select(static (x, _) => x!);

        var combined = logCallPipeline.Collect().Combine(jsonContextPipeline.Collect());

        context.RegisterSourceOutput(combined, static (ctx, source) =>
            Emit(ctx, source.Left, source.Right));
    }

    private static string? GetInvokedName(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
        SimpleNameSyntax s => s.Identifier.ValueText,
        _ => null,
    };

    private static LogCallResult? GetLogCall(GeneratorSyntaxContext context, CancellationToken ct)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        var semanticModel = context.SemanticModel;

        // Only intercept KetPsi's own handler overloads; user methods named Log* and MEL overloads are left alone.
        if (semanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol method) return null;
        var definition = method.ReducedFrom ?? method;
        if (definition.ContainingAssembly?.Name != AbstractionsAssembly ||
            definition.ContainingType?.Name != "LoggerExtensions")
            return null;

        var handlerParameter = definition.Parameters.LastOrDefault();
        if (handlerParameter is not { RefKind: RefKind.Ref }) return null;

        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();

        // The handler is always the last parameter; C# also binds `$"a" + $"b"` concatenations to it.
        var contents = new List<InterpolatedStringContentSyntax>();
        var handlerArgument = invocation.ArgumentList.Arguments.LastOrDefault(a => a.NameColon is null || a.NameColon.Name.Identifier.ValueText == handlerParameter.Name);
        if (handlerArgument is null || !TryCollectContents(handlerArgument.Expression, contents))
        {
            diagnostics.Add(Diagnostic.Create(NotInterceptable, invocation.GetLocation(),
                "pass an interpolated string (or a '+' concatenation of interpolated strings) directly"));
            return new LogCallResult(null, diagnostics.ToImmutable());
        }

#pragma warning disable RSEXPERIMENTAL002 // InterceptableLocation API
        var location = semanticModel.GetInterceptableLocation(invocation, ct);
#pragma warning restore RSEXPERIMENTAL002
        if (location is null)
        {
            diagnostics.Add(Diagnostic.Create(NotInterceptable, invocation.GetLocation(), "the call site is not interceptable"));
            return new LogCallResult(null, diagnostics.ToImmutable());
        }

        var callInfo = new LogCallInfo
        {
            ContainingTypeKey = GetContainingTypeKey(invocation, semanticModel, ct),
            InterceptsLocationAttribute = location.GetInterceptsLocationAttributeSyntax(),
            LogLevel = GetLogLevel(definition.Name),
            HandlerTypeName = handlerParameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            HasEventId = definition.Parameters.Any(p => IsType(p.Type, "Microsoft.Extensions.Logging", "EventId")),
            HasException = definition.Parameters.Any(p => IsType(p.Type, "System", "Exception")),
            IsScope = definition.Name == "StartScope",
        };

        var compilation = semanticModel.Compilation;
        var template = new StringBuilder();
        bool canIntercept = true;
        int argIndex = 0;

        foreach (var content in contents)
        {
            if (content is InterpolatedStringTextSyntax textSyntax)
            {
                string text = GetLiteralText(textSyntax, semanticModel, ct);
                callInfo.Parts.Add(new LogPart { IsLiteral = true, Text = text });
                // Literal braces must stay escaped in a message template.
                template.Append(text.Replace("{", "{{").Replace("}", "}}"));
                continue;
            }

            if (content is not InterpolationSyntax interpolation) continue;

            var type = semanticModel.GetTypeInfo(interpolation.Expression, ct).Type;
            var part = new LogPart { Index = argIndex };

            if (type is null || type is IErrorTypeSymbol)
            {
                canIntercept = false;
            }
            else if (IsNameable(type, compilation))
            {
                part.TypeName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                part.IsTypeNameExact = true;
            }
            else if (type.IsReferenceType)
            {
                // The handler already stores reference types as object, so reading them back as object is exact.
                part.TypeName = "object";
            }
            else
            {
                canIntercept = false;
                diagnostics.Add(Diagnostic.Create(InaccessibleValueType, interpolation.Expression.GetLocation(),
                    type.ToDisplayString(), interpolation.Expression.ToString()));
            }

            if (interpolation.AlignmentClause is { } alignmentClause &&
                semanticModel.GetConstantValue(alignmentClause.Value, ct) is { HasValue: true, Value: int alignment })
            {
                part.Alignment = alignment;
            }

            ParseFormat(interpolation, part, diagnostics);

            if (part.IsTypeNameExact && type is not null)
            {
                AddValueRegistrations(type, part.Registrations);
            }

            callInfo.Parts.Add(part);
            template.Append('{').Append(part.Name).Append('}');
            argIndex++;
        }

        callInfo.OriginalFormat = template.ToString();
        return new LogCallResult(canIntercept ? callInfo : null, diagnostics.ToImmutable());
    }

    /// <summary>
    /// The literal's runtime text. The token's ValueText keeps escaped braces doubled ("{{"), so the compiler's
    /// constant value is used instead; it is also correct for raw and verbatim interpolated strings.
    /// </summary>
    private static string GetLiteralText(InterpolatedStringTextSyntax textSyntax, SemanticModel semanticModel, CancellationToken ct)
    {
        var constant = semanticModel.GetOperation(textSyntax, ct) switch
        {
            IInterpolatedStringTextOperation text => text.Text.ConstantValue,
            IInterpolatedStringAppendOperation { AppendCall: IInvocationOperation { Arguments.Length: > 0 } call } => call.Arguments[0].Value.ConstantValue,
            _ => default,
        };
        if (constant.HasValue && constant.Value is string value) return value;

        return textSyntax.TextToken.ValueText.Replace("{{", "{").Replace("}}", "}");
    }

    /// <summary>
    /// Flattens an interpolated string, or a '+' chain of interpolated strings, into its parts in evaluation order
    /// (the order in which the handler receives AppendLiteral/AppendFormatted calls).
    /// </summary>
    private static bool TryCollectContents(ExpressionSyntax expression, List<InterpolatedStringContentSyntax> contents)
    {
        switch (Unparenthesize(expression))
        {
            case InterpolatedStringExpressionSyntax interpolated:
                contents.AddRange(interpolated.Contents);
                return true;
            case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AddExpression):
                return TryCollectContents(binary.Left, contents) && TryCollectContents(binary.Right, contents);
            default:
                return false;
        }
    }

    private static ExpressionSyntax Unparenthesize(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized) expression = parenthesized.Expression;
        return expression;
    }

    private static bool IsType(ITypeSymbol type, string ns, string name)
        => type.Name == name && type.ContainingNamespace?.ToDisplayString() == ns;

    private static string GetContainingTypeKey(InvocationExpressionSyntax invocation, SemanticModel semanticModel, CancellationToken ct)
    {
        var typeDecl = invocation.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (typeDecl is null) return "GlobalStatements";
        var symbol = semanticModel.GetDeclaredSymbol(typeDecl, ct);
        return symbol?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) ?? typeDecl.Identifier.ValueText;
    }

    /// <summary>
    /// Whether generated code (top level of the same assembly) can spell this type's name.
    /// Private/protected nested types, file-local types, anonymous types and type parameters cannot be named.
    /// </summary>
    private static bool IsNameable(ITypeSymbol type, Compilation compilation)
    {
        switch (type)
        {
            case IArrayTypeSymbol array:
                return IsNameable(array.ElementType, compilation);
            case INamedTypeSymbol named:
                if (named.IsAnonymousType || named.IsFileLocal) return false;
                for (var current = named; current is not null; current = current.ContainingType)
                {
                    if (!compilation.IsSymbolAccessibleWithin(current.OriginalDefinition, compilation.Assembly)) return false;
                }
                return named.TypeArguments.All(t => IsNameable(t, compilation));
            default:
                // Type parameters, pointers, dynamic and error types.
                return false;
        }
    }

    private static void ParseFormat(InterpolationSyntax interpolation, LogPart part, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        string? name = null;
        string rest = interpolation.FormatClause?.FormatStringToken.ValueText ?? string.Empty;

        if (rest.StartsWith("@", StringComparison.Ordinal))
        {
            int separator = rest.IndexOf(':');
            name = (separator < 0 ? rest.Substring(1) : rest.Substring(1, separator - 1)).Trim();
            rest = separator < 0 ? string.Empty : rest.Substring(separator + 1);
        }

        part.Name = string.IsNullOrEmpty(name) ? DeriveName(interpolation.Expression, part.Index) : name!;

        if (rest == "json")
        {
            part.Kind = LogPartKind.Json;
            return;
        }
        if (rest == "json:indexed")
        {
            part.Kind = LogPartKind.JsonIndexed;
            return;
        }

        // A standard format may itself contain ':' (e.g. HH:mm:ss), so only a whole "mask" segment starts masking.
        var segments = rest.Split(':');
        int maskIndex = Array.IndexOf(segments, "mask");
        if (maskIndex < 0)
        {
            part.Format = rest.Length == 0 ? null : rest;
            return;
        }

        part.Kind = LogPartKind.Mask;
        string format = string.Join(":", segments, 0, maskIndex);
        part.Format = format.Length == 0 ? null : format;

        if (maskIndex == segments.Length - 1) return; // plain "mask": keep defaults 0..^4

        string range = string.Join(":", segments, maskIndex + 1, segments.Length - maskIndex - 1);
        int dots = range.IndexOf("..", StringComparison.Ordinal);
        if (dots < 0)
        {
            // mask:N keeps the last N characters visible.
            if (MaskBound.IsMatch(range) && range[0] != '^')
            {
                part.MaskEnd = "^" + range;
                return;
            }
        }
        else
        {
            string start = range.Substring(0, dots).Trim();
            string end = range.Substring(dots + 2).Trim();
            start = start.Length == 0 ? "0" : start;
            end = end.Length == 0 ? "^0" : end;
            if (MaskBound.IsMatch(start) && MaskBound.IsMatch(end))
            {
                part.MaskStart = start;
                part.MaskEnd = end;
                return;
            }
        }

        diagnostics.Add(Diagnostic.Create(InvalidMaskRange, interpolation.FormatClause!.GetLocation(), range));
    }

    private static string DeriveName(ExpressionSyntax expression, int index) => Unparenthesize(expression) switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.ValueText,
        ConditionalAccessExpressionSyntax { WhenNotNull: MemberBindingExpressionSyntax binding } => binding.Name.Identifier.ValueText,
        _ => $"arg{index}",
    };

    /// <summary>
    /// Picks the constrained LogValueFormatter registration for a value type so sinks can format it without boxing
    /// or reflection. Reference types need none: interface checks on them do not allocate.
    /// </summary>
    private static void AddValueRegistrations(ITypeSymbol type, List<string> registrations)
    {
        if (!type.IsValueType) return;
        string typeName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
        {
            var underlying = nullable.TypeArguments[0];
            registrations.Add($"RegisterNullable<{underlying.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}>()");
            AddValueRegistrations(underlying, registrations);
            return;
        }

        bool utf8 = false, span = false, deferred = false;
        foreach (var iface in type.AllInterfaces)
        {
            if (IsType(iface, "System", "IUtf8SpanFormattable")) utf8 = true;
            else if (IsType(iface, "System", "ISpanFormattable")) span = true;
            else if (IsType(iface, "KetPsi.Extensions.Logging.Abstractions", "IDeferredUtf8Formatter")) deferred = true;
        }

        if (deferred && span) registrations.Add($"RegisterDeferred<{typeName}>()");
        else if (utf8 && span) registrations.Add($"RegisterFormattable<{typeName}>()");
        else if (span) registrations.Add($"RegisterSpanFormattable<{typeName}>()");
        else if (utf8) registrations.Add($"RegisterUtf8Formattable<{typeName}>()");
    }

    private static void Emit(SourceProductionContext context, ImmutableArray<LogCallResult> results, ImmutableArray<string> jsonContexts)
    {
        foreach (var result in results)
        {
            foreach (var diagnostic in result.Diagnostics) context.ReportDiagnostic(diagnostic);
        }

        var calls = results.Select(r => r.Call).Where(c => c is not null).Cast<LogCallInfo>().ToList();
        if (calls.Count == 0) return;

        string? jsonContext = jsonContexts.FirstOrDefault();
        var usedClassNames = new HashSet<string>(StringComparer.Ordinal);
        int id = 1;

        foreach (var group in calls.GroupBy(c => c.ContainingTypeKey, StringComparer.Ordinal))
        {
            // Class (and hint) names come from the fully qualified containing type, so equal simple names
            // in different namespaces or nesting levels no longer collide.
            string baseName = Sanitize(group.Key) + "_LogInterceptors";
            string className = baseName;
            for (int suffix = 2; !usedClassNames.Add(className); suffix++) className = $"{baseName}{suffix}";

            var sb = new StringBuilder();
            EmitFile(sb, className, group.ToList(), ref id, jsonContext);
            context.AddSource($"{className}.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
        }
    }

    private static string Sanitize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name) sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        return sb.ToString();
    }

    private static void EmitFile(StringBuilder sb, string className, List<LogCallInfo> calls, ref int id, string? jsonContext)
    {
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable disable");
        sb.AppendLine();
        sb.AppendLine("namespace System.Runtime.CompilerServices");
        sb.AppendLine("{");
        sb.AppendLine("    [global::System.AttributeUsage(global::System.AttributeTargets.Method, AllowMultiple = true)]");
        sb.AppendLine("    file sealed class InterceptsLocationAttribute : global::System.Attribute");
        sb.AppendLine("    {");
        sb.AppendLine("        public InterceptsLocationAttribute(int version, string data) { }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("namespace KetPsi.Extensions.Logging");
        sb.AppendLine("{");
        sb.AppendLine($"    internal static class {className}");
        sb.AppendLine("    {");

        int firstId = id;
        var registrations = new List<string>();
        var registered = new HashSet<string>(StringComparer.Ordinal);
        void Register(string line)
        {
            if (registered.Add(line)) registrations.Add(line);
        }

        var body = new StringBuilder();
        foreach (var call in calls)
        {
            int callId = id++;
            Register($"{AbstractionsNs}.StructuredLogStateDispatcher.Register<LogState_{callId}>();");
            foreach (var arg in call.Parts.Where(p => !p.IsLiteral))
            {
                foreach (var registration in arg.Registrations) Register($"{AbstractionsNs}.LogValueFormatter.{registration};");
                if (arg.Kind != LogPartKind.Value) Register($"{AbstractionsNs}.LogValueFormatter.RegisterDeferred<{GetWrapperType(arg)}>();");
            }
            EmitStructAndInterceptor(body, call, callId, jsonContext);
        }

        // Registration replaces runtime reflection (MakeGenericMethod), which NativeAOT cannot support.
        sb.AppendLine("        [global::System.Runtime.CompilerServices.ModuleInitializer]");
        sb.AppendLine("        internal static void Register()");
        sb.AppendLine("        {");
        if (jsonContext != null)
            sb.AppendLine($"            {AbstractionsNs}.JsonLogValueSerializationContext.Context ??= {jsonContext}.Default;");
        foreach (var line in registrations) sb.AppendLine($"            {line}");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.Append(body);

        sb.AppendLine("    }");
        sb.AppendLine("}");
    }

    private static void EmitStructAndInterceptor(StringBuilder sb, LogCallInfo call, int id, string? jsonContext)
    {
        var args = call.Parts.Where(p => !p.IsLiteral).ToList();
        string structName = $"LogState_{id}";
        string template = Literal(call.OriginalFormat);

        // Parameter names pre-encoded as UTF-8 once, so sinks never transcode them per log.
        foreach (var arg in args)
            sb.AppendLine($"        private static readonly byte[] __name_{id}_{arg.Index} = {Literal(arg.Name)}u8.ToArray();");
        if (args.Count > 0) sb.AppendLine();

        // JSON type info is resolved once per call site instead of on every log.
        foreach (var arg in args.Where(a => IsJson(a) && jsonContext != null && a.IsTypeNameExact))
        {
            sb.AppendLine($"        private static readonly global::System.Text.Json.Serialization.Metadata.JsonTypeInfo<{arg.TypeName}> __jsonTypeInfo_{id}_{arg.Index} =");
            sb.AppendLine($"            {jsonContext}.Default.GetTypeInfo(typeof({arg.TypeName})) as global::System.Text.Json.Serialization.Metadata.JsonTypeInfo<{arg.TypeName}>;");
            sb.AppendLine();
        }

        // 1. STATE STRUCT
        sb.AppendLine($"        internal readonly struct {structName} : {AbstractionsNs}.IDeferredUtf8Formatter, {AbstractionsNs}.IStructuredLogState");
        sb.AppendLine("        {");

        foreach (var arg in args) sb.AppendLine($"            private readonly {AbstractionsNs}.LogParameter<{GetWrapperType(arg)}> _arg{arg.Index};");
        if (args.Count > 0)
        {
            sb.AppendLine();
            sb.Append($"            public {structName}(");
            sb.Append(string.Join(", ", args.Select(a => $"{AbstractionsNs}.LogParameter<{GetWrapperType(a)}> arg{a.Index}")));
            sb.AppendLine(")");
            sb.AppendLine("            {");
            foreach (var arg in args) sb.AppendLine($"                _arg{arg.Index} = arg{arg.Index};");
            sb.AppendLine("            }");
        }
        sb.AppendLine();

        sb.AppendLine($"            public global::System.ReadOnlySpan<char> OriginalFormat => {template};");
        sb.AppendLine($"            public global::System.ReadOnlySpan<byte> OriginalFormatUtf8 => {template}u8;");
        sb.AppendLine();

        sb.AppendLine("            public void FormatTo(global::System.Buffers.IBufferWriter<byte> writer)");
        sb.AppendLine("            {");
        foreach (var part in call.Parts)
        {
            if (part.IsLiteral)
            {
                if (part.Text.Length > 0)
                    sb.AppendLine($"                global::System.Buffers.BuffersExtensions.Write<byte>(writer, {Literal(part.Text)}u8);");
            }
            else if (part.Alignment != 0)
            {
                sb.AppendLine($"                _arg{part.Index}.FormatTo(writer, {part.Alignment});");
            }
            else
            {
                sb.AppendLine($"                _arg{part.Index}.FormatTo(writer);");
            }
        }
        sb.AppendLine("            }");
        sb.AppendLine();

        // Same rendering, recording each value's byte range so sinks can reuse it instead of formatting again.
        sb.AppendLine($"            public int ParameterCount => {args.Count};");
        sb.AppendLine();
        sb.AppendLine($"            public void FormatTo({AbstractionsNs}.IPositionedBufferWriter writer, int[] valueRanges)");
        sb.AppendLine("            {");
        if (args.Any(a => a.Alignment == 0)) sb.AppendLine("                int start;");
        foreach (var part in call.Parts)
        {
            if (part.IsLiteral)
            {
                if (part.Text.Length > 0)
                    sb.AppendLine($"                global::System.Buffers.BuffersExtensions.Write<byte>(writer, {Literal(part.Text)}u8);");
            }
            else if (part.Alignment != 0)
            {
                // Padded output is not the plain value: mark it as not reusable.
                sb.AppendLine($"                _arg{part.Index}.FormatTo(writer, {part.Alignment});");
                sb.AppendLine($"                valueRanges[{2 * part.Index}] = -1;");
            }
            else
            {
                sb.AppendLine("                start = writer.WrittenCount;");
                sb.AppendLine($"                _arg{part.Index}.FormatTo(writer);");
                sb.AppendLine($"                valueRanges[{2 * part.Index}] = start;");
                sb.AppendLine($"                valueRanges[{2 * part.Index + 1}] = writer.WrittenCount - start;");
            }
        }
        sb.AppendLine("            }");
        sb.AppendLine();

        sb.AppendLine("            [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
        sb.AppendLine($"            public void WriteParameters<TWriter>(ref TWriter writer) where TWriter : struct, {AbstractionsNs}.ILogParameterWriter");
        sb.AppendLine("            {");
        foreach (var arg in args) sb.AppendLine($"                writer.Write(in _arg{arg.Index});");
        sb.AppendLine("            }");
        sb.AppendLine();

        // Only reached by providers that are not KetPsi-aware (they call the MEL formatter delegate).
        sb.AppendLine("            public override string ToString()");
        sb.AppendLine("            {");
        sb.AppendLine("                var buffer = new global::System.Buffers.ArrayBufferWriter<byte>(256);");
        sb.AppendLine("                FormatTo(buffer);");
        sb.AppendLine("                return global::System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);");
        sb.AppendLine("            }");

        sb.AppendLine("        }");
        sb.AppendLine();

        // 2. INTERCEPTOR
        var parameters = new List<string> { $"this {LoggingNs}.ILogger logger" };
        if (call.HasEventId) parameters.Add($"{LoggingNs}.EventId eventId");
        if (call.HasException) parameters.Add("global::System.Exception exception");
        parameters.Add($"ref {call.HandlerTypeName} handler");

        string returnType = call.IsScope ? "global::System.IDisposable" : "void";

        sb.AppendLine($"        {call.InterceptsLocationAttribute}");
        sb.AppendLine($"        public static {returnType} Intercept_{id}({string.Join(", ", parameters)})");
        sb.AppendLine("        {");

        // The handler constructor already evaluated IsEnabled and left both buffers null when disabled.
        sb.AppendLine($"            if (handler.Bytes == null && handler.References == null) return{(call.IsScope ? " null" : "")};");
        sb.AppendLine("            try");
        sb.AppendLine("            {");
        if (args.Count > 0)
        {
            sb.AppendLine("                int __byteOffset = 0;");
            sb.AppendLine("                int __refOffset = 0;");
        }

        var ctorArgs = new List<string>();
        foreach (var arg in args)
        {
            // Locals are index-based so duplicate or non-identifier property names cannot clash.
            string local = $"__a{arg.Index}";
            sb.AppendLine($"                {arg.TypeName} {local};");
            sb.AppendLine($"                if (global::System.Runtime.CompilerServices.RuntimeHelpers.IsReferenceOrContainsReferences<{arg.TypeName}>())");
            sb.AppendLine("                {");
            sb.AppendLine($"                    {local} = ({arg.TypeName})handler.References[__refOffset++];");
            sb.AppendLine("                }");
            sb.AppendLine("                else");
            sb.AppendLine("                {");
            sb.AppendLine($"                    {local} = global::System.Runtime.CompilerServices.Unsafe.ReadUnaligned<{arg.TypeName}>(ref handler.Bytes[__byteOffset]);");
            sb.AppendLine($"                    __byteOffset += global::System.Runtime.CompilerServices.Unsafe.SizeOf<{arg.TypeName}>();");
            sb.AppendLine("                }");

            string valueExpr = local;
            string formatArg = arg.Format is null ? "null" : Literal(arg.Format);

            if (IsJson(arg))
            {
                string typeInfo = jsonContext != null && arg.IsTypeNameExact ? $"__jsonTypeInfo_{id}_{arg.Index}" : "null";
                valueExpr = $"new {AbstractionsNs}.JsonLogValue<{arg.TypeName}>({local}, {typeInfo})";
                // Keep the format so sinks can tell json from json:indexed.
                formatArg = arg.Kind == LogPartKind.JsonIndexed ? "\"json:indexed\"" : "\"json\"";
            }
            else if (arg.Kind == LogPartKind.Mask)
            {
                valueExpr = $"new {AbstractionsNs}.MaskedSpanFormattable<{arg.TypeName}>({local}, {arg.MaskStart}, {arg.MaskEnd}, {formatArg})";
                formatArg = "null";
            }

            ctorArgs.Add($"new {AbstractionsNs}.LogParameter<{GetWrapperType(arg)}>({Literal(arg.Name)}, __name_{id}_{arg.Index}, {valueExpr}, {formatArg})");
        }

        string state = $"new {structName}({string.Join(", ", ctorArgs)})";

        if (call.IsScope)
        {
            sb.AppendLine($"                return logger.BeginScope({state});");
        }
        else
        {
            string eventIdArg = call.HasEventId ? "eventId" : "default";
            string exceptionArg = call.HasException ? "exception" : "null";
            sb.AppendLine($"                logger.Log({LoggingNs}.LogLevel.{call.LogLevel}, {eventIdArg}, {state}, {exceptionArg}, static (s, _) => s.ToString());");
        }

        sb.AppendLine("            }");
        sb.AppendLine("            finally");
        sb.AppendLine("            {");
        sb.AppendLine("                if (handler.Bytes != null) global::System.Buffers.ArrayPool<byte>.Shared.Return(handler.Bytes);");
        sb.AppendLine("                if (handler.References != null) global::System.Buffers.ArrayPool<object>.Shared.Return(handler.References, clearArray: true);");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine();
    }

    private static bool IsJson(LogPart arg) => arg.Kind is LogPartKind.Json or LogPartKind.JsonIndexed;

    private static string GetWrapperType(LogPart arg) => arg.Kind switch
    {
        LogPartKind.Json or LogPartKind.JsonIndexed => $"{AbstractionsNs}.JsonLogValue<{arg.TypeName}>",
        LogPartKind.Mask => $"{AbstractionsNs}.MaskedSpanFormattable<{arg.TypeName}>",
        _ => arg.TypeName,
    };

    /// <summary>Escapes text as a C# string literal, so quotes, backslashes and newlines in log messages are safe.</summary>
    private static string Literal(string value) => SymbolDisplay.FormatLiteral(value, quote: true);

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
