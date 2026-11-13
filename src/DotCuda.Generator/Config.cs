using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotCuda.Generator;

/// <summary>Per-library generator input (config/*.json). The only hand-maintained input to the generated code.</summary>
public sealed class LibraryConfig
{
    /// <summary>Short library name, e.g. "Driver"; used for file names and the ABI test class.</summary>
    public required string Name { get; init; }
    /// <summary>Project directory under src/, e.g. "DotCuda.Cuda12.Driver".</summary>
    public required string Project { get; init; }
    public string Namespace { get; init; } = "DotCuda.Cuda12";
    /// <summary>Static class holding the functions and constants, e.g. "Cu".</summary>
    public required string ClassName { get; init; }
    /// <summary>Top-level header parsed for this library.</summary>
    public required string Header { get; init; }
    /// <summary>Headers whose declarations belong to this library (file names). Defaults to just <see cref="Header"/>.</summary>
    public List<string> HeaderFiles { get; init; } = [];
    public List<string> FunctionPrefixes { get; init; } = [];
    public List<string> TypePrefixes { get; init; } = [];
    public List<string> ConstantPrefixes { get; init; } = [];
    /// <summary>Extra -D defines, used both for parsing and by the ABI test's C program.</summary>
    public List<string> Defines { get; init; } = [];
    /// <summary>
    /// Defines that only switch on annotations the generator reads (e.g. CUDNN_WARN_DEPRECATED for [Obsolete]); used when
    /// parsing, but not in the ABI test's C program, since they can't change layouts and some compilers reject them.
    /// </summary>
    public List<string> AnnotationDefines { get; init; } = [];
    /// <summary>Defines CUDA_API_PER_THREAD_DEFAULT_STREAM, switching stream-ordered calls to their _ptds/_ptsz exports.</summary>
    public bool PerThreadDefaultStream { get; init; }
    /// <summary>Integer typedefs wrapped in a one-field record struct: wrapper name -> other typedef names that alias it.</summary>
    public Dictionary<string, List<string>> IntegerHandles { get; init; } = [];
    /// <summary>
    /// Pointer typedefs to *defined* structs that are bound as nint handles anyway (e.g. cuBLASLt's semi-opaque
    /// descriptors). Pointer typedefs to undefined structs are always handles.
    /// </summary>
    public List<string> PointerHandles { get; init; } = [];
    /// <summary>Typedef names mapped to an explicit C# type, overriding the default (raw 'long'/'unsigned long' map to CLong/CULong).</summary>
    public Dictionary<string, string> LongTypes { get; init; } = [];
    /// <summary>Type renames applied after automatic naming: chosen name -> new name.</summary>
    public Dictionary<string, string> Renames { get; init; } = [];
    /// <summary>Other library configs (by Name) whose emitted types this library reuses instead of re-emitting.</summary>
    public List<string> References { get; init; } = [];
    /// <summary>Typedef names emitted even though no emitted declaration uses them (e.g. enums that are attribute values).</summary>
    public List<string> ExtraTypes { get; init; } = [];
    /// <summary>Functions left out on purpose: name as written -> reason (listed in SKIPPED.md).</summary>
    public Dictionary<string, string> ExcludeFunctions { get; init; } = [];
    /// <summary>
    /// Extra include directories (e.g. the cuDNN SDK). Environment variables are expanded and '*' segments match the
    /// highest-sorting directory, so "%ProgramFiles%\NVIDIA\CUDNN\v9.*\include\12.*" finds the newest install.
    /// Entries that don't resolve to an existing directory are ignored.
    /// </summary>
    public List<string> IncludeDirs { get; init; } = [];
    /// <summary>
    /// Headers parsed together with <see cref="Header"/> (e.g. library_types.h for the driver), so types they declare can
    /// be emitted here through <see cref="ExtraTypes"/> and shared by every library that references this one.
    /// </summary>
    public List<string> ExtraIncludes { get; init; } = [];
    /// <summary>Macros whose values, joined with '.', give the package version (default: CUDA_VERSION).</summary>
    public List<string> PackageVersionMacros { get; init; } = [];
    /// <summary>Macros recorded in the generated banner (e.g. CUBLAS_VER_MAJOR).</summary>
    public List<string> VersionMacros { get; init; } = [];
    public required NativeLibraryConfig Library { get; init; }
    /// <summary>DLLs whose export tables must contain every emitted entry point. Environment variables are expanded; missing files are reported.</summary>
    public List<string> ExportCheck { get; init; } = [];

    [JsonIgnore] public string ConfigPath { get; set; } = "";

    public IEnumerable<string> AllHeaderFiles => HeaderFiles.Count > 0 ? HeaderFiles : [Header];

    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static LibraryConfig Load(string path)
    {
        var cfg = JsonSerializer.Deserialize<LibraryConfig>(File.ReadAllText(path), Options)
                  ?? throw new InvalidDataException($"empty config {path}");
        cfg.ConfigPath = path;
        return cfg;
    }
}

public sealed class NativeLibraryConfig
{
    /// <summary>Name used in [LibraryImport]; the package's resolver maps it to a file.</summary>
    public required string LogicalName { get; init; }
    public required string Windows { get; init; }
    public required string Linux { get; init; }
    /// <summary>Files loaded from the same directory before the library (wildcards allowed; every match is loaded).</summary>
    public List<string> WindowsPreload { get; init; } = [];
    public List<string> LinuxPreload { get; init; } = [];
    /// <summary>Whether DOTCUDA_LIB_DIR and the toolkit directory are searched. False for the driver, which is always the system's.</summary>
    public bool UseLibDir { get; init; } = true;
}
