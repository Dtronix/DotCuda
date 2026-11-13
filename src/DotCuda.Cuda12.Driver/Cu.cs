namespace DotCuda.Cuda12;

/// <summary>CUDA driver API (cuda.h). The bindings are generated; this file holds the hand-written helpers.</summary>
public static unsafe partial class Cu
{
    /// <summary>Throws <see cref="DotCudaException"/> with the driver's error name and string unless <paramref name="result"/> is success.</summary>
    public static void Check(CUresult result)
    {
        if (result == CUresult.CUDA_SUCCESS) return;
        byte* name = null, description = null;
        cuGetErrorName(result, &name);
        cuGetErrorString(result, &description);
        throw new DotCudaException("CUDA driver", (int)result, NativeLibraries.Utf8(name), NativeLibraries.Utf8(description));
    }

    /// <summary>Allocates <paramref name="bytes"/> of device memory.</summary>
    public static CUdeviceptr Alloc(long bytes)
    {
        CUdeviceptr p;
        Check(cuMemAlloc(&p, (nuint)bytes));
        return p;
    }

    /// <summary>Creates an event with default flags.</summary>
    public static CUevent CreateEvent(uint flags = 0)
    {
        CUevent e;
        Check(cuEventCreate(&e, flags));
        return e;
    }

    /// <summary>Milliseconds between two recorded events.</summary>
    public static float Elapsed(CUevent start, CUevent end)
    {
        float ms;
        Check(cuEventElapsedTime(&ms, start, end));
        return ms;
    }
}

public readonly partial record struct CUstream
{
    /// <summary>The NULL stream (legacy default stream unless per-thread default streams are enabled).</summary>
    public static readonly CUstream Default = new(0);
}

public readonly partial record struct CUdeviceptr
{
    /// <summary>This pointer plus <paramref name="bytes"/>.</summary>
    public CUdeviceptr Offset(long bytes) => new((ulong)((long)Value + bytes));
}
