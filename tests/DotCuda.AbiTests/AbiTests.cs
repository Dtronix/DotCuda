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
    public static IEnumerable<string> Libraries() => ["Driver", "Nvrtc", "CublasLt", "Nvml", "NvJitLink", "Cudnn"];

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
        var compiler = Msvc.Find();
        if (compiler is null)
        {
            Console.WriteLine("MSVC not found (vswhere); C#/clang agreement is checked by CSharpLayoutMatchesClang only.");
            return;
        }
        var include = Path.Combine(Environment.GetEnvironmentVariable("CUDA_PATH") ?? throw new InvalidOperationException("CUDA_PATH is not set"), "include");
        var source = Path.Combine(AppContext.BaseDirectory, "Generated", $"abi_{library.ToLowerInvariant()}.c");
        // Extra include directories from the library's config (e.g. the cuDNN SDK), resolved the way the generator does.
        var extraFile = Path.ChangeExtension(source, ".includes");
        var extra = File.Exists(extraFile) ? File.ReadAllLines(extraFile).Select(IncludeDirs.Resolve).OfType<string>().ToList() : [];
        var lines = compiler.CompileAndRun(source, [.. extra, include]);

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
