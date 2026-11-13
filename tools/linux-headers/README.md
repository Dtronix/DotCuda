Minimal LP64 (x86_64 glibc) C library headers for `DotCuda.Generator --target linux`, which re-parses the CUDA
headers as Linux would see them so the Linux layouts can be compared with the Windows ones. Only the typedefs and
macros the CUDA/cuDNN/NVML headers use; the typedef choices match glibc (int64_t is `long`, size_t is `unsigned long`).
