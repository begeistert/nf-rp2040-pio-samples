# CI profiling with nfprof

This wires **nfprof** — a memory profiler for .NET nanoFramework — into CI as a **memory gate**: every
push builds each PIO sample fresh and snapshots its managed heap on the emulated RP2040 nanoCLR (no
board), so a change that grows the heap shows up in the report.

[`.github/workflows/profile.yml`](../.github/workflows/profile.yml):

- builds `WS2812`, `Blink` and `Fade` from source (the apps are **never hardcoded** — only the runtime is),
- prints each sample's **managed-heap report** — every type, no rollup, with GC stats — into the GitHub
  **job summary**, so a memory regression is visible in the diff between runs, and
- uploads a Chrome DevTools `.heapsnapshot` per sample as an artifact (retained sizes, dominators, retainers).

> These samples are I/O-bound — they `Thread.Sleep` between frames — so there's no managed hot path to
> profile for *time*; **memory is the meaningful metric** here. CPU-bound code would also get a
> speedscope flame graph via `nfprof calls`.

## What's here

| | |
|---|---|
| `nfprof/` | a vendored, framework-dependent build of nfprof — run with `dotnet nfprof.dll`. **BUSL-1.1** (see `nfprof/LICENSE-BUSL.txt`). |
| `fixture/` | the **runtime only**: `nanoBooter.bin` + `nanoCLR.bin` (with the PIO interop) and `firmware.manifest.json` (symbol addresses). The sample `.pe` images are built by the workflow, not stored here. |

## Run it locally

```bash
dotnet build tests/WS2812.Integration.Tests -c Release        # stages the app's .pe set
PE=tests/WS2812.Integration.Tests/bin/Release/net10.0/pe

dotnet profiling/nfprof/nfprof.dll memory \
  --firmware profiling/fixture --app-dir "$PE" --app-name WS2812.Sample --until NativeAddProgram --top 40

dotnet profiling/nfprof/nfprof.dll memory \
  --firmware profiling/fixture --app-dir "$PE" --app-name WS2812.Sample --until NativeAddProgram \
  --format heapsnapshot --out heap.heapsnapshot     # → Chrome DevTools › Memory › Load
```
