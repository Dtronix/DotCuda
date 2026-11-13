#if CUDA13
namespace DotCuda.Cuda13;
#else
namespace DotCuda.Cuda12;
#endif

/// <summary>cuBLASLt (cublasLt.h). The bindings are generated; this file holds the hand-written helpers.</summary>
public static unsafe partial class CublasLt
{
    /// <summary>Throws <see cref="DotCudaException"/> with cuBLASLt's status name and string unless <paramref name="status"/> is success.</summary>
    public static void Check(cublasStatus_t status)
    {
        if (status == cublasStatus_t.CUBLAS_STATUS_SUCCESS) return;
        throw new DotCudaException("cuBLASLt", (int)status,
            NativeLibraries.Utf8(cublasLtGetStatusName(status)), NativeLibraries.Utf8(cublasLtGetStatusString(status)));
    }

    /// <summary>Sets a matmul descriptor attribute from an unmanaged value (enum, pointer-sized integer, ...).</summary>
    public static void Set<T>(cublasLtMatmulDesc_t desc, cublasLtMatmulDescAttributes_t attribute, T value) where T : unmanaged
        => Check(cublasLtMatmulDescSetAttribute(desc, attribute, &value, (nuint)sizeof(T)));

    /// <summary>Sets a matrix layout attribute from an unmanaged value.</summary>
    public static void Set<T>(cublasLtMatrixLayout_t layout, cublasLtMatrixLayoutAttribute_t attribute, T value) where T : unmanaged
        => Check(cublasLtMatrixLayoutSetAttribute(layout, attribute, &value, (nuint)sizeof(T)));

    /// <summary>Sets a matmul preference attribute from an unmanaged value.</summary>
    public static void Set<T>(cublasLtMatmulPreference_t pref, cublasLtMatmulPreferenceAttributes_t attribute, T value) where T : unmanaged
        => Check(cublasLtMatmulPreferenceSetAttribute(pref, attribute, &value, (nuint)sizeof(T)));
}
