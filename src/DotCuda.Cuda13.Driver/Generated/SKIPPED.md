# Skipped declarations: Driver13

Generated from `cuda.h` (CUDA 13.4.0). Every declaration in the library's headers that has no binding, and why.

| Counts | |
| --- | --- |
| Functions | 489 |
| Enums | 123 |
| Structs / unions | 96 |
| Handles | 33 |
| Constants | 71 |
| **Skipped** | **3** |

| Kind | Name | File | Reason |
| --- | --- | --- | --- |
| function | `cuDeviceGetNvSciSyncAttributes` | cuda.h | excluded by config: NvSciSync is Linux/Tegra-only; nvcuda.dll does not export it |
| macro | `CUDAAPI` | cuda.h | not a valid C expression: `__stdcall` |
| macro | `CUDA_CB` | cuda.h | not a valid C expression: `__stdcall` |
