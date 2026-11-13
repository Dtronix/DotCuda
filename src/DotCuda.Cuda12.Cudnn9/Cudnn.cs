#if CUDA13
namespace DotCuda.Cuda13;
#else
namespace DotCuda.Cuda12;
#endif

/// <summary>cuDNN 9 (cudnn.h). The bindings are generated; this file holds the hand-written helpers.</summary>
public static unsafe partial class Cudnn
{
    /// <summary>Throws <see cref="DotCudaException"/> with cuDNN's error string unless <paramref name="status"/> is success.</summary>
    /// <remarks>For backend (graph) API failures, <c>cudnnGetLastErrorString</c> usually has more detail.</remarks>
    public static void Check(cudnnStatus_t status)
    {
        if (status == cudnnStatus_t.CUDNN_STATUS_SUCCESS) return;
        throw new DotCudaException("cuDNN", (int)status, status.ToString(), NativeLibraries.Utf8(cudnnGetErrorString(status)));
    }
}
