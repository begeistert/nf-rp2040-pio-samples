# CI profiling with nfprof

This wires **nfprof** — a memory/time profiler for .NET nanoFramework — into CI as a demo of profiling
embedded firmware on every push, with no physical board.

[`.github/workflows/profile.yml`](../.github/workflows/profile.yml) runs nfprof against a deployed PIO
app on the emulated RP2040 nanoCLR and:

- prints the **managed-heap report** (by real type, with string values and GC stats) and the **time
  profile** (self-time per method) straight into the GitHub **job summary**, and
- exports a Chrome DevTools `.heapsnapshot` and a [speedscope](https://speedscope.app) flame graph as a
  downloadable artifact — the interactive, dotMemory/dotTrace-style views.

## What's here

| | |
|---|---|
| `nfprof/` | a vendored, framework-dependent build of nfprof (run with `dotnet nfprof.dll`). **BUSL-1.1** — see `nfprof/LICENSE-BUSL.txt`. |
| `fixture/` | the firmware (`nanoBooter.bin` + `nanoCLR.bin`, built with the PIO interop), the deployment image (`deployment.bin`), and `firmware.manifest.json` (symbol addresses derived from the nanoCLR ELF). |

## Run it locally

```bash
dotnet profiling/nfprof/nfprof.dll memory \
  --firmware profiling/fixture --deployment profiling/fixture/deployment.bin --until NativeAddProgram

dotnet profiling/nfprof/nfprof.dll memory \
  --firmware profiling/fixture --deployment profiling/fixture/deployment.bin --until NativeAddProgram \
  --format heapsnapshot --out heap.heapsnapshot     # → Chrome DevTools › Memory › Load
```
