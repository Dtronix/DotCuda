using System.Text;
using System.Text.RegularExpressions;
using ClangSharp;
using ClangSharp.Interop;

namespace DotCuda.Generator;

/// <summary>All libraries generated in this run, so later libraries can reuse earlier libraries' types.</summary>
public sealed class Registry
{
    public Dictionary<string, LibraryModel> Libraries { get; } = [];
}

/// <summary>Parses one library's header with libclang and turns it into a <see cref="LibraryModel"/>.</summary>
public sealed partial class ModelBuilder
{
    readonly LibraryConfig _cfg;
    readonly LibraryModel _model;
    readonly Registry _registry;
    readonly string _include;
    readonly HashSet<string> _libraryFiles;

    // Typedef indexes over the whole translation unit, in declaration order.
    readonly Dictionary<string, List<string>> _aliases = [];          // record/enum USR -> typedef names
    readonly Dictionary<string, string> _typedefTarget = [];          // typedef name -> record/enum USR
    readonly Dictionary<string, List<(string Name, bool InLibrary)>> _pointerTypedefs = []; // record USR -> pointer typedefs
    readonly Dictionary<string, string> _intHandleByName = [];        // typedef name -> wrapper name
    readonly Dictionary<string, byte[]> _fileBytes = [];
    readonly Dictionary<string, Decl> _declsByUsr = [];               // record/enum definitions, including nested ones

    public ModelBuilder(LibraryConfig cfg, Registry registry, string include)
    {
        _cfg = cfg;
        _registry = registry;
        _include = include;
        _model = new LibraryModel(cfg);
        _libraryFiles = new HashSet<string>(cfg.AllHeaderFiles, StringComparer.OrdinalIgnoreCase);
        foreach (var (wrapper, aliases) in cfg.IntegerHandles)
        {
            _intHandleByName[wrapper] = wrapper;
            foreach (var a in aliases) _intHandleByName[a] = wrapper;
        }
    }

    /// <summary>Target-specific clang arguments. Windows x64 (MSVC) generates the bindings; --target linux re-parses for comparison.</summary>
    public static string[] TargetArgs { get; set; } = ["--target=x86_64-pc-windows-msvc", "-fms-extensions", "-fms-compatibility"];

    public static string[] ClangArgs(LibraryConfig cfg, string include) =>
    [
        .. TargetArgs, "-xc", "-std=c11",
        .. ResolveIncludeDirs(cfg).Select(d => $"-I{d}"),
        $"-I{include}",
        .. cfg.Defines.Concat(cfg.AnnotationDefines).Select(d => "-D" + d),
        .. cfg.PerThreadDefaultStream ? ["-DCUDA_API_PER_THREAD_DEFAULT_STREAM=1"] : Array.Empty<string>(),
    ];

    public static CXTranslationUnit Parse(CXIndex index, string path, string[] args, CXUnsavedFile[] unsaved, bool requireClean)
    {
        var err = CXTranslationUnit.TryParse(index, path, args, unsaved,
            CXTranslationUnit_Flags.CXTranslationUnit_DetailedPreprocessingRecord,
            out var handle);
        if (err != CXErrorCode.CXError_Success) throw new InvalidOperationException($"libclang failed to parse {path}: {err}");
        if (requireClean)
        {
            var errors = new List<string>();
            for (uint i = 0; i < handle.NumDiagnostics; i++)
            {
                using var d = handle.GetDiagnostic(i);
                if (d.Severity >= CXDiagnosticSeverity.CXDiagnostic_Error) errors.Add(d.Format(CXDiagnostic.DefaultDisplayOptions).ToString());
            }
            if (errors.Count > 0) throw new InvalidOperationException($"{path} has {errors.Count} errors:\n" + string.Join("\n", errors.Take(20)));
        }
        return handle;
    }

    public LibraryModel Build()
    {
        var index = CXIndex.Create(false, false);
        var path = ResolveIncludeDirs(_cfg).Append(_include).Select(d => Path.Combine(d, _cfg.Header)).FirstOrDefault(File.Exists)
                   ?? throw new FileNotFoundException($"{_cfg.Name}: {_cfg.Header} not found in {_include} or the config's includeDirs");
        var args = ClangArgs(_cfg, _include);
        // With extraIncludes, parse a small in-memory file that includes the header and the extras.
        var mainPath = path;
        CXUnsavedFile[] unsaved = [];
        if (_cfg.ExtraIncludes.Count > 0)
        {
            mainPath = Path.Combine(Path.GetDirectoryName(path)!, "__dotcuda_main.c");
            var text = string.Concat(_cfg.ExtraIncludes.Prepend(_cfg.Header).Select(h => $"#include \"{h}\"\n"));
            unsaved = [CXUnsavedFile.Create(mainPath, text)];
        }
        using var tu = TranslationUnit.GetOrCreate(Parse(index, mainPath, args, unsaved, requireClean: true));
        foreach (var u in unsaved) u.Dispose();

        var children = tu.TranslationUnitDecl.CursorChildren.ToList();
        IndexTypedefs(children.OfType<TypedefDecl>());
        IndexDefinitions(tu.TranslationUnitDecl.Decls);

        foreach (var c in children)
        {
            if (!InLibrary(c.Handle)) continue;
            try
            {
                switch (c)
                {
                    case EnumDecl e when e.IsThisDeclarationADefinition:
                        if (IsAnonymousTag(e.Handle) && !_aliases.ContainsKey(e.Handle.Usr.ToString())) AddAnonymousEnum(e);
                        else if (PassesTypePrefix(ChooseName(e.Handle).Name)) EnsureEnum(e.Handle, null);
                        break;
                    case RecordDecl r when r.IsThisDeclarationADefinition:
                        if (IsAnonymousTag(r.Handle) && !_aliases.ContainsKey(r.Handle.Usr.ToString())) break; // nested; handled by parent
                        if (PassesTypePrefix(ChooseName(r.Handle).Name)) EnsureRecord(r.Handle, null);
                        break;
                    case TypedefDecl td:
                        VisitTypedef(td);
                        break;
                    case FunctionDecl f:
                        AddFunction(f);
                        break;
                }
            }
            catch (SkipException ex)
            {
                _model.Skipped.Add(new(KindOf(c), NameOf(c), FileOf(c.Handle), ex.Message));
            }
        }

        ApplyAliasMacros(children.OfType<MacroDefinitionRecord>());

        foreach (var extra in _cfg.ExtraTypes)
        {
            var td = children.OfType<TypedefDecl>().FirstOrDefault(t => t.Name == extra)
                     ?? throw new InvalidOperationException($"{_cfg.Name}: extraTypes entry '{extra}' is not a typedef in {_cfg.Header}");
            try { MapType(td.TypeForDecl.Handle, null); }
            catch (SkipException ex) { _model.Skipped.Add(new("typedef", extra, FileOf(td.Handle), ex.Message)); }
        }

        new MacroEvaluator(this, index, args, path, children.OfType<MacroDefinitionRecord>().ToList()).Run();

        PropagateSkips();
        CheckNameClashes();
        index.Dispose();
        return _model;
    }

    // ---------------------------------------------------------------- typedef indexing

    void IndexTypedefs(IEnumerable<TypedefDecl> typedefs)
    {
        foreach (var td in typedefs)
        {
            var name = td.Name;
            var u = StripSugar(td.Handle.TypedefDeclUnderlyingType, stopAtTypedef: true);
            switch (u.kind)
            {
                case CXTypeKind.CXType_Record or CXTypeKind.CXType_Enum:
                {
                    var usr = u.Declaration.Usr.ToString();
                    Add(_aliases, usr, name);
                    _typedefTarget[name] = usr;
                    break;
                }
                case CXTypeKind.CXType_Typedef when _typedefTarget.TryGetValue(u.Declaration.Spelling.ToString(), out var target):
                    Add(_aliases, target, name);
                    _typedefTarget[name] = target;
                    break;
                case CXTypeKind.CXType_Pointer:
                {
                    var p = u.PointeeType.CanonicalType;
                    if (p.kind == CXTypeKind.CXType_Record)
                        Add(_pointerTypedefs, p.Declaration.Usr.ToString(), (name, InLibrary(td.Handle)));
                    break;
                }
            }
        }

        static void Add<T>(Dictionary<string, List<T>> d, string k, T v)
        {
            if (!d.TryGetValue(k, out var l)) d[k] = l = [];
            if (!l.Contains(v)) l.Add(v);
        }
    }

    void IndexDefinitions(IEnumerable<Decl> decls)
    {
        foreach (var d in decls)
        {
            if (d is TagDecl { IsThisDeclarationADefinition: true } tag) _declsByUsr[tag.Handle.Usr.ToString()] = tag;
            if (d is RecordDecl r) IndexDefinitions(r.Decls);
        }
    }

    void VisitTypedef(TypedefDecl td)
    {
        var name = td.Name;
        if (_intHandleByName.TryGetValue(name, out var wrapper))
        {
            if (wrapper == name) EnsureIntHandle(wrapper, td.Handle.TypedefDeclUnderlyingType, null);
            return;
        }
        var u = td.Handle.TypedefDeclUnderlyingType;
        var canon = u.CanonicalType;
        if (canon.kind == CXTypeKind.CXType_Pointer && canon.PointeeType.kind == CXTypeKind.CXType_Record
            && (canon.PointeeType.Declaration.Definition.IsNull || _cfg.PointerHandles.Contains(name)) && PassesTypePrefix(name))
            EnsureHandle(canon.PointeeType.Declaration, null);
        else if (canon.kind == CXTypeKind.CXType_Pointer && canon.PointeeType.kind == CXTypeKind.CXType_Void && _cfg.PointerHandles.Contains(name))
            EnsureVoidHandle(name, null);
        // Typedefs of records/enums are covered by the record/enum itself; function-pointer typedefs are inlined.
    }

    // ---------------------------------------------------------------- naming

    [GeneratedRegex(@"_v\d+$")] private static partial Regex VersionSuffix();

    internal (string Name, string CSpelling) ChooseName(CXCursor decl)
    {
        var usr = decl.Usr.ToString();
        var aliases = _aliases.GetValueOrDefault(usr) ?? [];
        var anonymous = IsAnonymousTag(decl);
        var tag = anonymous ? "" : decl.Spelling.ToString();
        string name;
        if (!anonymous && tag.EndsWith("_t")) name = tag;
        else if (aliases.FirstOrDefault(a => !VersionSuffix().IsMatch(a)) is { } unversioned) name = unversioned;
        else if (aliases.Count > 0) name = aliases[0];
        else if (!anonymous) name = tag;
        else throw new SkipException("anonymous type with no typedef");

        var keyword = decl.kind switch
        {
            CXCursorKind.CXCursor_EnumDecl => "enum",
            CXCursorKind.CXCursor_UnionDecl => "union",
            _ => "struct",
        };
        var cSpelling = aliases.Contains(name) ? name : $"{keyword} {name}";
        return (_cfg.Renames.GetValueOrDefault(name, name), cSpelling);
    }

    static bool IsAnonymousTag(CXCursor decl) => decl.IsAnonymous || decl.Spelling.ToString().Length == 0
        || decl.Spelling.ToString().Contains("(unnamed") || decl.Spelling.ToString().Contains("(anonymous");

    bool PassesTypePrefix(string name) => _cfg.TypePrefixes.Count == 0 || _cfg.TypePrefixes.Any(name.StartsWith);

    // ---------------------------------------------------------------- lookup

    /// <summary>An item with this key from this library or a referenced one.</summary>
    Item? Find(string key)
    {
        if (_model.Items.TryGetValue(key, out var own)) return own;
        foreach (var r in _cfg.References)
            if (_registry.Libraries.TryGetValue(r, out var lib) && lib.Items.TryGetValue(key, out var item))
                return item;
        return null;
    }

    string Use(Item item, Item? depOwner)
    {
        depOwner?.Deps.Add(item.Key);
        return item.CsName;
    }

    // ---------------------------------------------------------------- enums

    string EnsureEnum(CXCursor decl, Item? depOwner)
    {
        var def = decl.Definition;
        if (def.IsNull) throw new SkipException($"enum {decl.Spelling} has no definition");
        var key = def.Usr.ToString();
        if (Find(key) is { } existing) return Use(existing, depOwner);

        var (name, cSpelling) = ChooseName(def);
        var underlyingType = def.EnumDecl_IntegerType;
        var underlying = MapPrimitive(underlyingType.CanonicalType)
                         ?? throw new SkipException($"enum {name} has unsupported underlying type {underlyingType.Spelling}");
        bool unsigned = underlying is "uint" or "ulong" or "ushort" or "byte";
        var item = new EnumItem
        {
            Key = key, CsName = name, Library = _cfg.Name, SourceFile = FileOf(def), Underlying = underlying,
            CSpelling = cSpelling, Doc = DocOf(def), Obsolete = ObsoleteOf(def),
        };
        _model.Add(item);
        foreach (var c in EnumWrapper(def).Enumerators)
        {
            var h = c.Handle;
            var raw = unsigned ? unchecked((long)h.EnumConstantDeclUnsignedValue) : h.EnumConstantDeclValue;
            var text = unsigned ? h.EnumConstantDeclUnsignedValue.ToString() : h.EnumConstantDeclValue.ToString();
            item.Members.Add(new(Escape(c.Name), text, raw, DocOf(h), ObsoleteOf(h)));
        }
        return Use(item, depOwner);
    }

    /// <summary>An untagged, untypedef'd enum: its members become constants of the library class.</summary>
    void AddAnonymousEnum(EnumDecl e)
    {
        var underlying = MapPrimitive(e.Handle.EnumDecl_IntegerType.CanonicalType) ?? "int";
        foreach (var c in e.Enumerators)
        {
            if (!PassesConstantPrefix(c.Name)) continue;
            var value = c.Handle.EnumConstantDeclValue;
            _model.Add(new ConstantItem
            {
                Key = "const:" + c.Name, CsName = c.Name, Library = _cfg.Name, SourceFile = FileOf(c.Handle),
                CsType = underlying, Value = value.ToString(), RawValue = value, Doc = DocOf(c.Handle),
            });
        }
    }

    EnumDecl EnumWrapper(CXCursor def) => (EnumDecl)_declsByUsr[def.Usr.ToString()];

    // ---------------------------------------------------------------- records

    string EnsureRecord(CXCursor decl, Item? depOwner)
    {
        var def = decl.Definition;
        if (def.IsNull) throw new SkipException($"{decl.Spelling} is opaque but used by value");
        var key = def.Usr.ToString();
        if (Find(key) is { } existing) return Use(existing, depOwner);

        var (name, cSpelling) = ChooseName(def);
        var type = def.Type;
        var item = new StructItem
        {
            Key = key, CsName = name, Library = _cfg.Name, SourceFile = FileOf(def), CSpelling = cSpelling,
            IsUnion = def.kind == CXCursorKind.CXCursor_UnionDecl, Size = type.SizeOf, Align = type.AlignOf,
            Doc = DocOf(def), Obsolete = ObsoleteOf(def),
        };
        _model.Add(item); // before fields, so self-referencing pointers resolve
        try
        {
            BuildFields(def, item, item);
        }
        catch (SkipException ex)
        {
            item.SkipReason = ex.Message;
            _model.Skipped.Add(new(item.IsUnion ? "union" : "struct", name, item.SourceFile, ex.Message));
        }
        return Use(item, depOwner);
    }

    void BuildFields(CXCursor def, StructItem target, StructItem top)
    {
        var record = (RecordDecl)_declsByUsr[def.Usr.ToString()];
        if (record.Fields.Count == 0) throw new SkipException($"{target.CsName} has no fields");
        int anonymousCount = 0;
        var used = new HashSet<string>();
        foreach (var f in record.Fields)
        {
            var h = f.Handle;
            var cName = f.Name;
            if (h.IsBitField) throw new SkipException($"bit-field '{cName}' in {target.CsName}");
            var offsetBits = h.OffsetOfField;
            if (offsetBits < 0) throw new SkipException($"clang could not lay out field '{cName}' of {target.CsName}");
            var t = h.Type;
            var stripped = StripSugar(t, stopAtTypedef: false);
            // Unnamed members (C11 anonymous struct/union) are spelled "(anonymous at file:line)" by clang.
            bool anonymousMember = cName.Length == 0 || cName.Contains("(anonymous") || cName.Contains("(unnamed");
            var csName = anonymousMember ? (++anonymousCount == 1 ? "Anonymous" : $"Anonymous{anonymousCount}") : Escape(cName);
            if (csName == target.CsName || csName.TrimStart('@') == target.CsName) csName += "_";
            used.Add(csName);

            StructItem? nested = null;
            string csType;
            if (stripped.kind == CXTypeKind.CXType_Record && IsAnonymousTag(stripped.Declaration)
                && !_aliases.ContainsKey(stripped.Declaration.Usr.ToString()))
            {
                var nd = stripped.Declaration.Definition;
                var isUnion = nd.kind == CXCursorKind.CXCursor_UnionDecl;
                var baseName = char.ToUpperInvariant(csName.TrimStart('@')[0]) + csName.TrimStart('@')[1..];
                nested = new StructItem
                {
                    Key = $"{top.Key}#{csName}", CsName = baseName + (isUnion ? "Union" : "Struct"), Library = _cfg.Name,
                    IsUnion = isUnion, Size = stripped.SizeOf, Align = stripped.AlignOf,
                };
                BuildFields(nd, nested, top);
                target.Nested.Add(nested);
                csType = nested.CsName;
            }
            else if (stripped.kind == CXTypeKind.CXType_ConstantArray)
            {
                csType = BuildFixedBuffer(stripped, csName.TrimStart('@'), target, top, out nested);
            }
            else if (stripped.kind == CXTypeKind.CXType_IncompleteArray)
            {
                throw new SkipException($"flexible array member '{cName}' in {target.CsName}");
            }
            else
            {
                csType = MapType(t, top);
            }
            bool arrayOfNested = nested is not null && stripped.kind == CXTypeKind.CXType_ConstantArray;
            target.Fields.Add(new(csName, csType, anonymousMember ? null : cName, offsetBits / 8, t.SizeOf, DocOf(h), nested, arrayOfNested));
        }
    }

    string BuildFixedBuffer(CXType array, string baseName, StructItem owner, StructItem top, out StructItem? nestedElement)
    {
        var elem = array.ArrayElementType;
        var strippedElem = StripSugar(elem, stopAtTypedef: false);
        string elemCs;
        string? comment = null;
        nestedElement = null;
        if (strippedElem.kind == CXTypeKind.CXType_ConstantArray)
            elemCs = BuildFixedBuffer(strippedElem, baseName + "_Row", owner, top, out _);
        else if (strippedElem.kind == CXTypeKind.CXType_Record && IsAnonymousTag(strippedElem.Declaration)
                 && !_aliases.ContainsKey(strippedElem.Declaration.Usr.ToString()))
        {
            // struct { ... } sensor[3]: the anonymous element type becomes a nested struct, and the buffer holds it.
            var nd = strippedElem.Declaration.Definition;
            var isUnion = nd.kind == CXCursorKind.CXCursor_UnionDecl;
            nestedElement = new StructItem
            {
                Key = $"{top.Key}#{owner.CsName}.{baseName}[]",
                CsName = char.ToUpperInvariant(baseName[0]) + baseName[1..] + (isUnion ? "Union" : "Struct"),
                Library = _cfg.Name, IsUnion = isUnion, Size = strippedElem.SizeOf, Align = strippedElem.AlignOf,
            };
            BuildFields(nd, nestedElement, top);
            owner.Nested.Add(nestedElement);
            elemCs = nestedElement.CsName;
        }
        else
        {
            elemCs = MapType(elem, top);
            if (elemCs.Contains('*'))
            {
                // [InlineArray] element types must be valid generic arguments, so pointers become nint.
                comment = elemCs;
                elemCs = "nint";
            }
        }
        var name = baseName + "_e__FixedBuffer";
        owner.Buffers.Add(new(name, elemCs, array.ArraySize, comment));
        return name;
    }

    // ---------------------------------------------------------------- handles

    string EnsureHandle(CXCursor recordDecl, Item? depOwner)
    {
        var usr = recordDecl.Usr.ToString();
        var key = "handle:" + usr;
        if (Find(key) is { } existing) return Use(existing, depOwner);
        var candidates = _pointerTypedefs.GetValueOrDefault(usr) ?? [];
        var name = candidates.FirstOrDefault(c => c.InLibrary).Name ?? candidates.FirstOrDefault().Name ?? recordDecl.Spelling.ToString();
        name = _cfg.Renames.GetValueOrDefault(name, name);
        var item = new HandleItem
        {
            Key = key, CsName = name, Library = _cfg.Name, SourceFile = FileOf(recordDecl), ValueType = "nint",
            CDescription = $"struct {recordDecl.Spelling} *",
        };
        _model.Add(item);
        return Use(item, depOwner);
    }

    /// <summary>A <c>typedef void *Name</c> listed in pointerHandles (cuDNN's cudnnBackendDescriptor_t).</summary>
    string EnsureVoidHandle(string name, Item? depOwner)
    {
        var key = "handle:void:" + name;
        if (Find(key) is { } existing) return Use(existing, depOwner);
        var item = new HandleItem { Key = key, CsName = name, Library = _cfg.Name, ValueType = "nint", CDescription = $"typedef void *{name}" };
        _model.Add(item);
        return Use(item, depOwner);
    }

    string EnsureIntHandle(string wrapper, CXType type, Item? depOwner)
    {
        var key = "int:" + wrapper;
        if (Find(key) is { } existing) return Use(existing, depOwner);
        // Walk the typedef chain so fixed-width names win over the canonical type: CUgraphConditionalHandle is
        // cuuint64_t -> uint64_t, which is 'unsigned long long' on Windows but 'unsigned long' on Linux; both are ulong.
        string? value = null;
        var t = type;
        for (int guard = 0; guard < 64 && value is null; guard++)
        {
            if (t.kind == CXTypeKind.CXType_Elaborated) { t = t.NamedType; continue; }
            if (t.kind != CXTypeKind.CXType_Typedef) break;
            if (StdTypedefs.TryGetValue(t.Declaration.Spelling.ToString(), out var std)) value = std;
            else t = t.Declaration.TypedefDeclUnderlyingType;
        }
        if (value is null && t.CanonicalType.kind is CXTypeKind.CXType_Long or CXTypeKind.CXType_ULong)
            throw new SkipException($"integer handle {wrapper} is 'long', which differs between Windows and Linux");
        value ??= MapPrimitive(t.CanonicalType) ?? throw new SkipException($"integer handle {wrapper} is not an integer");
        var item = new HandleItem
        {
            Key = key, CsName = wrapper, Library = _cfg.Name, ValueType = value, CDescription = type.CanonicalType.Spelling.ToString(),
        };
        _model.Add(item);
        return Use(item, depOwner);
    }

    // ---------------------------------------------------------------- functions

    void AddFunction(FunctionDecl f)
    {
        var h = f.Handle;
        var written = WrittenName(h);
        if (!(_cfg.FunctionPrefixes.Count == 0 || _cfg.FunctionPrefixes.Any(written.StartsWith))) return;
        var key = "fn:" + written;
        if (_model.Items.ContainsKey(key)) return; // redeclaration
        if (_cfg.ExcludeFunctions.TryGetValue(written, out var why)) throw new SkipException($"excluded by config: {why}");
        var entryPoint = f.Name;
        if (h.StorageClass == CX_StorageClass.CX_SC_Static)
        {
            // static inline T f(a, b) { return g(a, b); } (nvJitLink: nvJitLinkCreate -> __nvJitLinkCreate_12_6):
            // bind f's signature to g's export. Anything else in an inline body has no export to bind to.
            entryPoint = ForwardTarget(f)
                ?? throw new SkipException("static inline in the header with a body that does more than forward its arguments, so there is no export");
        }
        if (h.Type.kind == CXTypeKind.CXType_FunctionProto && h.Type.IsFunctionTypeVariadic)
            throw new SkipException("variadic");

        var item = new FunctionItem
        {
            Key = key, CsName = written, Library = _cfg.Name, SourceFile = FileOf(h), EntryPoint = entryPoint,
            Doc = DocOf(h), Obsolete = ObsoleteOf(h),
        };
        item.ReturnType = MapType(h.ResultType, item);
        var used = new HashSet<string>();
        for (uint i = 0; i < (uint)h.NumArguments; i++)
        {
            var a = h.GetArgument(i);
            var pname = a.Spelling.ToString();
            if (pname.Length == 0) pname = $"arg{i}";
            pname = Escape(pname);
            while (!used.Add(pname)) pname += "_";
            item.Parameters.Add((MapType(a.Type, item), pname));
        }
        _model.Add(item);
    }

    /// <summary>
    /// Headers that declare the versioned symbol directly (nvml.h: <c>nvmlInit_v2(void)</c>) and alias it with
    /// <c>#define nvmlInit nvmlInit_v2</c> are bound under the name C code calls, nvmlInit, with the versioned entry point.
    /// </summary>
    void ApplyAliasMacros(IEnumerable<MacroDefinitionRecord> macros)
    {
        foreach (var m in macros)
        {
            if (m.IsFunctionLike || !InLibrary(m.Handle)) continue;
            var body = MacroBody(m);
            if (!IdentifierRegex().IsMatch(body)) continue;
            if (!_model.Items.TryGetValue("fn:" + body, out var target) || target is not FunctionItem fn || fn.CsName != body) continue;
            if (_model.Items.ContainsKey("fn:" + m.Name) || _model.Items.Values.Any(i => i is FunctionItem && i.CsName == m.Name)) continue;
            fn.CsName = m.Name;
        }
    }

    [GeneratedRegex(@"^[A-Za-z_]\w*$")] private static partial Regex IdentifierRegex();

    /// <summary>The callee's name if <paramref name="f"/>'s body is exactly <c>return callee(p0, p1, ...)</c> over its own parameters.</summary>
    static string? ForwardTarget(FunctionDecl f)
    {
        if (f.Body is not CompoundStmt { Body.Count: 1 } body) return null;
        var expr = body.Body[0] switch
        {
            ReturnStmt r => r.RetValue,
            Expr e when f.ReturnType.Handle.kind == CXTypeKind.CXType_Void => e,
            _ => null,
        };
        if (Unwrap(expr) is not CallExpr { DirectCallee: { } callee } call) return null;
        if (call.Args.Count != f.Parameters.Count) return null;
        for (int i = 0; i < call.Args.Count; i++)
            if (Unwrap(call.Args[i]) is not DeclRefExpr { Decl: var d } || d != f.Parameters[i]) return null;
        if (callee.Parameters.Count != f.Parameters.Count) return null;
        for (int i = 0; i < callee.Parameters.Count; i++)
            if (clang.equalTypes(callee.Parameters[i].Type.Handle.CanonicalType, f.Parameters[i].Type.Handle.CanonicalType) == 0) return null;
        return callee.Name;

        static Expr? Unwrap(Expr? e)
        {
            while (true)
            {
                switch (e)
                {
                    case ImplicitCastExpr c: e = c.SubExpr; continue;
                    case ParenExpr p: e = p.SubExpr; continue;
                    default: return e;
                }
            }
        }
    }

    /// <summary>Resolves a config's includeDirs; entries that don't resolve to an existing directory are dropped.</summary>
    public static IEnumerable<string> ResolveIncludeDirs(LibraryConfig cfg) =>
        cfg.IncludeDirs.Select(ResolveDirectory).OfType<string>();

    /// <summary>Expands environment variables, then resolves '*' segments to the highest (natural order) match. Null if missing.</summary>
    public static string? ResolveDirectory(string raw)
    {
        var expanded = Environment.ExpandEnvironmentVariables(raw);
        if (expanded.Contains('%')) return null; // unset variable
        var parts = expanded.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        bool drive = parts[0].EndsWith(':');
        string? current = drive ? parts[0] + Path.DirectorySeparatorChar : Path.GetPathRoot(expanded) is { Length: > 0 } r ? r : ".";
        foreach (var part in parts.Skip(drive ? 1 : 0))
        {
            if (current is null) break;
            current = part.Contains('*')
                ? (Directory.Exists(current) ? Directory.GetDirectories(current, part).OrderBy(d => d, NaturalComparer.Instance).LastOrDefault() : null)
                : Path.Combine(current, part);
        }
        return current is not null && Directory.Exists(current) ? current : null;
    }

    // ---------------------------------------------------------------- type mapping

    static CXType StripSugar(CXType t, bool stopAtTypedef)
    {
        for (int guard = 0; guard < 64; guard++)
        {
            switch (t.kind)
            {
                case CXTypeKind.CXType_Elaborated: t = t.NamedType; continue;
                case CXTypeKind.CXType_Attributed: t = t.ModifiedType; continue;
                case CXTypeKind.CXType_Typedef when !stopAtTypedef: t = t.Declaration.TypedefDeclUnderlyingType; continue;
                case CXTypeKind.CXType_Unexposed when t.CanonicalType.kind != CXTypeKind.CXType_Unexposed: t = t.CanonicalType; continue;
            }
            return t;
        }
        return t;
    }

    static readonly Dictionary<string, string> StdTypedefs = new()
    {
        ["size_t"] = "nuint", ["ssize_t"] = "nint", ["ptrdiff_t"] = "nint", ["intptr_t"] = "nint", ["uintptr_t"] = "nuint",
        ["int8_t"] = "sbyte", ["uint8_t"] = "byte", ["int16_t"] = "short", ["uint16_t"] = "ushort",
        ["int32_t"] = "int", ["uint32_t"] = "uint", ["int64_t"] = "long", ["uint64_t"] = "ulong",
    };

    static string? MapPrimitive(CXType canonical) => canonical.kind switch
    {
        CXTypeKind.CXType_Void => "void",
        CXTypeKind.CXType_Bool => "bool",
        CXTypeKind.CXType_Char_S or CXTypeKind.CXType_Char_U or CXTypeKind.CXType_UChar => "byte",
        CXTypeKind.CXType_SChar => "sbyte",
        CXTypeKind.CXType_Short => "short",
        CXTypeKind.CXType_UShort => "ushort",
        CXTypeKind.CXType_Int => "int",
        CXTypeKind.CXType_UInt => "uint",
        CXTypeKind.CXType_LongLong => "long",
        CXTypeKind.CXType_ULongLong => "ulong",
        CXTypeKind.CXType_Float => "float",
        CXTypeKind.CXType_Double => "double",
        CXTypeKind.CXType_Long => "int",    // only reached for enum underlying types / constants; signatures reject long below
        CXTypeKind.CXType_ULong => "uint",
        _ => null,
    };

    internal string MapType(CXType t, Item? depOwner)
    {
        for (int guard = 0; guard < 64; guard++)
        {
            switch (t.kind)
            {
                case CXTypeKind.CXType_Elaborated: t = t.NamedType; continue;
                case CXTypeKind.CXType_Attributed: t = t.ModifiedType; continue;
                case CXTypeKind.CXType_Typedef:
                {
                    var name = t.Declaration.Spelling.ToString();
                    if (_intHandleByName.TryGetValue(name, out var wrapper)) return EnsureIntHandle(wrapper, t, depOwner);
                    if (StdTypedefs.TryGetValue(name, out var std)) return std;
                    if (_cfg.LongTypes.TryGetValue(name, out var mapped)) return mapped;
                    if (_cfg.PointerHandles.Contains(name))
                    {
                        var pointee = t.Declaration.TypedefDeclUnderlyingType.CanonicalType.PointeeType;
                        if (pointee.kind == CXTypeKind.CXType_Void) return EnsureVoidHandle(name, depOwner);
                        if (pointee.kind != CXTypeKind.CXType_Record) throw new SkipException($"pointerHandles entry {name} is not a pointer to a struct or void");
                        return EnsureHandle(pointee.Declaration, depOwner);
                    }
                    t = t.Declaration.TypedefDeclUnderlyingType;
                    continue;
                }
                case CXTypeKind.CXType_Unexposed when t.CanonicalType.kind != CXTypeKind.CXType_Unexposed:
                    t = t.CanonicalType;
                    continue;
            }
            break;
        }

        switch (t.kind)
        {
            // C 'long' is 32-bit on Windows and 64-bit on Linux; CLong/CULong have exactly that size on each platform.
            case CXTypeKind.CXType_Long:
                return "CLong";
            case CXTypeKind.CXType_ULong:
                return "CULong";
            case CXTypeKind.CXType_WChar:
                throw new SkipException("wchar_t differs between Windows and Linux");
            case CXTypeKind.CXType_Pointer:
                return MapPointer(t.PointeeType, depOwner);
            case CXTypeKind.CXType_Record:
                if (IsAnonymousTag(t.Declaration) && !_aliases.ContainsKey(t.Declaration.Usr.ToString()))
                    throw new SkipException("anonymous record used outside a struct");
                return EnsureRecord(t.Declaration, depOwner);
            case CXTypeKind.CXType_Enum:
                return EnsureEnum(t.Declaration, depOwner);
            case CXTypeKind.CXType_ConstantArray or CXTypeKind.CXType_IncompleteArray:
                return MapType(t.ArrayElementType, depOwner) + "*"; // only parameters reach here (decayed)
        }
        return MapPrimitive(t) ?? throw new SkipException($"unsupported type '{t.Spelling}' ({t.KindSpelling})");
    }

    string MapPointer(CXType pointee, Item? depOwner)
    {
        var canon = pointee.CanonicalType;
        // C library types (FILE, ...) differ between the MSVC and glibc runtimes and are only ever passed by pointer:
        // keep them opaque rather than emitting one runtime's private layout.
        if (canon.kind == CXTypeKind.CXType_Record && canon.Declaration.Location.IsInSystemHeader)
            return "void*";
        if (canon.kind == CXTypeKind.CXType_Record && canon.Declaration.Definition.IsNull)
            return EnsureHandle(canon.Declaration, depOwner);
        if (canon.kind == CXTypeKind.CXType_FunctionNoProto)
            throw new SkipException("function pointer without a prototype");
        if (canon.kind == CXTypeKind.CXType_FunctionProto)
        {
            // Walk the sugared type down to the prototype so typedef'd argument types (size_t, handles) survive.
            var fn = pointee;
            for (int guard = 0; guard < 64 && fn.kind != CXTypeKind.CXType_FunctionProto; guard++)
                fn = fn.kind switch
                {
                    CXTypeKind.CXType_Typedef => fn.Declaration.TypedefDeclUnderlyingType,
                    CXTypeKind.CXType_Elaborated => fn.NamedType,
                    CXTypeKind.CXType_Attributed => fn.ModifiedType,
                    CXTypeKind.CXType_Pointer => fn.PointeeType,
                    _ => fn.CanonicalType,
                };
            if (fn.IsFunctionTypeVariadic) throw new SkipException("variadic function pointer");
            var parts = new List<string>();
            for (uint i = 0; i < (uint)fn.NumArgTypes; i++) parts.Add(MapType(fn.GetArgType(i), depOwner));
            parts.Add(MapType(fn.ResultType, depOwner));
            return $"delegate* unmanaged<{string.Join(", ", parts)}>";
        }
        return MapType(pointee, depOwner) + "*";
    }

    // ---------------------------------------------------------------- constants (called by MacroEvaluator)

    internal bool PassesConstantPrefix(string name) => _cfg.ConstantPrefixes.Count == 0 || _cfg.ConstantPrefixes.Any(name.StartsWith);
    internal bool InLibraryFile(string file) => _libraryFiles.Contains(Path.GetFileName(file));
    internal LibraryModel Model => _model;
    internal string HandleFor(CXCursor recordDecl, Item owner) => EnsureHandle(recordDecl, owner);

    // ---------------------------------------------------------------- skips and checks

    void PropagateSkips()
    {
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var item in _model.Items.Values.Where(i => !i.Skipped))
            {
                foreach (var dep in item.Deps)
                {
                    var d = Find(dep);
                    if (d is { Skipped: true })
                    {
                        item.SkipReason = $"uses {d.CsName}, which is skipped";
                        _model.Skipped.Add(new(KindOf(item), item.CsName, item.SourceFile, item.SkipReason));
                        changed = true;
                        break;
                    }
                }
            }
        }
    }

    void CheckNameClashes()
    {
        var types = _model.Items.Values.Where(i => i is EnumItem or StructItem or HandleItem && !i.Skipped).GroupBy(i => i.CsName).Where(g => g.Count() > 1);
        var clashes = types.Select(g => g.Key).ToList();
        foreach (var r in _cfg.References)
            if (_registry.Libraries.TryGetValue(r, out var lib))
                foreach (var t in _model.Items.Values.Where(i => i is EnumItem or StructItem or HandleItem && !i.Skipped))
                    if (lib.Items.Values.Any(o => o.CsName == t.CsName && o is EnumItem or StructItem or HandleItem))
                        clashes.Add($"{t.CsName} (also in {r})");
        if (clashes.Count > 0) throw new InvalidOperationException($"{_cfg.Name}: duplicate C# type names: {string.Join(", ", clashes)}");
    }

    static string KindOf(Item i) => i switch
    {
        EnumItem => "enum", StructItem s => s.IsUnion ? "union" : "struct", HandleItem => "handle", FunctionItem => "function", _ => "constant",
    };

    static string KindOf(Cursor c) => c switch
    {
        FunctionDecl => "function", EnumDecl => "enum", RecordDecl => "struct", TypedefDecl => "typedef", _ => c.CursorKindSpelling,
    };

    static string NameOf(Cursor c) => c is NamedDecl n ? n.Name : c.Spelling;

    // ---------------------------------------------------------------- source helpers

    bool InLibrary(CXCursor c) => _libraryFiles.Contains(Path.GetFileName(FileOf(c)));

    internal static string FileOf(CXCursor c)
    {
        c.Location.GetExpansionLocation(out var file, out _, out _, out _);
        return file.Name.ToString();
    }

    byte[] Bytes(string file)
    {
        if (!_fileBytes.TryGetValue(file, out var b)) _fileBytes[file] = b = File.ReadAllBytes(file);
        return b;
    }

    /// <summary>The identifier as written at the declaration's expansion location (before macro remapping).</summary>
    string WrittenName(CXCursor c)
    {
        c.Location.GetExpansionLocation(out var file, out _, out _, out var offset);
        var bytes = Bytes(file.Name.ToString()); // offsets are bytes; the headers contain non-ASCII UTF-8
        int end = (int)offset;
        while (end < bytes.Length && (char.IsAsciiLetterOrDigit((char)bytes[end]) || bytes[end] == '_')) end++;
        return Encoding.ASCII.GetString(bytes, (int)offset, end - (int)offset);
    }

    /// <summary>Text of a macro definition after its name, trimmed.</summary>
    internal string MacroBody(MacroDefinitionRecord m)
    {
        var extent = m.Handle.Extent;
        extent.Start.GetExpansionLocation(out var file, out _, out _, out var start);
        extent.End.GetExpansionLocation(out _, out _, out _, out var end);
        var bytes = Bytes(file.Name.ToString());
        var text = Encoding.UTF8.GetString(bytes, (int)start, (int)Math.Min(end, (uint)bytes.Length) - (int)start);
        return text.Length > m.Name.Length ? text[m.Name.Length..].Trim() : "";
    }

    internal static string? DocOf(CXCursor c)
    {
        var s = c.BriefCommentText.ToString();
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    internal static string? ObsoleteOf(CXCursor c)
    {
        if (c.Availability != CXAvailabilityKind.CXAvailability_Deprecated) return null;
        return "Deprecated in the CUDA headers.";
    }

    static readonly HashSet<string> Keywords =
    [
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const", "continue",
        "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern", "false", "finally",
        "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "override", "params", "private", "protected",
        "public", "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static", "string",
        "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort",
        "using", "virtual", "void", "volatile", "while",
    ];

    internal static string Escape(string name) => Keywords.Contains(name) ? "@" + name : name;
}
