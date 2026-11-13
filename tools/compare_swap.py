"""Compare the spike bench on DotCuda's generated bindings against the hand-written ones.

Reads results/swap_handwritten{,_run2,_run3}.json and results/swap_dotcuda{,_run2,_run3}.json (runs made
alternately on an idle GPU), skips runs listed in swap_contamination.json, and prints every timing as
median(DotCuda) / median(hand-written), flagging any outside +-3%, plus whether every accuracy metric is identical.

Usage: python tools/compare_swap.py <path/to/CSLLM/spike-cuda>
"""
import json
import statistics
import pathlib
import sys

if len(sys.argv) < 2:
    sys.exit("usage: python tools/compare_swap.py <path/to/CSLLM/spike-cuda>")
SPIKE = pathlib.Path(sys.argv[1])
R = SPIKE / "results"
NOISE = 0.03


def load(name):
    return json.loads((R / f"{name}.json").read_text())


contaminated = {}
if (R / "swap_contamination.json").exists():
    contaminated = {k for k, v in json.loads((R / "swap_contamination.json").read_text(encoding="utf-8-sig")).items() if v}


def runs(prefix):
    names = sorted(p.stem for p in R.glob(f"{prefix}*.json") if p.stem in (prefix, f"{prefix}_run2", f"{prefix}_run3"))
    used = [n for n in names if n not in contaminated]
    print(f"{prefix}: {len(used)} runs used ({', '.join(used)}); excluded as contaminated: {', '.join(sorted(set(names) - set(used))) or 'none'}")
    return [load(n) for n in used]


hw = runs("swap_handwritten")
dc = runs("swap_dotcuda")


def flatten(d, prefix=""):
    out = {}
    for k, v in d.items():
        key = f"{prefix}.{k}" if prefix else k
        if isinstance(v, dict):
            out.update(flatten(v, key))
        elif isinstance(v, (int, float)) and not isinstance(v, bool):
            out[key] = float(v)
    return out


fh, fd = [flatten(r) for r in hw], [flatten(r) for r in dc]
timing = [k for k in fh[0] if (k.endswith("ms") or k.endswith("ms_tuned") or k.endswith("_us"))
          and not k.startswith("env") and k != "nvrtc_compile_ms"]
accuracy = [k for k in fh[0] if k.split(".")[-1] in ("rel", "frob", "max_abs", "mismatches", "mismatches_tuned")]

print(f"{'metric':45} {'hand-written':>13} {'DotCuda':>10} {'ratio':>7}")
outside = []
for k in timing:
    a = statistics.median(f[k] for f in fh)
    b = statistics.median(f[k] for f in fd)
    ratio = b / a
    flag = "" if abs(ratio - 1) <= NOISE else "  <-- outside +-3%"
    if flag:
        outside.append(k)
    print(f"{k:45} {a:13.4f} {b:10.4f} {ratio:7.3f}{flag}")

# Accuracy: same kernels and same cuBLASLt algorithms give bit-identical outputs, so the metrics should match exactly.
# Autotuned ("_tuned") results depend on which candidate wins the timing race, so they are reported separately.
default_acc = [k for k in accuracy if "tuned" not in k]
differ = [k for k in default_acc if any(f[k] != fh[0][k] for f in fh + fd)]
tuned_differ = [k for k in accuracy if "tuned" in k and any(f[k] != fh[0][k] for f in fh + fd)]
print()
print(f"timings: {len(timing)} compared, {len(outside)} outside +-{NOISE:.0%}: {', '.join(outside) or 'none'}")
print(f"accuracy (default algorithm): {len(default_acc)} metrics, {len(differ)} differ between any two runs: {', '.join(differ) or 'none'}")
print(f"accuracy (autotuned algorithm): {len(accuracy) - len(default_acc)} metrics, {len(tuned_differ)} differ (tuner picked a different kernel)")
print("env (DotCuda run 1):", json.dumps(dc[0]["env"]))
