using DotCuda.Cuda12;

namespace DotCuda.SmokeTests;

/// <summary>Device 0's primary context, shared by the tests. Contexts are current per thread, so each test calls <see cref="Use"/>.</summary>
static unsafe class Gpu
{
    static readonly Lazy<(CUdevice Device, CUcontext Context)?> Primary = new(() =>
    {
        try
        {
            if (Cu.cuInit(0) != CUresult.CUDA_SUCCESS) return null;
            int count;
            if (Cu.cuDeviceGetCount(&count) != CUresult.CUDA_SUCCESS || count == 0) return null;
            CUdevice dev;
            Cu.Check(Cu.cuDeviceGet(&dev, 0));
            CUcontext ctx;
            Cu.Check(Cu.cuDevicePrimaryCtxRetain(&ctx, dev));
            return (dev, ctx);
        }
        catch (DllNotFoundException)
        {
            return null;
        }
    });

    /// <summary>Makes the primary context current on this thread; skips the test when there is no CUDA device.</summary>
    public static CUdevice Use()
    {
        if (Primary.Value is not { } p)
        {
            Skip.Test("no CUDA driver or device");
            throw new System.Diagnostics.UnreachableException();
        }
        Cu.Check(Cu.cuCtxSetCurrent(p.Context));
        return p.Device;
    }
}
