namespace DotCuda.Cuda12;

/// <summary>NVML (nvml.h). The bindings are generated; this file holds the hand-written helpers.</summary>
public static unsafe partial class Nvml
{
    /// <summary>Throws <see cref="DotCudaException"/> with NVML's error string unless <paramref name="result"/> is success.</summary>
    public static void Check(nvmlReturn_t result)
    {
        if (result == nvmlReturn_t.NVML_SUCCESS) return;
        throw new DotCudaException("NVML", (int)result, result.ToString(), NativeLibraries.Utf8(nvmlErrorString(result)));
    }
}
