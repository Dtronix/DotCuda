using System.Globalization;
using System.Text;
using ClangSharp;
using ClangSharp.Interop;

namespace DotCuda.Generator;

/// <summary>
/// Evaluates object-like macros by letting clang compile them: an in-memory file includes the header and declares
/// <c>static __auto_type __dotcuda_v_X = (X);</c> per macro, so clang supplies both the value and its type.
/// Macros that aren't expressions simply fail to evaluate.
/// </summary>
sealed class MacroEvaluator(ModelBuilder builder, CXIndex index, string[] args, string headerPath, List<MacroDefinitionRecord> macros)
{
    const string ValuePrefix = "__dotcuda_v_", IntPrefix = "__dotcuda_i_";

    public void Run()
    {
        var model = builder.Model;
        var cfg = model.Config;
        var candidates = macros
            .Where(m => !m.IsFunctionLike && !m.Name.StartsWith('_') && builder.InLibraryFile(ModelBuilder.FileOf(m.Handle)) && builder.PassesConstantPrefix(m.Name))
            .GroupBy(m => m.Name).Select(g => g.Last()).ToList();
        var versionNames = cfg.VersionMacros.Concat(cfg.PackageVersionMacros).Append("CUDA_VERSION").Distinct().ToList();

        var src = new StringBuilder();
        src.Append($"#include \"{Path.GetFileName(headerPath)}\"\n#include \"cuda.h\"\n");
        foreach (var name in candidates.Select(m => m.Name).Concat(versionNames).Distinct())
            src.Append($"static __auto_type {ValuePrefix}{name} = ({name});\nstatic long long {IntPrefix}{name} = (long long)({name});\n");

        var synthPath = Path.Combine(Path.GetDirectoryName(headerPath)!, "__dotcuda_macros.c");
        using var unsaved = CXUnsavedFile.Create(synthPath, src.ToString());
        using var tu = TranslationUnit.GetOrCreate(ModelBuilder.Parse(index, synthPath, args, [unsaved], requireClean: false));
        // clang's error recovery still "evaluates" some invalid initializers (e.g. NVML's bit-range macros "0:0" give 0),
        // so any declaration on a line with an error diagnostic is discarded.
        var errorLines = new HashSet<uint>();
        for (uint i = 0; i < tu.Handle.NumDiagnostics; i++)
        {
            using var d = tu.Handle.GetDiagnostic(i);
            if (d.Severity < CXDiagnosticSeverity.CXDiagnostic_Error) continue;
            d.Location.GetExpansionLocation(out var f, out var line, out _, out _);
            if (Path.GetFileName(f.Name.ToString()) == Path.GetFileName(synthPath)) errorLines.Add(line);
        }
        uint LineOf(VarDecl v)
        {
            v.Location.GetExpansionLocation(out _, out var line, out _, out _);
            return line;
        }
        var vars = tu.TranslationUnitDecl.Decls.OfType<VarDecl>()
            .Where(v => v.Name.StartsWith("__dotcuda_") && !errorLines.Contains(LineOf(v)))
            .ToDictionary(v => v.Name);

        foreach (var name in versionNames)
            if (vars.TryGetValue(ValuePrefix + name, out var v) && Eval(v) is { Kind: CXEvalResultKind.CXEval_Int } r)
                model.VersionInfo[name] = r.Text;
        if (model.VersionInfo.TryGetValue("CUDA_VERSION", out var cv) && int.TryParse(cv, out var cudaVersion))
            model.CudaVersion = $"{cudaVersion / 1000}.{cudaVersion % 1000 / 10}.{cudaVersion % 10}";
        model.HeaderVersion = cfg.PackageVersionMacros.Count == 0
            ? model.CudaVersion
            : string.Join('.', cfg.PackageVersionMacros.Select(m => model.VersionInfo.TryGetValue(m, out var v) ? v
                : throw new InvalidOperationException($"{cfg.Name}: package version macro {m} did not evaluate")));

        foreach (var m in candidates)
        {
            var body = builder.MacroBody(m);
            if (body.Length == 0) continue; // include guards, feature flags
            if (model.Items.ContainsKey("const:" + m.Name)) continue;
            var file = ModelBuilder.FileOf(m.Handle);
            try
            {
                if (!vars.TryGetValue(ValuePrefix + m.Name, out var v)) throw new SkipException("not a valid C expression");
                var constant = Convert(m.Name, v, vars.GetValueOrDefault(IntPrefix + m.Name), file, body);
                model.Add(constant);
            }
            catch (SkipException ex)
            {
                model.Skipped.Add(new("macro", m.Name, file, $"{ex.Message}: `{Truncate(body)}`"));
            }
        }
    }

    ConstantItem Convert(string name, VarDecl v, VarDecl? asInt, string file, string body)
    {
        var type = v.Type.Handle.CanonicalType;
        var doc = $"<c>{System.Security.SecurityElement.Escape(Truncate(body))}</c>";
        ConstantItem Make(string csType, string value, long? raw, bool staticReadonly = false) => new()
        {
            Key = "const:" + name, CsName = name, Library = builder.Model.Config.Name, SourceFile = file,
            CsType = csType, Value = value, RawValue = raw, StaticReadonly = staticReadonly, Doc = doc,
        };

        switch (type.kind)
        {
            case CXTypeKind.CXType_Pointer:
            {
                // "12" decays to char* under __auto_type and no longer evaluates as a string; a body that is exactly one
                // escape-free string literal is taken from the source (C and C# agree on such literals).
                if (System.Text.RegularExpressions.Regex.IsMatch(body, "^\"[^\"\\\\]*\"$")) return Make("string", body, null);
                if (asInt is null || Eval(asInt) is not { Kind: CXEvalResultKind.CXEval_Int } pi) throw new SkipException("pointer value is not a constant");
                var pointee = type.PointeeType.CanonicalType;
                if (pointee.kind == CXTypeKind.CXType_Record && pointee.Declaration.Definition.IsNull)
                {
                    var item = Make("", "", pi.Raw, staticReadonly: true);
                    var handle = builder.HandleFor(pointee.Declaration, item);
                    return Make(handle, $"new((nint){pi.Raw})", pi.Raw, staticReadonly: true).WithDeps(item);
                }
                return Make("nint", pi.Raw.ToString(CultureInfo.InvariantCulture), pi.Raw);
            }
            case CXTypeKind.CXType_Float or CXTypeKind.CXType_Double:
            {
                if (Eval(v) is not { Kind: CXEvalResultKind.CXEval_Float } f) throw new SkipException("not a constant");
                return Make("double", f.Text, null);
            }
            case CXTypeKind.CXType_ConstantArray:
            {
                if (Eval(v) is not { Kind: CXEvalResultKind.CXEval_StrLiteral } s) throw new SkipException("not a constant");
                return Make("string", SymbolDisplayString(s.Text), null);
            }
        }
        var csType = type.kind switch
        {
            CXTypeKind.CXType_Int or CXTypeKind.CXType_Short or CXTypeKind.CXType_SChar or CXTypeKind.CXType_Char_S => "int",
            CXTypeKind.CXType_UInt or CXTypeKind.CXType_UShort or CXTypeKind.CXType_UChar or CXTypeKind.CXType_Char_U => "uint",
            CXTypeKind.CXType_LongLong or CXTypeKind.CXType_Long => "long",
            CXTypeKind.CXType_ULongLong or CXTypeKind.CXType_ULong => "ulong",
            CXTypeKind.CXType_Enum => "int",
            // A failed __auto_type means the body isn't an expression at all (calling conventions, attributes, ...).
            _ when type.Spelling.ToString() == "__auto_type" => throw new SkipException("not a value (attribute or calling-convention macro)"),
            _ => throw new SkipException($"value of type '{type.Spelling}' is not an integer constant"),
        };
        if (Eval(v) is not { Kind: CXEvalResultKind.CXEval_Int } r) throw new SkipException("not a constant");
        return Make(csType, r.Text, r.Raw);
    }

    readonly record struct EvalResult(CXEvalResultKind Kind, string Text, long Raw);

    static EvalResult? Eval(VarDecl v)
    {
        using var ev = v.Handle.Evaluate;
        switch (ev.Kind)
        {
            case CXEvalResultKind.CXEval_Int:
                return ev.IsUnsignedInt
                    ? new(ev.Kind, ev.AsUnsigned.ToString(CultureInfo.InvariantCulture), unchecked((long)ev.AsUnsigned))
                    : new(ev.Kind, ev.AsLongLong.ToString(CultureInfo.InvariantCulture), ev.AsLongLong);
            case CXEvalResultKind.CXEval_Float:
                return new(ev.Kind, ev.AsDouble.ToString("R", CultureInfo.InvariantCulture), 0);
            case CXEvalResultKind.CXEval_StrLiteral:
                return new(ev.Kind, ev.AsStr.ToString() ?? "", 0);
            default:
                return null;
        }
    }

    static string SymbolDisplayString(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (var ch in s)
            sb.Append(ch switch { '"' => "\\\"", '\\' => "\\\\", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t", _ => ch.ToString() });
        return sb.Append('"').ToString();
    }

    static string Truncate(string s)
    {
        s = string.Join(' ', s.Split((char[])['\r', '\n', '\t', ' ', '\\'], StringSplitOptions.RemoveEmptyEntries));
        return s.Length > 80 ? s[..77] + "..." : s;
    }
}

static class ConstantItemExtensions
{
    public static ConstantItem WithDeps(this ConstantItem c, Item from)
    {
        c.Deps.UnionWith(from.Deps);
        return c;
    }
}
