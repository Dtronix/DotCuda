namespace DotCuda.Generator;

/// <summary>Something the emitter writes. Deps are the keys of other items it needs; an item whose dep is skipped is skipped too.</summary>
public abstract class Item
{
    /// <summary>Clang USR (or a synthetic key for integer handles); unique across libraries.</summary>
    public required string Key { get; init; }
    public required string CsName { get; set; }
    public required string Library { get; init; }
    public string SourceFile { get; init; } = "";
    public string? Doc { get; init; }
    public string? Obsolete { get; init; }
    public HashSet<string> Deps { get; } = [];
    public string? SkipReason { get; set; }
    public bool Skipped => SkipReason is not null;
}

public sealed class EnumItem : Item
{
    public required string Underlying { get; init; }
    /// <summary>How C code names the type (typedef name, or "enum tag").</summary>
    public required string CSpelling { get; init; }
    public List<EnumMember> Members { get; } = [];
}

public sealed record EnumMember(string Name, string Value, long RawValue, string? Doc, string? Obsolete);

public sealed class StructItem : Item
{
    public bool IsUnion { get; init; }
    public long Size { get; init; }
    public long Align { get; init; }
    /// <summary>How C code names the top-level type; null for nested anonymous types.</summary>
    public string? CSpelling { get; init; }
    public List<FieldItem> Fields { get; } = [];
    public List<StructItem> Nested { get; } = [];
    public List<FixedBuffer> Buffers { get; } = [];
}

/// <param name="CsName">C# field name (escaped if needed).</param>
/// <param name="CName">C member name; null for anonymous members (C accesses their fields directly).</param>
/// <param name="Offset">Byte offset in the immediate parent, from clang.</param>
/// <param name="NestedType">The nested anonymous struct/union this field holds, if any.</param>
/// <param name="NestedIsArrayElement">True when the field is a fixed array whose elements are <paramref name="NestedType"/>.</param>
public sealed record FieldItem(string CsName, string CsType, string? CName, long Offset, long Size, string? Doc, StructItem? NestedType, bool NestedIsArrayElement = false);

/// <summary>An [InlineArray] type nested in a struct.</summary>
public sealed record FixedBuffer(string Name, string ElementType, long Length, string? Comment);

public sealed class HandleItem : Item
{
    /// <summary>"nint" for opaque pointer handles; the integer type for wrapped integer typedefs.</summary>
    public required string ValueType { get; init; }
    public required string CDescription { get; init; }
}

public sealed class FunctionItem : Item
{
    public required string EntryPoint { get; init; }
    public string ReturnType { get; set; } = "";
    public List<(string Type, string Name)> Parameters { get; } = [];
}

/// <summary>A macro constant. Emitted as const unless <see cref="StaticReadonly"/>.</summary>
public sealed class ConstantItem : Item
{
    public required string CsType { get; init; }
    public required string Value { get; init; }
    public long? RawValue { get; init; }
    public bool StaticReadonly { get; init; }
}

public sealed record SkippedDecl(string Kind, string Name, string File, string Reason);

/// <summary>Thrown while mapping a declaration that the generator cannot express.</summary>
public sealed class SkipException(string reason) : Exception(reason);

/// <summary>Everything generated for one library.</summary>
public sealed class LibraryModel(LibraryConfig config)
{
    public LibraryConfig Config { get; } = config;
    public Dictionary<string, Item> Items { get; } = [];
    /// <summary>Items in the order they were first reached (header order, then dependencies).</summary>
    public List<Item> Order { get; } = [];
    public List<SkippedDecl> Skipped { get; } = [];
    public Dictionary<string, string> VersionInfo { get; } = [];
    /// <summary>Package version: the CUDA toolkit version, or the library's own (e.g. cuDNN 9.14.0) when configured.</summary>
    public string HeaderVersion { get; set; } = "";
    /// <summary>The CUDA toolkit version of the headers (CUDA_VERSION).</summary>
    public string CudaVersion { get; set; } = "";

    public void Add(Item item)
    {
        Items.Add(item.Key, item);
        Order.Add(item);
    }

    public IEnumerable<T> Emitted<T>() where T : Item => Order.OfType<T>().Where(i => !i.Skipped);
}
