# Skipped declarations: Nvml13

Generated from `nvml.h` (CUDA 13.4.0). Every declaration in the library's headers that has no binding, and why.

| Counts | |
| --- | --- |
| Functions | 404 |
| Enums | 67 |
| Structs / unions | 190 |
| Handles | 7 |
| Constants | 636 |
| **Skipped** | **3** |

| Kind | Name | File | Reason |
| --- | --- | --- | --- |
| function | `nvmlDeviceGetBBXTimeData_v1` | nvml.h | excluded by config: declared in the CUDA 13.4 nvml.h but not exported by nvml.dll of driver 617.14; remove once drivers export it |
| macro | `NVML_VGPU_PGPU_VIRTUALIZATION_CAP_MIGRATION` | nvml.h | not a valid C expression: `0:0` |
| macro | `NVML_VGPU_VIRTUALIZATION_CAP_MIGRATION` | nvml.h | not a valid C expression: `0:0` |
