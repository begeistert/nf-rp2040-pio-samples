# CI profiling with nfprof

This wires **nfprof** — a memory & time profiler for .NET nanoFramework — into CI: every push builds each
PIO sample fresh and profiles it on the emulated RP2040 nanoCLR (no board), so a change that grows the
heap (or shifts where time goes) shows up in the report.

[`.github/workflows/profile.yml`](../.github/workflows/profile.yml):

- builds `WS2812`, `Blink` and `Fade` from source (the apps are **never hardcoded** — only the runtime is),
- prints each sample's **managed-heap report** — every type, no rollup, with GC stats — into the GitHub
  **job summary**, so a memory regression is visible in the diff between runs, and
- uploads, per sample, a Chrome DevTools `.heapsnapshot` (retained sizes, dominators, retainers) and a
  [speedscope](https://speedscope.app) flame graph as artifacts.

> These samples `Thread.Sleep` between frames. nfprof samples **every thread** — not just the scheduled
> one — so a parked thread still shows its wait stack (`Main → Thread.Sleep`) as wall-clock time, the way
> dotTrace does, rather than an empty profile.

## What's here

| | |
|---|---|
| `nfprof/` | a vendored, framework-dependent build of nfprof — run with `dotnet nfprof.dll`. **BUSL-1.1** (see `nfprof/LICENSE-BUSL.txt`). |
| `fixture/` | the **runtime only**: `nanoBooter.bin` + `nanoCLR.bin` (with the PIO interop) and `firmware.manifest.json` (symbol addresses). The sample `.pe` images are built by the workflow, not stored here. |

## Run it locally

```bash
dotnet build tests/WS2812.Integration.Tests -c Release        # stages the app's .pe set
PE=tests/WS2812.Integration.Tests/bin/Release/net10.0/pe
nf() { dotnet profiling/nfprof/nfprof.dll "$@" --firmware profiling/fixture --app-dir "$PE" --app-name WS2812.Sample --until NativeAddProgram; }

nf memory --top 40
nf memory --format heapsnapshot   --out heap.heapsnapshot       # → Chrome DevTools › Memory › Load
nf calls  --instructions 8000000 --format speedscope --out trace.speedscope.json   # → speedscope.app
```
