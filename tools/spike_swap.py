"""Copy CSLLM/spike-cuda/bench to bench-dotcuda and switch it to the DotCuda packages.

Only `using`/project-reference changes and name changes are made: the spike's shortened enum member
names become the C names the generator emits, handle fields `.Ptr`/`.Handle` become `.Value`, and
CudaException/NativeLibraries names change. The original bench is left untouched as the baseline.

Usage: python tools/spike_swap.py <path/to/CSLLM/spike-cuda>
"""
import pathlib
import re
import shutil
import sys

if len(sys.argv) < 2:
    sys.exit("usage: python tools/spike_swap.py <path/to/CSLLM/spike-cuda>")
SPIKE = pathlib.Path(sys.argv[1])
SRC, DST = SPIKE / "bench", SPIKE / "bench-dotcuda"
DOTCUDA = pathlib.Path(__file__).resolve().parents[1]

ENUMS = {
    "CUdevice_attribute": {"MultiprocessorCount": "CU_DEVICE_ATTRIBUTE_MULTIPROCESSOR_COUNT",
                           "ComputeCapabilityMajor": "CU_DEVICE_ATTRIBUTE_COMPUTE_CAPABILITY_MAJOR",
                           "ComputeCapabilityMinor": "CU_DEVICE_ATTRIBUTE_COMPUTE_CAPABILITY_MINOR"},
    "cudaDataType_t": {"R_32F": "CUDA_R_32F", "R_16BF": "CUDA_R_16BF", "R_8I": "CUDA_R_8I", "R_32I": "CUDA_R_32I"},
    "cublasComputeType_t": {"Compute32F_FastTF32": "CUBLAS_COMPUTE_32F_FAST_TF32", "Compute32F": "CUBLAS_COMPUTE_32F",
                            "Compute32I": "CUBLAS_COMPUTE_32I"},
    "cublasOperation_t": {"N": "CUBLAS_OP_N", "T": "CUBLAS_OP_T"},
    "cublasStatus_t": {"Success": "CUBLAS_STATUS_SUCCESS"},
    "CUresult": {"Success": "CUDA_SUCCESS"},
    "nvrtcResult": {"Success": "NVRTC_SUCCESS"},
    "cublasLtMatmulPreferenceAttributes_t": {"MaxWorkspaceBytes": "CUBLASLT_MATMUL_PREF_MAX_WORKSPACE_BYTES"},
    "cublasLtMatmulDescAttributes_t": {
        "ComputeType": "CUBLASLT_MATMUL_DESC_COMPUTE_TYPE", "ScaleType": "CUBLASLT_MATMUL_DESC_SCALE_TYPE",
        "TransA": "CUBLASLT_MATMUL_DESC_TRANSA", "TransB": "CUBLASLT_MATMUL_DESC_TRANSB",
        "Epilogue": "CUBLASLT_MATMUL_DESC_EPILOGUE", "BiasPointer": "CUBLASLT_MATMUL_DESC_BIAS_POINTER",
        "EpilogueAuxPointer": "CUBLASLT_MATMUL_DESC_EPILOGUE_AUX_POINTER", "EpilogueAuxLd": "CUBLASLT_MATMUL_DESC_EPILOGUE_AUX_LD",
        "EpilogueAuxDataType": "CUBLASLT_MATMUL_DESC_EPILOGUE_AUX_DATA_TYPE", "BiasDataType": "CUBLASLT_MATMUL_DESC_BIAS_DATA_TYPE"},
    "cublasLtEpilogue_t": {
        "Default": "CUBLASLT_EPILOGUE_DEFAULT", "Bias": "CUBLASLT_EPILOGUE_BIAS", "Gelu": "CUBLASLT_EPILOGUE_GELU",
        "GeluBias": "CUBLASLT_EPILOGUE_GELU_BIAS", "GeluAux": "CUBLASLT_EPILOGUE_GELU_AUX",
        "GeluAuxBias": "CUBLASLT_EPILOGUE_GELU_AUX_BIAS", "DGelu": "CUBLASLT_EPILOGUE_DGELU",
        "DGeluBGrad": "CUBLASLT_EPILOGUE_DGELU_BGRAD", "BGradA": "CUBLASLT_EPILOGUE_BGRADA", "BGradB": "CUBLASLT_EPILOGUE_BGRADB"},
}

# Literal renames (order matters only where one key contains another).
LITERAL = [
    ("CudaException", "DotCudaException"),
    ("NativeLibraries.Describe(NativeLibraries.CublasLt)", "NativeLibraries.Describe(CublasLt.LibraryName)"),
    ("NativeLibraries.Describe(NativeLibraries.Nvrtc)", "NativeLibraries.Describe(Nvrtc.LibraryName)"),
    ("NativeLibraries.Describe(NativeLibraries.Cuda)", "NativeLibraries.Describe(Cu.LibraryName)"),
    ("using CudaDirect;\n", ""),  # replaced by global usings in the csproj
    # CUdeviceptr locals whose names collide with DBuf variables elsewhere, so they are renamed exactly.
    ("Add(p.Ptr)", "Add(p.Value)"),
    ("(void*)c.Ptr", "(void*)c.Value"),
]

# Handle record structs: the spike named the field Handle/Ptr/Ordinal; generated ones are all Value.
HANDLE_MEMBER = re.compile(r"\b(_module|Workspace|bias|A|B|D|Ptr|aux|Aux|ws)\.(Ptr|Handle)\b")


def convert(text: str) -> tuple[str, int]:
    n = 0
    for enum, members in ENUMS.items():
        for old, new in members.items():
            text, k = re.subn(rf"\b{enum}\.{old}\b", f"{enum}.{new}", text)
            n += k
    for old, new in LITERAL:
        n += text.count(old)
        text = text.replace(old, new)
    text, k = HANDLE_MEMBER.subn(lambda m: f"{m.group(1)}.Value", text)
    return text, n + k


def main() -> None:
    if DST.exists():
        shutil.rmtree(DST)
    DST.mkdir()
    total = 0
    for f in sorted(SRC.iterdir()):
        if f.is_dir():
            continue  # bin/obj
        if f.suffix == ".cs":
            text, n = convert(f.read_text(encoding="utf-8"))
            (DST / f.name).write_text(text, encoding="utf-8")
            print(f"{f.name}: {n} renames")
            total += n
        elif f.suffix == ".csproj":
            text = f.read_text(encoding="utf-8")
            refs = "\n".join(
                f'    <ProjectReference Include="{DOTCUDA / "src" / p / (p + ".csproj")}" />'
                for p in ("DotCuda.Cuda12.Driver", "DotCuda.Cuda12.Nvrtc", "DotCuda.Cuda12.CublasLt"))
            replacement = "\n" + refs + '\n    <Using Include="DotCuda" />\n    <Using Include="DotCuda.Cuda12" />'
            text = re.sub(r'\s*<ProjectReference Include="\.\.\\bindings\\Bindings\.csproj" />', lambda _: replacement, text)
            (DST / f.name).write_text(text, encoding="utf-8")
        else:
            shutil.copy2(f, DST / f.name)
    print(f"total: {total} renames into {DST}")


if __name__ == "__main__":
    main()
