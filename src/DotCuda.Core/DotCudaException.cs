using System.Runtime.InteropServices;

namespace DotCuda;

/// <summary>A CUDA library call returned a non-success status.</summary>
/// <param name="library">Library that failed, e.g. "CUDA driver".</param>
/// <param name="code">The raw status value.</param>
/// <param name="name">The status's name, e.g. "CUDA_ERROR_OUT_OF_MEMORY", when the library provides one.</param>
/// <param name="description">The library's description of the status, or extra context such as a compile log.</param>
public sealed class DotCudaException(string library, int code, string? name, string? description)
    : Exception($"{library} error {code} {name}: {description}")
{
    /// <summary>Library that failed.</summary>
    public string Library { get; } = library;
    /// <summary>The raw status value.</summary>
    public int Code { get; } = code;
    /// <summary>The status's name, if known.</summary>
    public string? StatusName { get; } = name;
    /// <summary>The library's description, if known.</summary>
    public string? Description { get; } = description;
}

/// <summary>Null-terminated UTF-8 string in unmanaged memory, for the APIs that take <c>const char*</c>.</summary>
public readonly unsafe struct Utf8Z : IDisposable
{
    /// <summary>Pointer to the first byte; valid until <see cref="Dispose"/>.</summary>
    public readonly byte* Ptr;

    /// <summary>Copies <paramref name="s"/> to unmanaged memory.</summary>
    public Utf8Z(string s) => Ptr = (byte*)Marshal.StringToCoTaskMemUTF8(s);

    /// <summary>Frees the memory.</summary>
    public void Dispose() => Marshal.FreeCoTaskMem((nint)Ptr);
}
