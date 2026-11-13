# DotCuda

Pointer-only `[LibraryImport]` bindings for CUDA, generated from NVIDIA's C headers. No marshalling, no
shims, no native code of our own, NativeAOT-compatible.

| Package | Header | Class | Contents |
| --- | --- | --- | --- |
| `DotCuda.Core` | — | `NativeLibraries`, `DotCudaException`, `Utf8Z` | library resolution, errors, UTF-8 strings |
| `DotCuda.Cuda12.Driver` | `cuda.h` (+ `library_types.h`) | `Cu` | 434 functions, 99 enums, 74 structs, 30 handles, 65 constants |
| `DotCuda.Cuda12.Nvrtc` | `nvrtc.h` | `Nvrtc` | 21 functions |
| `DotCuda.Cuda12.NvJitLink` | `nvJitLink.h` | `NvJitLink` | 14 functions |
| `DotCuda.Cuda12.CublasLt` | `cublasLt.h` | `CublasLt` | 44 functions, 20 enums, 6 structs, 5 handles, 21 constants |
| `DotCuda.Cuda12.Nvml` | `nvml.h` | `Nvml` | 330 functions, 51 enums, 95 structs, 6 handles, 405 constants |
| `DotCuda.Cudnn9` | `cudnn.h` (cuDNN 9.14 SDK, CUDA 12 build) | `Cudnn` | 220 functions, 66 enums, 5 structs, 22 handles, 36 constants |
| **CUDA 13** (13.4 headers, namespace `DotCuda.Cuda13`) | | | |
| `DotCuda.Cuda13.Driver` | `cuda.h` (+ `library_types.h`) | `Cu` | 489 functions |
| `DotCuda.Cuda13.Nvrtc` | `nvrtc.h` | `Nvrtc` | 29 functions |
| `DotCuda.Cuda13.NvJitLink` | `nvJitLink.h` | `NvJitLink` | 16 functions |
| `DotCuda.Cuda13.CublasLt` | `cublasLt.h` | `CublasLt` | 53 functions |
| `DotCuda.Cuda13.Nvml` | `nvml.h` | `Nvml` | 404 functions |
| `DotCuda.Cuda13.Cudnn9` | `cudnn.h` (cuDNN 9.14 SDK, CUDA 13 build) | `Cudnn` | 220 functions |

The CUDA 12 packages above use the 12.6 headers and namespace `DotCuda.Cuda12` (cuDNN: `DotCuda.Cudnn9`). CUDA 13
supports Turing (sm_75) and newer; Maxwell, Pascal and Volta need the CUDA 12 packages.

Everything not bound is listed with its reason in each package's `Generated/SKIPPED.md`. Types shared across libraries
(`CUstream` for `cudaStream_t`, `cudaDataType_t`, `libraryPropertyType_t`) live in the driver package and are reused.

```csharp
using DotCuda;
using DotCuda.Cuda12;

unsafe
{
    Cu.Check(Cu.cuInit(0));
    CUdevice dev;
    Cu.Check(Cu.cuDeviceGet(&dev, 0));
    CUdeviceptr p = Cu.Alloc(1 << 20);          // cuMemAlloc binds to the cuMemAlloc_v2 export
    Cu.Check(Cu.cuMemFree(p));
}
```

## What the generated code looks like

- Names are the C names: `Cu.cuMemAlloc`, `CUresult.CUDA_SUCCESS`, `cublasLtEpilogue_t.CUBLASLT_EPILOGUE_GELU_BIAS`.
- Signatures use only primitives, pointers, enums, blittable structs and handle structs; `const char*` is `byte*`
  (use `Utf8Z` / `NativeLibraries.Utf8`). Every assembly has `[assembly: DisableRuntimeMarshalling]`.
- Opaque handles (`CUcontext`, `cublasLtHandle_t`, ...) are `readonly record struct X(nint Value)`; integer
  handles (`CUdeviceptr`, `CUdevice`, ...) wrap their integer type the same way.
- Versioned exports are found automatically, however the header spells them: `#define cuMemAlloc cuMemAlloc_v2`,
  `nvmlInit_v2` declared plus `#define nvmlInit nvmlInit_v2`, and static inline forwarders such as nvJitLink's
  `nvJitLinkCreate` -> `__nvJitLinkCreate_12_6` all bind under the C name with the real `EntryPoint`.
- C `long` / `unsigned long` (32-bit on Windows, 64-bit on Linux) map to `CLong` / `CULong`.
- Structs are `[StructLayout(Sequential)]`; unions are `Explicit` with clang's offsets; fixed arrays are `[InlineArray]`.
- Macro constants (`CUDA_VERSION`, `CU_STREAM_LEGACY`, ...) are evaluated by clang and emitted as typed constants.
- Hand-written helpers per package: `Check(...)`, `Cu.Alloc`, `CublasLt.Set<T>`, `Nvrtc.Compile`.

## Finding the native libraries

No NVIDIA binaries are shipped. The driver (`nvcuda.dll` / `libcuda.so.1`) and NVML (`nvml.dll` / `libnvidia-ml.so.1`)
always come from the installed driver.
NVRTC, nvJitLink, cuBLASLt and cuDNN are searched in this order:

1. `DOTCUDA_LIB_DIR` (for example PyTorch's `torch/lib`, or the directory of NVIDIA's pip wheels);
2. the normal native search path (application directory, `PATH` / `LD_LIBRARY_PATH`);
3. the toolkit: `%CUDA_PATH%\bin` on Windows, `$CUDA_PATH/lib64` or `/usr/local/cuda/lib64` on Linux.

`NativeLibraries.Describe(Cu.LibraryName)` reports which file was loaded. CUDA's minor-version compatibility means
the 12.6-generated bindings run against any 12.x libraries (tested with 12.8 from PyTorch).

## Regenerating

Needs Windows, the CUDA 12 toolkit headers (`CUDA_PATH`), the CUDA 13 headers (below), the cuDNN SDK headers (the newest `%ProgramFiles%\NVIDIA\CUDNN\v9.*`,
or `DOTCUDA_CUDNN_INCLUDE`), and .NET 10.

```
dotnet run --project src/DotCuda.Generator [-- --include <cuda include dir>] [--export-dir <dir with DLLs>]...
```

CUDA 13 headers don't need a toolkit install: `tools/fetch-cuda-sdk.ps1` downloads just the needed components from
NVIDIA's redistributable archives (SHA-256 checked) into `%LOCALAPPDATA%\DotCuda\sdk\cuda-<version>`, which the
`config/cuda13.*.json` files find automatically (or set `DOTCUDA_CUDA13_PATH`). Its `bin\x64` also works as
`DOTCUDA_LIB_DIR` for running against those libraries.

```
pwsh tools/fetch-cuda-sdk.ps1 -Version 13.4.3
```

Pick a version your driver supports: generating from headers newer than the driver makes the export check fail for
the driver functions it doesn't have yet.

The generator reads `config/*.json` (the only hand-maintained input), parses each header with libclang
(ClangSharp), and writes `src/<package>/Generated/*` plus `tests/DotCuda.AbiTests/Generated/*`. It fails if an
emitted entry point is missing from the export table of any DLL it can find (`nvcuda.dll`, `%CUDA_PATH%\bin`,
`DOTCUDA_LIB_DIR`, `--export-dir`). Generated files are committed, so header updates show up as reviewable diffs.

## Linux

The packages build for linux-x64 and carry the Linux file names (`libcuda.so.1`, `libnvrtc.so.12`, `libnvJitLink.so.12`,
`libcublasLt.so.12`, `libnvidia-ml.so.1`, `libcudnn.so.9`). To check that the bindings are right for Linux, the generator
re-parses the headers as x86_64 Linux (glibc LP64, with the stub C headers in `tools/linux-headers`) and the result is
compared with the Windows output:

```
dotnet run --project src/DotCuda.Generator -- --target linux --out <dir>
python tools/compare_targets.py <dir>
```

Result for every library: the same functions, signatures, struct sizes and offsets, and enum values. GCC types
non-negative enums as `unsigned int` where MSVC uses `int`, which has the same size and bits. Running on Linux has not
been tested.

## Tests (TUnit)

- `tests/DotCuda.AbiTests`: every struct size and field offset, enum value and integer constant agrees across the
  C# layout, clang, and MSVC compiling the same header (MSVC found through `vswhere`; clang-only when missing).
- `tests/DotCuda.SmokeTests`: driver init, alloc/copy, an NVRTC compile + kernel launch, and a cuBLASLt matmul
  checked against the CPU. Skipped without a CUDA device. Also runs as a NativeAOT executable:

```
dotnet test
dotnet publish tests/DotCuda.SmokeTests -c Release -r win-x64 -p:PublishAot=true -o artifacts/aot-smoke
artifacts/aot-smoke/DotCuda.SmokeTests.exe
```

See `docs/bindgen-plan.md` for the plan and `docs/bindgen-results.md` for the first-pass results.

## License

DotCuda's own code (the generator, `DotCuda.Core`, the hand-written helpers, tests and tools) is MIT; see `LICENSE`.

NVIDIA's headers and libraries are not part of this repository and stay under NVIDIA's license terms (the CUDA Toolkit
EULA, the cuDNN license). The generated files (`src/*/Generated/`, `tests/DotCuda.AbiTests/Generated/`) are derived from
those headers: declarations, constant values and, in XML doc comments, NVIDIA's documentation text. Review NVIDIA's terms
before redistributing them or publishing packages built from them.
