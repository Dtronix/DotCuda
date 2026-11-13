# Plan: DotCuda binding generator, first pass

Nov 13, 2026

## Goal

A C# generator that reads NVIDIA's C headers and emits pointer-only `[LibraryImport]` bindings, plus the first three packages built from its output: `DotCuda.Cuda12.Driver`, `DotCuda.Cuda12.Nvrtc` and `DotCuda.Cuda12.CublasLt`.

It replaces the hand-written bindings from the CSLLM direct-CUDA spike (`CSLLM/spike-cuda/bindings`, 346 lines, 55 calls). That spike passed all five of its checks but recorded eight binding complications. The generator should make the five header-derived ones automatic: versioned symbol names, struct layouts, enum values, typed attribute values and strings.

## Done when

1. **No hand edits.** The generator produces all three packages from the headers; per-library config files are the only input. Every skipped declaration is listed, with its reason, in a generated `SKIPPED.md`.
2. **Pointer-only signatures.** Every generated call is a `[LibraryImport]` whose signature contains only primitives, pointers, enums, `nint`/`ulong` handle structs and blittable structs, under `[assembly: DisableRuntimeMarshalling]`.
3. **ABI tests pass.** Every generated struct's size and field offsets, and every enum constant, match the C compiler on win-x64.
4. **The spike still passes.** `CSLLM/spike-cuda/bench`, switched to the three packages with only `using` and name changes, builds and passes all five checks again, with numbers within run-to-run noise (±3%) of the hand-written bindings.
5. **Builds for both platforms.** `dotnet build -r linux-x64` succeeds with no source changes. Running on Linux is out of scope.

## Repo layout

```
DotCuda/
  src/
    DotCuda.Generator/        console app: parse headers with libclang, emit C# + ABI tests
    DotCuda.Core/             hand-written, version-independent: library resolver, DotCudaException, Utf8Z
    DotCuda.Cuda12.Driver/    Generated/*.g.cs + hand-written Check() and error strings
    DotCuda.Cuda12.Nvrtc/
    DotCuda.Cuda12.CublasLt/
  config/
    cuda12.driver.json        header, symbol filters, logical library name, per-platform file names
    cuda12.nvrtc.json
    cuda12.cublaslt.json
  tests/
    DotCuda.AbiTests/         generated expectations + C program compiled with MSVC
    DotCuda.SmokeTests/       init, alloc/copy, NVRTC compile + launch, one cuBLASLt matmul
  docs/
```

NVIDIA headers are **not committed**. The generator takes an include directory, by default `%CUDA_PATH%\include`, and records the header version (`CUDA_VERSION`, `CUBLAS_VER_*`, `NVRTC` version) in each generated file's banner and in the package version.

## Generator design

**Parsing.** Use libclang through ClangSharp's managed API (`ClangSharp` plus the `libclang`/`libClangSharp` runtime packages), not ClangSharp's P/Invoke generator CLI, so that the emitter is our own code. Parse each library's top-level header once, with the same defines a normal Windows x64 build uses. Only declarations from that library's headers are kept, filtered by file path and symbol prefix (`cu`/`CU` for the driver, `nvrtc` for NVRTC, `cublasLt`/`CUBLASLT_` for cuBLASLt, plus the shared `cudaDataType_t` and `cublasComputeType_t`). If CppAst.NET proves simpler for macros, switch; decide in step 1.

**Mapping rules.**

| C | C# |
| --- | --- |
| `int`, `unsigned int`, `size_t`, `int64_t`, `float`, ... | `int`, `uint`, `nuint`, `long`, `float` |
| `long`, `unsigned long` | **error**: 32-bit on Windows, 64-bit on Linux; must be resolved in config |
| `typedef struct X_st* Handle` (opaque pointer) | `readonly record struct Handle(nint Value)` |
| `CUdeviceptr` (`unsigned long long` typedef) | `readonly record struct CUdeviceptr(ulong Value)`; config lists integer typedefs to wrap |
| `enum` | C# `enum` with clang's underlying type; values from clang, never hand-typed |
| `struct` | `[StructLayout(Sequential)]`; fixed arrays as `[InlineArray]` structs |
| `union` | `[StructLayout(Explicit)]`, every field at `FieldOffset(0)` (or its real offset for nested unions) |
| bit-fields | skipped, with the reason in `SKIPPED.md` |
| function pointer typedef | `delegate* unmanaged<...>` with the mapped signature |
| any `T*`, `const char*`, `char**`, `void**` | `T*`, `byte*`, `byte**`, `void**`; no string or array marshalling |
| `#define X X_v2` | `EntryPoint = "X_v2"` on the binding for `X` |
| `#define` integer constant | `public const` on a `Constants` class |
| `__attribute__((deprecated))` | `[Obsolete]` |

**Versioned symbols.** Resolve the `#define` remaps after preprocessing, so the per-thread default-stream variants (`_ptds` / `_ptsz`) follow whatever `CUDA_API_PER_THREAD_DEFAULT_STREAM` setting the config chooses (default: off, as in the spike). Cross-check each emitted entry-point name against the DLL's real exports (`dumpbin /exports` or reading the PE export table directly). A missing export is a generator error, not a runtime surprise.

**Output.** One `partial` static class per library (`Cu`, `Nvrtc`, `CublasLt`), split into `Functions.g.cs`, `Enums.g.cs`, `Structs.g.cs`, `Handles.g.cs` and `Constants.g.cs`. Generated files are committed, so diffs between header versions are reviewable.

## Hand-written pieces (kept small)

- `DotCuda.Core`: the resolver (logical name → per-platform file, optional `DOTCUDA_LIB_DIR`), `DotCudaException`, `Utf8Z`. This is the spike's `NativeLibraries.cs` minus the CUDA-specific file names, which move to each package's config-generated `LibraryNames.g.cs`.
- Per package: `Check(status)`, turning a non-zero status into an exception with the library's own error name and string. Plus the spike's few helpers: `Alloc`, the `Set<T>` attribute helper and NVRTC `Compile`.

No ergonomic layer beyond that in this pass.

## ABI tests

The generator writes two things per library:
1. **`abi_<lib>.c`:** a program that prints, as JSON, `sizeof` and `offsetof` for every emitted struct field, plus every enum constant.
2. **`AbiExpected.g.cs`:** the same facts from clang's own layout (`clang_Type_getSizeOf` / `getOffsetOf`).

`DotCuda.AbiTests` compiles the C program with MSVC (found through `vswhere`), runs it, and checks three things agree: MSVC's output, clang's numbers, and the C# layout (`Unsafe.SizeOf<T>()` and `Marshal.OffsetOf`). When the C compiler isn't available, the test falls back to clang-only expectations and says so.

## Steps

- [x] **Generator skeleton.** Parse `cuda.h`; list functions, enums, structs and macros. Pick ClangSharp or CppAst.NET based on macro access.
  - **Decision: ClangSharp 21.1.8.4** (managed API; runtime packages `libclang.runtime.win-x64` 21.1.8, `libClangSharp.runtime.win-x64` 21.1.8.2). Parse flags: `--target=x86_64-pc-windows-msvc -fms-extensions -fms-compatibility -xc -std=c11`; `cuda.h` parses with zero errors.
  - From `cuda.h` (12.6): 435 functions, 97 enums, 70 struct/union definitions, 256 typedefs, 150 macros.
  - **Versioned symbols, no macro evaluator needed:** the function decl's name is the post-preprocessing export (`cuMemAlloc_v2`); the source bytes at its expansion location are the name as written (`cuMemAlloc`). 70 driver functions remap this way. (Offsets are byte offsets; the headers contain non-ASCII UTF-8.)
  - **Macro constants, evaluated by clang:** a synthetic in-memory TU includes the header and declares `static __auto_type __dotcuda_X = (X);` per object-like macro; `clang_Cursor_Evaluate` gives value and type. 62 integer constants from `cuda.h`. The other non-int results are function remaps (already handled), empty/attribute macros, and 5 pointer-valued constants (`CU_STREAM_LEGACY`, `CU_STREAM_PER_THREAD`, `CU_LAUNCH_PARAM_*`), which step 2 can emit as typed handle/`nint` values via a `(long long)` cast.
  - CppAst.NET not needed; it wraps the same libclang.
- [ ] **Driver package.** Emit, build, and confirm the spike's 32 driver calls exist with the same entry points (including the nine `_v2` names).
- [ ] **NVRTC package.**
- [ ] **cuBLASLt package.** Confirm the 64- and 96-byte structs come out the same as the spike's.
- [ ] **Export check.** Every entry point exists in the 12.8 DLLs in PyTorch's `torch/lib` and in `nvcuda.dll`.
- [ ] **ABI tests.**
- [ ] **Smoke tests.**
- [ ] **Spike swap.** Point `CSLLM/spike-cuda/bench` at the packages through project references and re-run the benchmarks and `compare.py`.
- [ ] **linux-x64 build.**
- [ ] **Report.** `docs/bindgen-results.md`: done criteria with evidence, generated call counts per library, `SKIPPED.md` summary, which spike complications are now automatic and which remain.

## Versioning and packaging

- **Package IDs:** `DotCuda.Cuda12.<Library>`; version = header version, e.g. 12.6.0 from the local CUDA 12.6 headers. CUDA's compatibility within a major version means 12.6 bindings run against the 12.8 DLLs from `torch/lib`. Generating against 12.8 headers needs a 12.8 toolkit or NVIDIA's pip wheels; that's a follow-up, not a blocker.
- **Later packages:** cuDNN and NCCL on their own version lines (`DotCuda.Cudnn9`, `DotCuda.Nccl2`). CUDA 13 gets `DotCuda.Cuda13.*`, generated from the same tool.
- **No NVIDIA binaries** in any package. Check NVIDIA's EULA on header-derived output before the first public release.
- **Publishing** to NuGet is out of scope for this pass.

## Out of scope (later phases)

- **Phase 2:** cuDNN backend API and NCCL packages; the ergonomic layer (handle lifetimes, `SafeHandle`, typed attribute setters generated from the attribute tables in the headers).
- **Phase 3, kernel tooling:**
  - `DotCuda.Kernels`: an NVRTC compile helper with an on-disk cache.
  - `DotCuda.Build`: an MSBuild task that compiles `<CudaKernel>` items through NVRTC into embedded code for each architecture plus PTX.
  - A source generator that emits typed launch methods from `extern "C" __global__` signatures.

  Custom kernels stay in user projects or their own packages; nobody forks DotCuda to add kernels.
- CUDA runtime API, cuFFT, cuSPARSE, cuSOLVER, NVML (NVML is a candidate for phase 2).

## Risks

- **ClangSharp's native packages:** the libclang runtime is large and version-pinned; the generator is a dev-time tool, so it doesn't affect consumers.
- **Macro handling:** libclang exposes macro definitions but not their evaluated values. Integer constants and `X → X_v2` remaps need a small evaluator or a token-level parse. This is the most likely place for step 1 to slip.
- **Header drift across 12.x:** generated files are committed, so each minor update shows up as a reviewable diff.
