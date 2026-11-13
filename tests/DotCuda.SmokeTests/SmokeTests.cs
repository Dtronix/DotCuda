using DotCuda.Cuda12;

namespace DotCuda.SmokeTests;

/// <summary>
/// One real call path per package, on device 0. Skipped when there is no CUDA device.
/// The GPU work lives in synchronous unsafe helpers (async methods can't take addresses of locals); tests assert on the results.
/// </summary>
public class SmokeTests
{
    [Test]
    public async Task DriverInitAndDeviceQuery()
    {
        var (version, sm) = QueryDevice();
        await Assert.That(version).IsGreaterThanOrEqualTo(12000);
        await Assert.That(sm).IsGreaterThan(0);
    }

    static unsafe (int Version, int SmCount) QueryDevice()
    {
        var dev = Gpu.Use();
        int version, sm, major, minor;
        Cu.Check(Cu.cuDriverGetVersion(&version));
        Cu.Check(Cu.cuDeviceGetAttribute(&sm, CUdevice_attribute.CU_DEVICE_ATTRIBUTE_MULTIPROCESSOR_COUNT, dev));
        Cu.Check(Cu.cuDeviceGetAttribute(&major, CUdevice_attribute.CU_DEVICE_ATTRIBUTE_COMPUTE_CAPABILITY_MAJOR, dev));
        Cu.Check(Cu.cuDeviceGetAttribute(&minor, CUdevice_attribute.CU_DEVICE_ATTRIBUTE_COMPUTE_CAPABILITY_MINOR, dev));
        byte* name = stackalloc byte[256];
        Cu.Check(Cu.cuDeviceGetName(name, 256, dev));
        Console.WriteLine($"{NativeLibraries.Utf8(name)}: driver API {version}, {sm} SMs, sm_{major}{minor}; {NativeLibraries.Describe(Cu.LibraryName)}");
        return (version, sm);
    }

    [Test]
    public async Task ErrorsBecomeExceptionsWithDriverNames()
    {
        Gpu.Use();
        var ex = Assert.Throws<DotCudaException>(() => Cu.Check(Cu.cuMemFree(new CUdeviceptr(0xdead_0000))));
        Console.WriteLine(ex.Message);
        await Assert.That(ex.StatusName).IsNotNull();
        await Assert.That(ex.StatusName!).StartsWith("CUDA_ERROR_");
    }

    [Test]
    public async Task AllocCopyRoundTrip()
    {
        var (expected, actual) = RoundTrip();
        await Assert.That(actual.AsSpan().SequenceEqual(expected)).IsTrue();
    }

    static unsafe (byte[] Expected, byte[] Actual) RoundTrip()
    {
        Gpu.Use();
        const int n = 1 << 20;
        var host = new byte[n];
        new Random(1).NextBytes(host);
        var back = new byte[n];
        var d = Cu.Alloc(n);
        try
        {
            fixed (byte* p = host) Cu.Check(Cu.cuMemcpyHtoD(d, p, n));
            Cu.Check(Cu.cuMemsetD8(d.Offset(16), 0xAB, 16));
            fixed (byte* p = back) Cu.Check(Cu.cuMemcpyDtoH(p, d, n));
        }
        finally
        {
            Cu.Check(Cu.cuMemFree(d));
        }
        for (int i = 16; i < 32; i++) host[i] = 0xAB;
        return (host, back);
    }

    [Test]
    public async Task NvrtcCompileAndLaunch()
    {
        var (x, y) = CompileAndLaunch();
        await Assert.That(Enumerable.Range(0, x.Length).All(i => y[i] == 2 * x[i] + 0.5f)).IsTrue();
    }

    static unsafe (float[] X, float[] Y) CompileAndLaunch()
    {
        var dev = Gpu.Use();
        int major, minor, nvMajor, nvMinor;
        Cu.Check(Cu.cuDeviceGetAttribute(&major, CUdevice_attribute.CU_DEVICE_ATTRIBUTE_COMPUTE_CAPABILITY_MAJOR, dev));
        Cu.Check(Cu.cuDeviceGetAttribute(&minor, CUdevice_attribute.CU_DEVICE_ATTRIBUTE_COMPUTE_CAPABILITY_MINOR, dev));
        Nvrtc.Check(Nvrtc.nvrtcVersion(&nvMajor, &nvMinor));

        const string src = """
            extern "C" __global__ void axpb(float* y, const float* x, float a, float b, int n)
            {
                int i = blockIdx.x * blockDim.x + threadIdx.x;
                if (i < n) y[i] = a * x[i] + b;
            }
            """;
        var (cubin, log) = Nvrtc.Compile(src, "axpb.cu", $"-arch=sm_{major}{minor}");
        Console.WriteLine($"NVRTC {nvMajor}.{nvMinor} ({NativeLibraries.Describe(Nvrtc.LibraryName)}): {cubin.Length} byte cubin for sm_{major}{minor}. {log}");

        CUmodule module;
        fixed (byte* p = cubin) Cu.Check(Cu.cuModuleLoadData(&module, p));
        const int n = 10_000;
        var x = Enumerable.Range(0, n).Select(i => (float)i).ToArray();
        var y = new float[n];
        var dx = Cu.Alloc(n * 4);
        var dy = Cu.Alloc(n * 4);
        try
        {
            CUfunction fn;
            using (var name = new Utf8Z("axpb")) Cu.Check(Cu.cuModuleGetFunction(&fn, module, name.Ptr));
            fixed (float* px = x) Cu.Check(Cu.cuMemcpyHtoD(dx, px, n * 4));

            float a = 2, b = 0.5f;
            int count = n;
            void** args = stackalloc void*[] { &dy, &dx, &a, &b, &count };
            Cu.Check(Cu.cuLaunchKernel(fn, (n + 255) / 256, 1, 1, 256, 1, 1, 0, CUstream.Default, args, null));
            Cu.Check(Cu.cuCtxSynchronize());
            fixed (float* py = y) Cu.Check(Cu.cuMemcpyDtoH(py, dy, n * 4));
        }
        finally
        {
            Cu.Check(Cu.cuMemFree(dx));
            Cu.Check(Cu.cuMemFree(dy));
            Cu.Check(Cu.cuModuleUnload(module));
        }
        return (x, y);
    }

    [Test]
    public async Task CublasLtMatmulMatchesCpu()
    {
        var (found, relErr) = Matmul();
        await Assert.That(found).IsEqualTo(1);
        await Assert.That(relErr).IsLessThan(1e-5);
    }

    static unsafe (int Found, double RelErr) Matmul()
    {
        Gpu.Use();
        const int M = 96, N = 80, K = 64; // row-major D[M,N] = A[M,K] * B[K,N]
        var rng = new Random(2);
        var A = Enumerable.Range(0, M * K).Select(_ => (float)rng.NextDouble() - 0.5f).ToArray();
        var B = Enumerable.Range(0, K * N).Select(_ => (float)rng.NextDouble() - 0.5f).ToArray();
        var D = new float[M * N];

        cublasLtHandle_t lt;
        CublasLt.Check(CublasLt.cublasLtCreate(&lt));
        Console.WriteLine($"cuBLASLt {CublasLt.cublasLtGetVersion()} ({NativeLibraries.Describe(CublasLt.LibraryName)})");
        const long workspaceBytes = 4 << 20;
        var dA = Cu.Alloc(A.Length * 4);
        var dB = Cu.Alloc(B.Length * 4);
        var dD = Cu.Alloc(D.Length * 4);
        var ws = Cu.Alloc(workspaceBytes);
        int found;
        try
        {
            fixed (float* p = A) Cu.Check(Cu.cuMemcpyHtoD(dA, p, (nuint)A.Length * 4));
            fixed (float* p = B) Cu.Check(Cu.cuMemcpyHtoD(dB, p, (nuint)B.Length * 4));

            // Column-major view: D^T[N,M] = B^T[N,K] * A^T[K,M], so cuBLASLt's A is our B and its B is our A.
            cublasLtMatmulDesc_t desc;
            CublasLt.Check(CublasLt.cublasLtMatmulDescCreate(&desc, cublasComputeType_t.CUBLAS_COMPUTE_32F, cudaDataType_t.CUDA_R_32F));
            cublasLtMatrixLayout_t la, lb, ld;
            CublasLt.Check(CublasLt.cublasLtMatrixLayoutCreate(&la, cudaDataType_t.CUDA_R_32F, N, K, N));
            CublasLt.Check(CublasLt.cublasLtMatrixLayoutCreate(&lb, cudaDataType_t.CUDA_R_32F, K, M, K));
            CublasLt.Check(CublasLt.cublasLtMatrixLayoutCreate(&ld, cudaDataType_t.CUDA_R_32F, N, M, N));
            cublasLtMatmulPreference_t pref;
            CublasLt.Check(CublasLt.cublasLtMatmulPreferenceCreate(&pref));
            CublasLt.Set(pref, cublasLtMatmulPreferenceAttributes_t.CUBLASLT_MATMUL_PREF_MAX_WORKSPACE_BYTES, (ulong)workspaceBytes);

            cublasLtMatmulHeuristicResult_t result;
            CublasLt.Check(CublasLt.cublasLtMatmulAlgoGetHeuristic(lt, desc, la, lb, ld, ld, pref, 1, &result, &found));
            if (found == 1)
            {
                float alpha = 1, beta = 0;
                CublasLt.Check(CublasLt.cublasLtMatmul(lt, desc, &alpha, (void*)dB.Value, la, (void*)dA.Value, lb, &beta,
                    (void*)dD.Value, ld, (void*)dD.Value, ld, &result.algo, (void*)ws.Value, (nuint)workspaceBytes, CUstream.Default));
                Cu.Check(Cu.cuCtxSynchronize());
                fixed (float* p = D) Cu.Check(Cu.cuMemcpyDtoH(p, dD, (nuint)D.Length * 4));
            }
            CublasLt.Check(CublasLt.cublasLtMatmulPreferenceDestroy(pref));
            CublasLt.Check(CublasLt.cublasLtMatrixLayoutDestroy(la));
            CublasLt.Check(CublasLt.cublasLtMatrixLayoutDestroy(lb));
            CublasLt.Check(CublasLt.cublasLtMatrixLayoutDestroy(ld));
            CublasLt.Check(CublasLt.cublasLtMatmulDescDestroy(desc));
        }
        finally
        {
            Cu.Check(Cu.cuMemFree(dA));
            Cu.Check(Cu.cuMemFree(dB));
            Cu.Check(Cu.cuMemFree(dD));
            Cu.Check(Cu.cuMemFree(ws));
            CublasLt.Check(CublasLt.cublasLtDestroy(lt));
        }

        double maxErr = 0, maxRef = 0;
        for (int i = 0; i < M; i++)
            for (int j = 0; j < N; j++)
            {
                double r = 0;
                for (int k = 0; k < K; k++) r += (double)A[i * K + k] * B[k * N + j];
                maxErr = Math.Max(maxErr, Math.Abs(D[i * N + j] - r));
                maxRef = Math.Max(maxRef, Math.Abs(r));
            }
        Console.WriteLine($"max |D - ref| / max |ref| = {maxErr / maxRef:E2}");
        return (found, maxErr / maxRef);
    }
}
