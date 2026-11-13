using System.Diagnostics;

namespace DotCuda.AbiTests;

/// <summary>Finds MSVC through vswhere and compiles a C file in a vcvars64 environment.</summary>
sealed class Msvc(string vcvars, string version)
{
    public string Version { get; } = version;

    public static Msvc? Find()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (!File.Exists(vswhere)) return null;
        var (code, stdout, _) = Run(vswhere, "-latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath", null);
        var install = stdout.Trim().Split('\n').FirstOrDefault()?.Trim();
        if (code != 0 || string.IsNullOrEmpty(install)) return null;
        var vcvars = Path.Combine(install, "VC", "Auxiliary", "Build", "vcvars64.bat");
        if (!File.Exists(vcvars)) return null;
        var versionFile = Path.Combine(install, "VC", "Auxiliary", "Build", "Microsoft.VCToolsVersion.default.txt");
        return new Msvc(vcvars, File.Exists(versionFile) ? File.ReadAllText(versionFile).Trim() : "unknown");
    }

    /// <summary>Compiles <paramref name="source"/> as C11 against <paramref name="includes"/>, runs it, and returns its stdout lines.</summary>
    public string[] CompileAndRun(string source, IReadOnlyList<string> includes)
    {
        var dir = Directory.CreateTempSubdirectory("dotcuda-abi-").FullName;
        try
        {
            var exe = Path.Combine(dir, Path.GetFileNameWithoutExtension(source) + ".exe");
            var inc = string.Join(" ", includes.Select(i => $"/I\"{i}\""));
            var cmd = $"/s /c \"call \"{vcvars}\" >nul && cl /nologo /std:c11 /W0 {inc} \"{source}\" /Fe:\"{exe}\" /Fo:\"{dir}\\\\\"\"";
            var (code, stdout, stderr) = Run("cmd.exe", cmd, dir);
            if (code != 0) throw new InvalidOperationException($"cl failed ({code}):\n{stdout}\n{stderr}");
            var (runCode, output, runErr) = Run(exe, "", dir);
            if (runCode != 0) throw new InvalidOperationException($"{exe} exited with {runCode}: {runErr}");
            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    static (int Code, string Stdout, string Stderr) Run(string file, string args, string? cwd)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = cwd ?? Environment.CurrentDirectory,
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return (p.ExitCode, stdout.Result, stderr.Result);
    }
}
