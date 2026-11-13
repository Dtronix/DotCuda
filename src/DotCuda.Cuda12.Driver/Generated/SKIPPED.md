# Skipped declarations: Driver

Generated from `cuda.h` (CUDA 12.6.0). Every declaration in the library's headers that has no binding, and why.

| Counts | |
| --- | --- |
| Functions | 434 |
| Enums | 99 |
| Structs / unions | 74 |
| Handles | 30 |
| Constants | 65 |
| **Skipped** | **3** |

| Kind | Name | File | Reason |
| --- | --- | --- | --- |
| function | `cuDeviceGetNvSciSyncAttributes` | cuda.h | excluded by config: NvSciSync is Linux/Tegra-only; nvcuda.dll does not export it |
| macro | `CUDAAPI` | cuda.h | not a valid C expression: `__stdcall` |
| macro | `CUDA_CB` | cuda.h | not a valid C expression: `__stdcall` |
