"""Compare the bindings generated from the Windows x64 parse with a Linux x64 parse of the same headers.

    dotnet run --project src/DotCuda.Generator -- --target linux --out <dir>
    python tools/compare_targets.py <dir>

Reports, per library: functions/types present on only one platform, functions whose signature differs, struct
sizes/offsets that differ (from the ABI expectations both runs write), and enum underlying-type differences, which are
expected (GCC gives a non-negative enum 'unsigned int', MSVC 'int'; same size) unless a value lies outside 0..INT_MAX.
"""
import json
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parents[1]
OTHER = pathlib.Path(sys.argv[1])

FN = re.compile(r"\[LibraryImport\(LibraryName(?:, EntryPoint = \"(\w+)\")?\)\]\s*public static partial (.+?) (\w+)\((.*?)\);", re.S)
TYPE = re.compile(r"^public (?:enum|readonly partial record struct|unsafe partial struct) (\w+)(?: : (\w+))?", re.M)
FACT = re.compile(r'f\.Add\(new\("([^"]+)", [^;]*?, (-?\d+)\)\);')


def functions(gen):
    text = (gen / "Functions.g.cs").read_text(encoding="utf-8")
    return {m.group(3): (m.group(1) or m.group(3), m.group(2), " ".join(m.group(4).split())) for m in FN.finditer(text)}


def types(gen):
    out = {}
    for f in ("Enums.g.cs", "Structs.g.cs", "Handles.g.cs"):
        for m in TYPE.finditer((gen / f).read_text(encoding="utf-8")):
            out[m.group(1)] = m.group(2)
    return out


def facts(tests, lib):
    p = tests / f"AbiExpected.{lib}.g.cs"
    return {m.group(1): int(m.group(2)) for m in FACT.finditer(p.read_text(encoding="utf-8"))} if p.exists() else {}


# project -> library name, from the configs (JSON with // comments)
LIBS = {}
for c in (ROOT / "config").glob("*.json"):
    text = "\n".join(re.sub(r"^\s*//.*$", "", line) for line in c.read_text(encoding="utf-8").splitlines())
    cfg = json.loads(text)
    LIBS[cfg["project"]] = cfg["name"]

total_problems = 0
for gen in sorted((ROOT / "src").glob("*/Generated")):
    project = gen.parent.name
    other = OTHER / "src" / project / "Generated"
    lib = LIBS.get(project)
    fw, fl = functions(gen), functions(other)
    tw, tl = types(gen), types(other)
    xw = facts(ROOT / "tests/DotCuda.AbiTests/Generated", lib) if lib else {}
    xl = facts(OTHER / "tests/DotCuda.AbiTests/Generated", lib) if lib else {}

    print(f"== {project}")
    only_w = sorted(set(fw) - set(fl)); only_l = sorted(set(fl) - set(fw))
    sig = sorted(n for n in set(fw) & set(fl) if fw[n] != fl[n])
    t_only_w = sorted(set(tw) - set(tl)); t_only_l = sorted(set(tl) - set(tw))
    enum_sign = sorted(n for n in set(tw) & set(tl) if tw[n] != tl[n])
    layout = sorted(k for k in set(xw) & set(xl) if not k.startswith("enum:") and xw[k] != xl[k])
    enum_vals = sorted(k for k in set(xw) & set(xl) if k.startswith("enum:") and xw[k] != xl[k]
                       and not (xw[k] < 0 and xl[k] == xw[k] + 2**32))
    big = sorted(k for k, v in xl.items() if k.startswith("enum:") and (v < 0 or v > 2**31 - 1))
    print(f"   functions: {len(fw)} Windows, {len(fl)} Linux")
    for label, xs in [("functions only on Windows", only_w), ("functions only on Linux", only_l), ("signature differs", sig),
                      ("types only on Windows", t_only_w), ("types only on Linux", t_only_l),
                      ("struct size/offset differs", layout), ("enum value differs", enum_vals)]:
        if xs:
            print(f"   {label} ({len(xs)}):")
            for x in xs[:40]:
                detail = f"  W: {fw[x]}\n            L: {fl[x]}" if label == "signature differs" else (f"  W {xw[x]} / L {xl[x]}" if x in xw and x in xl else "")
                print(f"      {x}{detail}")
            total_problems += len(xs) if label in ("signature differs", "struct size/offset differs", "enum value differs") else 0
    print(f"   enum underlying type int (Windows) vs uint (Linux): {len(enum_sign)} enums; values outside 0..INT_MAX on Linux: {len(big)}"
          + (f" ({', '.join(big[:8])})" if big else ""))
print()
print(f"ABI-relevant differences on shared declarations (signatures, layouts, enum values): {total_problems}")
