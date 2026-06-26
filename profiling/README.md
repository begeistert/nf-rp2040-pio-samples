# CI profiling with nfprof

This wires **nfprof** — a memory/time profiler for .NET nanoFramework — into CI as a **performance
gate**: every push builds each PIO sample fresh and profiles it on the emulated RP2040 nanoCLR (no
board), so a change that grows the heap or lengthens the run shows up in the report.

[`.github/workflows/profile.yml`](../.github/workflows/profile.yml):

- builds `WS2812`, `Blink` and `Fade` from source (the apps are **never hardcoded** — only the runtime is),
- profiles each, printing the **managed-heap report** (by real type, with GC stats) into the GitHub
  **job summary** — so memory regressions are visible in the diff between runs, and
- exports a Chrome DevTools `.heapsnapshot` and a [speedscope](https://speedscope.app) flame graph per
  sample as a downloadable artifact, for the interactive deep dive.

## What's here

| | |
|---|---|
| `nfprof/` | a vendored, framework-dependent build of nfprof — run with `dotnet nfprof.dll`. **BUSL-1.1** (see `nfprof/LICENSE-BUSL.txt`). |
| `fixture/` | the **runtime only**: `nanoBooter.bin` + `nanoCLR.bin` (built with the PIO interop) and `firmware.manifest.json` (symbol addresses). The sample `.pe` images are built by the workflow, not stored here. |

## Run it locally

```bash
dotnet build tests/WS2812.Integration.Tests -c Release        # stages the app's .pe set
PE=tests/WS2812.Integration.Tests/bin/Release/net10.0/pe

dotnet profiling/nfprof/nfprof.dll memory \
  --firmware profiling/fixture --app-dir "$PE" --app-name WS2812.Sample --until NativeAddProgram

dotnet profiling/nfprof/nfprof.dll memory \
  --firmware profiling/fixture --app-dir "$PE" --app-name WS2812.Sample --until NativeAddProgram \
  --format heapsnapshot --out heap.heapsnapshot     # → Chrome DevTools › Memory › Load
```
