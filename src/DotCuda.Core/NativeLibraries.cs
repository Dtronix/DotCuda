using System.Reflection;
using System.Runtime.InteropServices;

namespace DotCuda;

/// <summary>Per-platform file names for one logical library name used in <c>[LibraryImport]</c>.</summary>
/// <param name="LogicalName">The name the bindings import from, e.g. "nvrtc".</param>
/// <param name="WindowsFile">File name on Windows, e.g. "nvrtc64_120_0.dll".</param>
/// <param name="LinuxFile">File name on Linux, e.g. "libnvrtc.so.12".</param>
/// <param name="WindowsPreload">Files loaded from the same directory first, when loading from a directory (wildcards allowed; every match is loaded).</param>
/// <param name="LinuxPreload">As <paramref name="WindowsPreload"/>, on Linux.</param>
/// <param name="UseLibDir">Whether <c>DOTCUDA_LIB_DIR</c> and the toolkit directory are searched. False for the driver.</param>
public sealed record NativeLibrarySpec(
    string LogicalName, string WindowsFile, string LinuxFile, string[] WindowsPreload, string[] LinuxPreload, bool UseLibDir)
{
    /// <summary>The file name for the current OS.</summary>
    public string File => OperatingSystem.IsWindows() ? WindowsFile : LinuxFile;

    /// <summary>Preload patterns for the current OS.</summary>
    public string[] Preload => OperatingSystem.IsWindows() ? WindowsPreload : LinuxPreload;
}

/// <summary>
/// Resolves the logical library names used by the DotCuda bindings. Each binding assembly registers its libraries
/// from a module initializer. Search order for toolkit libraries (NVRTC, cuBLASLt, ...):
/// <list type="number">
/// <item><c>DOTCUDA_LIB_DIR</c>, if set (for example PyTorch's <c>torch/lib</c>);</item>
/// <item>the normal native library search (application directory, <c>PATH</c> / <c>LD_LIBRARY_PATH</c>);</item>
/// <item>the toolkit: <c>%CUDA_PATH%\bin</c> on Windows, <c>$CUDA_PATH/lib64</c> or <c>/usr/local/cuda/lib64</c> on Linux.</item>
/// </list>
/// The driver library always comes from the normal search, since it must match the installed driver.
/// </summary>
public static class NativeLibraries
{
    /// <summary>Environment variable naming a directory searched first for toolkit libraries.</summary>
    public const string LibDirVariable = "DOTCUDA_LIB_DIR";

    static readonly Dictionary<string, NativeLibrarySpec> Specs = new();
    static readonly Dictionary<string, (nint Handle, string Description)> Loaded = new();

    /// <summary>Registers <paramref name="specs"/> and installs the resolver for <paramref name="assembly"/>. Called once per binding assembly.</summary>
    public static void Register(Assembly assembly, params NativeLibrarySpec[] specs)
    {
        lock (Specs)
            foreach (var s in specs)
                Specs[s.LogicalName] = s;
        NativeLibrary.SetDllImportResolver(assembly, (name, asm, path) =>
        {
            NativeLibrarySpec? spec;
            lock (Specs) Specs.TryGetValue(name, out spec);
            return spec is null ? 0 : Load(spec, asm, path);
        });
    }

    /// <summary>Loads (once) and returns the native handle for a registered logical library.</summary>
    public static nint Load(string logicalName)
    {
        NativeLibrarySpec? spec;
        lock (Specs) Specs.TryGetValue(logicalName, out spec);
        if (spec is null) throw new DllNotFoundException($"DotCuda: no library registered as '{logicalName}'");
        return Load(spec, null, null);
    }

    /// <summary>Where a library was loaded from (or would be), for logging which build is in use.</summary>
    public static string Describe(string logicalName)
    {
        lock (Loaded)
            if (Loaded.TryGetValue(logicalName, out var l)) return l.Description;
        try
        {
            Load(logicalName);
            lock (Loaded) return Loaded[logicalName].Description;
        }
        catch (Exception e) when (e is DllNotFoundException or BadImageFormatException)
        {
            return $"{logicalName}: not loaded ({e.Message})";
        }
    }

    static nint Load(NativeLibrarySpec spec, Assembly? asm, DllImportSearchPath? path)
    {
        lock (Loaded)
        {
            if (Loaded.TryGetValue(spec.LogicalName, out var existing)) return existing.Handle;
            var file = spec.File;
            var attempts = new List<string>();

            if (spec.UseLibDir && Environment.GetEnvironmentVariable(LibDirVariable) is { Length: > 0 } dir)
                if (TryLoadFrom(spec, dir, out var h)) return Remember(spec, h, Path.Combine(dir, file));
                else attempts.Add(Path.Combine(dir, file));

            if (asm is not null ? NativeLibrary.TryLoad(file, asm, path, out var handle) : NativeLibrary.TryLoad(file, out handle))
                return Remember(spec, handle, file + " (system search path)");
            attempts.Add(file + " (system search path)");

            if (spec.UseLibDir)
                foreach (var toolkit in ToolkitDirectories())
                    if (TryLoadFrom(spec, toolkit, out var h)) return Remember(spec, h, Path.Combine(toolkit, file));
                    else attempts.Add(Path.Combine(toolkit, file));

            throw new DllNotFoundException($"DotCuda: could not load '{spec.LogicalName}'. Tried: {string.Join("; ", attempts)}");
        }
    }

    static nint Remember(NativeLibrarySpec spec, nint handle, string description)
    {
        Loaded[spec.LogicalName] = (handle, description);
        return handle;
    }

    static bool TryLoadFrom(NativeLibrarySpec spec, string dir, out nint handle)
    {
        handle = 0;
        var full = Path.Combine(dir, spec.File);
        if (!File.Exists(full)) return false;
        // e.g. NVRTC loads its builtins library by bare name at compile time; preloading it by full path
        // makes that lookup succeed whatever the process's search path is.
        // e.g. cuDNN's shim loads its seven sub-libraries by bare name; every match of every pattern is loaded.
        foreach (var pattern in spec.Preload)
            if (Directory.Exists(dir))
                foreach (var match in Directory.GetFiles(dir, pattern).OrderBy(f => f, StringComparer.Ordinal))
                    NativeLibrary.TryLoad(match, out _);
        return NativeLibrary.TryLoad(full, out handle);
    }

    static IEnumerable<string> ToolkitDirectories()
    {
        var cudaPath = Environment.GetEnvironmentVariable("CUDA_PATH");
        if (OperatingSystem.IsWindows())
        {
            if (cudaPath is not null) yield return Path.Combine(cudaPath, "bin");
        }
        else
        {
            if (cudaPath is not null) yield return Path.Combine(cudaPath, "lib64");
            yield return "/usr/local/cuda/lib64";
        }
    }

    /// <summary>Reads a null-terminated UTF-8 string; null for a null pointer.</summary>
    public static unsafe string? Utf8(byte* p) => p == null ? null : Marshal.PtrToStringUTF8((nint)p);
}
