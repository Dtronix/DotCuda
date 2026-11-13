using DotCuda.Cuda12;
using DotCuda.Cudnn9;

namespace DotCuda.SmokeTests;

/// <summary>nvJitLink (CPU only: NVRTC LTO-IR linked into a cubin) and cuDNN (handle + version; uses the GPU).</summary>
public class LinkAndCudnnTests
{
    /// <summary>Compiles a kernel to LTO-IR with NVRTC (-dlto) and links it with nvJitLink. Needs no GPU.</summary>
    [Test]
    public async Task NvJitLinkLinksNvrtcLtoIr()
    {
        (int cubinSize, (uint Major, uint Minor) version) result = default;
        try
        {
            result = LinkLtoIr();
        }
        catch (DllNotFoundException e)
        {
            Skip.Test($"NVRTC / nvJitLink not installed: {e.Message}");
        }
        var (cubinSize, version) = result;
        await Assert.That(cubinSize).IsGreaterThan(0);
        await Assert.That(version.Major).IsGreaterThanOrEqualTo(12u);
    }

    static unsafe (int CubinSize, (uint Major, uint Minor) Version) LinkLtoIr()
    {
        uint major, minor;
        NvJitLink.Check(NvJitLink.nvJitLinkVersion(&major, &minor));

        const string src = """extern "C" __global__ void scale(float* y, float a, int n) { int i = blockIdx.x * blockDim.x + threadIdx.x; if (i < n) y[i] *= a; }""";
        nvrtcProgram prog;
        using (var s = new Utf8Z(src))
        using (var n = new Utf8Z("scale.cu"))
            Nvrtc.Check(Nvrtc.nvrtcCreateProgram(&prog, s.Ptr, n.Ptr, 0, null, null));
        byte[] ltoir;
        try
        {
            using var arch = new Utf8Z("-arch=sm_86");
            using var dlto = new Utf8Z("-dlto");
            byte** opts = stackalloc byte*[] { arch.Ptr, dlto.Ptr };
            Nvrtc.Check(Nvrtc.nvrtcCompileProgram(prog, 2, opts));
            nuint size;
            Nvrtc.Check(Nvrtc.nvrtcGetLTOIRSize(prog, &size));
            ltoir = new byte[(int)size];
            fixed (byte* p = ltoir) Nvrtc.Check(Nvrtc.nvrtcGetLTOIR(prog, p));
        }
        finally
        {
            Nvrtc.Check(Nvrtc.nvrtcDestroyProgram(&prog));
        }

        nvJitLinkHandle link;
        using (var arch = new Utf8Z("-arch=sm_86"))
        using (var lto = new Utf8Z("-lto"))
        {
            byte** opts = stackalloc byte*[] { arch.Ptr, lto.Ptr };
            NvJitLink.Check(NvJitLink.nvJitLinkCreate(&link, 2, opts));
        }
        try
        {
            using var name = new Utf8Z("scale.ltoir");
            fixed (byte* p = ltoir)
                NvJitLink.Check(NvJitLink.nvJitLinkAddData(link, nvJitLinkInputType.NVJITLINK_INPUT_LTOIR, p, (nuint)ltoir.Length, name.Ptr), link);
            NvJitLink.Check(NvJitLink.nvJitLinkComplete(link), link);
            nuint cubinSize;
            NvJitLink.Check(NvJitLink.nvJitLinkGetLinkedCubinSize(link, &cubinSize), link);
            var cubin = new byte[(int)cubinSize];
            fixed (byte* p = cubin) NvJitLink.Check(NvJitLink.nvJitLinkGetLinkedCubin(link, p), link);
            Console.WriteLine($"nvJitLink {major}.{minor} ({NativeLibraries.Describe(NvJitLink.LibraryName)}): {ltoir.Length} bytes LTO-IR -> {cubin.Length} byte cubin");
            return (cubin.Length, (major, minor));
        }
        finally
        {
            NvJitLink.Check(NvJitLink.nvJitLinkDestroy(&link));
        }
    }

    /// <summary>Creates a cuDNN handle on device 0 and binds it to the default stream. Uses the GPU.</summary>
    [Test]
    public async Task CudnnCreateHandle()
    {
        Gpu.Use();
        var version = CreateHandle();
        await Assert.That(version / 10000).IsEqualTo((nuint)9);
    }

    static unsafe nuint CreateHandle()
    {
        var version = Cudnn.cudnnGetVersion();
        cudnnHandle_t handle;
        Cudnn.Check(Cudnn.cudnnCreate(&handle));
        try
        {
            Cudnn.Check(Cudnn.cudnnSetStream(handle, CUstream.Default));
            Console.WriteLine($"cuDNN {version} ({NativeLibraries.Describe(Cudnn.LibraryName)})");
        }
        finally
        {
            Cudnn.Check(Cudnn.cudnnDestroy(handle));
        }
        return version;
    }
}
