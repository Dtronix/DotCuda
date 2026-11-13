using System.Text;

#if CUDA13
namespace DotCuda.Cuda13;
#else
namespace DotCuda.Cuda12;
#endif

/// <summary>NVRTC (nvrtc.h). The bindings are generated; this file holds the hand-written helpers.</summary>
public static unsafe partial class Nvrtc
{
    /// <summary>Throws <see cref="DotCudaException"/> unless <paramref name="result"/> is success; <paramref name="log"/> (e.g. the compile log) goes in the message.</summary>
    public static void Check(nvrtcResult result, string? log = null)
    {
        if (result == nvrtcResult.NVRTC_SUCCESS) return;
        var name = NativeLibraries.Utf8(nvrtcGetErrorString(result));
        throw new DotCudaException("NVRTC", (int)result, name, log);
    }

    /// <summary>Compiles CUDA C++ to a cubin (or PTX when no <c>-arch=sm_XX</c> is given). Returns the image and the compile log.</summary>
    public static (byte[] image, string log) Compile(string source, string name, params string[] options)
    {
        nvrtcProgram prog;
        using (var src = new Utf8Z(source))
        using (var nm = new Utf8Z(name))
            Check(nvrtcCreateProgram(&prog, src.Ptr, nm.Ptr, 0, null, null));
        try
        {
            var opts = options.Select(o => new Utf8Z(o)).ToArray();
            nvrtcResult r;
            try
            {
                byte** p = stackalloc byte*[opts.Length];
                for (int i = 0; i < opts.Length; i++) p[i] = opts[i].Ptr;
                r = nvrtcCompileProgram(prog, opts.Length, p);
            }
            finally
            {
                foreach (var o in opts) o.Dispose();
            }

            nuint logSize;
            Check(nvrtcGetProgramLogSize(prog, &logSize));
            var logBuf = new byte[(int)logSize];
            fixed (byte* l = logBuf) Check(nvrtcGetProgramLog(prog, l));
            var log = Encoding.UTF8.GetString(logBuf).TrimEnd('\0');
            Check(r, log);

            nuint size;
            byte[] image;
            if (nvrtcGetCUBINSize(prog, &size) == nvrtcResult.NVRTC_SUCCESS && size > 0)
            {
                image = new byte[(int)size];
                fixed (byte* c = image) Check(nvrtcGetCUBIN(prog, c));
            }
            else
            {
                Check(nvrtcGetPTXSize(prog, &size));
                image = new byte[(int)size];
                fixed (byte* c = image) Check(nvrtcGetPTX(prog, c));
            }
            return (image, log);
        }
        finally
        {
            Check(nvrtcDestroyProgram(&prog));
        }
    }
}
