using DotCuda.Generator;

// Usage: DotCuda.Generator [--include DIR] [--root DIR] [--export-dir DIR]... [--no-export-check]
//   --include      CUDA include directory (default: %CUDA_PATH%\include)
//   --root         repository root (default: nearest parent directory containing DotCuda.slnx)
//   --export-dir   extra directory holding the libraries' DLLs to check exports against (repeatable;
//                  %CUDA_PATH%\bin and DOTCUDA_LIB_DIR are always checked when present)

string? include = null, root = null, outRoot = null, target = "windows";
var exportDirs = new List<string>();
bool exportCheck = true;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--include": include = args[++i]; break;
        case "--root": root = args[++i]; break;
        case "--export-dir": exportDirs.Add(args[++i]); break;
        case "--no-export-check": exportCheck = false; break;
        case "--target": target = args[++i]; break;
        case "--out": outRoot = args[++i]; break;
        default: Console.Error.WriteLine($"unknown argument {args[i]}"); return 2;
    }
}

var cudaPath = Environment.GetEnvironmentVariable("CUDA_PATH");
include ??= cudaPath is null ? null : Path.Combine(cudaPath, "include");
if (include is null || !File.Exists(Path.Combine(include, "cuda.h")))
{
    Console.Error.WriteLine("CUDA headers not found; pass --include or set CUDA_PATH.");
    return 2;
}
root ??= FindRoot(Environment.CurrentDirectory) ?? FindRoot(AppContext.BaseDirectory)
    ?? throw new InvalidOperationException("repository root (DotCuda.slnx) not found; pass --root");
if (cudaPath is not null) exportDirs.Add(Path.Combine(cudaPath, "bin"));
if (Environment.GetEnvironmentVariable("DOTCUDA_LIB_DIR") is { Length: > 0 } libDir) exportDirs.Add(libDir);

switch (target)
{
    case "windows": break;
    case "linux":
        ModelBuilder.TargetArgs = ["--target=x86_64-unknown-linux-gnu", "-nostdinc", $"-isystem{Path.Combine(root, "tools", "linux-headers")}"];
        exportCheck = false;
        break;
    default: Console.Error.WriteLine($"unknown target {target} (windows, linux)"); return 2;
}
outRoot ??= root;

var configs = Directory.GetFiles(Path.Combine(root, "config"), "*.json").Select(LibraryConfig.Load).ToList();
var registry = new Registry();
int failures = 0;

foreach (var cfg in TopologicalOrder(configs))
{
    Console.WriteLine($"== {cfg.Name}: {cfg.Header}");
    var model = new ModelBuilder(cfg, registry, include).Build();
    registry.Libraries[cfg.Name] = model;

    var outDir = Path.Combine(outRoot, "src", cfg.Project, "Generated");
    var refNamespaces = cfg.References.Select(r => registry.Libraries[r].Config.Namespace).ToList();
    new CSharpEmitter(model, outDir, refNamespaces).Emit();
    new AbiEmitter(model, Path.Combine(outRoot, "tests", "DotCuda.AbiTests", "Generated")).Emit();

    var version = model.HeaderVersion == model.CudaVersion ? $"CUDA {model.CudaVersion}" : $"{model.HeaderVersion} (CUDA {model.CudaVersion} headers)";
    Console.WriteLine($"   {version}: {model.Emitted<FunctionItem>().Count()} functions, {model.Emitted<EnumItem>().Count()} enums, " +
                      $"{model.Emitted<StructItem>().Count()} structs, {model.Emitted<HandleItem>().Count()} handles, " +
                      $"{model.Emitted<ConstantItem>().Count()} constants; {model.Skipped.Count} skipped");
    Console.WriteLine($"   remapped entry points: {model.Emitted<FunctionItem>().Count(f => f.EntryPoint != f.CsName)}");

    if (!exportCheck) continue;
    // Entries may use wildcards in directory names (resolved like includeDirs); unresolvable ones are reported as not found.
    var dlls = cfg.ExportCheck.Select(e => ModelBuilder.ResolveDirectory(Path.GetDirectoryName(e)!) is { } d
        ? Path.Combine(d, Path.GetFileName(e)) : Environment.ExpandEnvironmentVariables(e)).ToList();
    if (cfg.Library.UseLibDir) dlls.AddRange(exportDirs.Select(d => Path.Combine(d, cfg.Library.Windows)));
    foreach (var dll in dlls.Distinct(StringComparer.OrdinalIgnoreCase))
    {
        if (!File.Exists(dll)) { Console.WriteLine($"   exports: {dll} not found, not checked"); continue; }
        var missing = ExportCheck.Missing(model, dll);
        if (missing.Count == 0) Console.WriteLine($"   exports: all {model.Emitted<FunctionItem>().Count()} entry points found in {dll}");
        else
        {
            failures++;
            Console.Error.WriteLine($"   ERROR: {missing.Count} entry points missing from {dll}: {string.Join(", ", missing)}");
        }
    }
}
return failures == 0 ? 0 : 1;

static string? FindRoot(string start)
{
    for (var d = new DirectoryInfo(start); d is not null; d = d.Parent)
        if (File.Exists(Path.Combine(d.FullName, "DotCuda.slnx"))) return d.FullName;
    return null;
}

static IEnumerable<LibraryConfig> TopologicalOrder(List<LibraryConfig> configs)
{
    var byName = configs.ToDictionary(c => c.Name);
    var done = new HashSet<string>();
    var result = new List<LibraryConfig>();
    void Visit(LibraryConfig c, HashSet<string> stack)
    {
        if (done.Contains(c.Name)) return;
        if (!stack.Add(c.Name)) throw new InvalidOperationException($"reference cycle at {c.Name}");
        foreach (var r in c.References)
            Visit(byName.TryGetValue(r, out var rc) ? rc : throw new InvalidOperationException($"{c.Name} references unknown library {r}"), stack);
        done.Add(c.Name);
        result.Add(c);
    }
    foreach (var c in configs.OrderBy(c => c.Name)) Visit(c, []);
    return result;
}
