using System.Text;

namespace DotCuda.Cuda12;

/// <summary>nvJitLink (nvJitLink.h). The bindings are generated; this file holds the hand-written helpers.</summary>
public static unsafe partial class NvJitLink
{
    /// <summary>
    /// Throws <see cref="DotCudaException"/> unless <paramref name="result"/> is success. When <paramref name="handle"/>
    /// is given, the linker's error log goes in the message (nvJitLink has no error-string function).
    /// </summary>
    public static void Check(nvJitLinkResult result, nvJitLinkHandle handle = default)
    {
        if (result == nvJitLinkResult.NVJITLINK_SUCCESS) return;
        throw new DotCudaException("nvJitLink", (int)result, result.ToString(), handle.Value == 0 ? null : ErrorLog(handle));
    }

    /// <summary>The linker's error log, or null if it is empty or cannot be read.</summary>
    public static string? ErrorLog(nvJitLinkHandle handle)
    {
        nuint size;
        if (nvJitLinkGetErrorLogSize(handle, &size) != nvJitLinkResult.NVJITLINK_SUCCESS || size <= 1) return null;
        var buffer = new byte[(int)size];
        fixed (byte* p = buffer)
            if (nvJitLinkGetErrorLog(handle, p) != nvJitLinkResult.NVJITLINK_SUCCESS) return null;
        return Encoding.UTF8.GetString(buffer).TrimEnd('\0');
    }
}
