using System.Text.Json;

namespace DotCuda.AbiTests;

/// <summary>One layout fact: the C# value measured at run time and clang's value recorded by the generator.</summary>
public readonly record struct AbiFact(string Key, long CSharp, long Clang);

/// <summary>
/// Every generated struct's size and field offsets, every enum constant and every integer macro constant must agree
/// three ways: the C# layout, clang's layout (recorded at generation), and MSVC compiling the same header.
/// When MSVC is not installed the test checks C# against clang only and says so.
/// </summary>
public class AbiTests
{
    public static IEnumerable<string> Libraries() =>
        ["Driver", "Nvrtc", "CublasLt", "Nvml", "NvJitLink", "Cudnn", "Driver13", "Nvrtc13", "NvJitLink13", "CublasLt13", "Nvml13", "Cudnn13"];

    static List<AbiFact> Facts(string library)
    {
        var facts = new List<AbiFact>();
        switch (library)
        {
            case "Driver": AbiExpected.CollectDriver(facts); break;
            case "Nvrtc": AbiExpected.CollectNvrtc(facts); break;
            case "CublasLt": AbiExpected.CollectCublasLt(facts); break;
            case "Nvml": AbiExpected.CollectNvml(facts); break;
            case "NvJitLink": AbiExpected.CollectNvJitLink(facts); break;
            case "Cudnn": AbiExpected.CollectCudnn(facts); break;
            case "Driver13": AbiExpected.CollectDriver13(facts); break;
            case "Nvrtc13": AbiExpected.CollectNvrtc13(facts); break;
            case "NvJitLink13": AbiExpected.CollectNvJitLink13(facts); break;
            case "CublasLt13": AbiExpected.CollectCublasLt13(facts); break;
            case "Nvml13": AbiExpected.CollectNvml13(facts); break;
            case "Cudnn13": AbiExpected.CollectCudnn13(facts); break;
            default: throw new ArgumentException(library);
        }
        return facts;
    }

    [Test]
    [MethodDataSource(nameof(Libraries))]
    public async Task CSharpLayoutMatchesClang(string library)
    {
        var facts = Facts(library);
        var wrong = facts.Where(f => f.CSharp != f.Clang).Select(f => $"{f.Key}: C# {f.CSharp}, clang {f.Clang}").ToList();
        Console.WriteLine($"{library}: {facts.Count} facts ({Count(facts, "size:")} sizes, {Count(facts, "off:")} offsets, " +
                         $"{Count(facts, "enum:")} enum values, {Count(facts, "const:")} constants)");
        foreach (var w in wrong.Take(50)) Console.WriteLine("MISMATCH " + w);
        await Assert.That(wrong.Count).IsEqualTo(0);
    }

    [Test]
    [MethodDataSource(nameof(Libraries))]
    public async Task MsvcMatchesClang(string library)
    {
        // C#/clang agreement is always checked by CSharpLayoutMatchesClang; this adds MSVC where it and the headers exist
        // (CI runners have MSVC on Windows but no CUDA headers).
        var compiler = Msvc.Find();
        if (compiler is null) Skip.Test("MSVC not found (vswhere); C#/clang agreement is still checked by CSharpLayoutMatchesClang.");
        // Every library's headers include CUDA toolkit headers (cuDNN's include cuda_runtime_api.h), so the toolkit comes first.
        var cudaPath = Environment.GetEnvironmentVariable("CUDA_PATH");
        if (cudaPath is null || !File.Exists(Path.Combine(cudaPath, "include", "cuda.h")))
            Skip.Test("CUDA toolkit headers not found (CUDA_PATH); C#/clang agreement is still checked by CSharpLayoutMatchesClang.");
        var source = Path.Combine(AppContext.BaseDirectory, "Generated", $"abi_{library.ToLowerInvariant()}.c");
        // Extra include directories from the library's config (e.g. the cuDNN SDK), resolved the way the generator does.
        var extraFile = Path.ChangeExtension(source, ".includes");
        var extra = File.Exists(extraFile) ? File.ReadAllLines(extraFile).Select(IncludeDirs.Resolve).OfType<string>().ToList() : [];
        List<string> includes = [.. extra, Path.Combine(cudaPath!, "include")];
        var headers = File.ReadLines(source).Select(l => System.Text.RegularExpressions.Regex.Match(l, "^#include \"(.+)\"$"))
            .Where(m => m.Success).Select(m => m.Groups[1].Value).ToList();
        var missing = headers.Where(h => !includes.Any(d => File.Exists(Path.Combine(d, h)))).ToList();
        if (missing.Count > 0)
            Skip.Test($"headers not found ({string.Join(", ", missing)}); set CUDA_PATH (and install the cuDNN SDK) to compare with MSVC.");
        var lines = compiler!.CompileAndRun(source, includes);

        var msvc = new Dictionary<string, long>();
        foreach (var line in lines)
        {
            using var doc = JsonDocument.Parse(line);
            msvc[doc.RootElement.GetProperty("k").GetString()!] = doc.RootElement.GetProperty("v").GetInt64();
        }

        var facts = Facts(library).ToDictionary(f => f.Key);
        var wrong = new List<string>();
        foreach (var (key, value) in msvc)
        {
            if (!facts.TryGetValue(key, out var f)) wrong.Add($"{key}: printed by the C program but not in AbiExpected");
            else if (f.Clang != value) wrong.Add($"{key}: MSVC {value}, clang {f.Clang}, C# {f.CSharp}");
        }
        var notInC = facts.Keys.Where(k => !msvc.ContainsKey(k)).ToList();
        Console.WriteLine($"{library}: MSVC {compiler.Version} checked {msvc.Count} of {facts.Count} facts.");
        if (notInC.Count > 0)
            Console.WriteLine($"Not expressible in C (sizes of anonymous members), clang/C# only: {string.Join(", ", notInC)}");
        foreach (var w in wrong.Take(50)) Console.WriteLine("MISMATCH " + w);
        // Only sizes of anonymous members may be missing from the C program.
        await Assert.That(notInC.Count(k => !k.StartsWith("size:"))).IsEqualTo(0);
        await Assert.That(wrong.Count).IsEqualTo(0);
    }

    static int Count(List<AbiFact> facts, string prefix) => facts.Count(f => f.Key.StartsWith(prefix));
}
