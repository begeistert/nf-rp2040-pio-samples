# RP2040 / RP2350 PIO — C# nanoFramework samples

[![CI](https://github.com/begeistert/nf-rp2040-pio-samples/actions/workflows/ci.yml/badge.svg)](https://github.com/begeistert/nf-rp2040-pio-samples/actions/workflows/ci.yml)
[![Profile](https://github.com/begeistert/nf-rp2040-pio-samples/actions/workflows/profile.yml/badge.svg)](https://github.com/begeistert/nf-rp2040-pio-samples/actions/workflows/profile.yml)

Sample apps that drive real peripherals from C# on a Raspberry Pi Pico 1 (RP2040) / Pico 2 (RP2350),
using the **PIO (Programmable I/O)** subsystem. Every PIO program is **assembled in C# at runtime** with
an inline `PioAssembler` (no external `pioasm`, no raw `ushort[]`); a state machine runs it in hardware
while the CPU only touches the FIFOs.

These run on top of the `nanoFramework.Hardware.Rp2040` PIO library, which is being proposed for
upstream — discussion: <https://github.com/orgs/nanoframework/discussions/1807>.

## Samples

| Sample | What it shows | |
|--------|---------------|--|
| **[WS2812.Sample](src/WS2812.Sample)** | NeoPixel / WS2812 — bit-bangs the 800 kHz protocol on one or more strips, each on its own state machine and data pin. | [tests](tests/WS2812.Integration.Tests) |
| **[Blink.Sample](src/Blink.Sample)** | The "hello world" of PIO: `pull; out pins, 1`. | [tests](tests/Blink.Integration.Tests) |
| **[Fade.Sample](src/Fade.Sample)** | PWM in PIO: side-set duty fades the LED (32 levels, no flicker). | [tests](tests/Fade.Integration.Tests) |

### How a sample maps to the library

```
PioAssembler ─Build()→ PioProgram ─FromProgram()→ PioStateMachineConfig   (wrap/side-set/shift auto-applied)
Pio.Get(0) → AddProgram → ClaimStateMachine → Init(offset, cfg) → Enabled → Put(value)
```

## Layout

```
src/    the sample apps (nfproj) — reference only mscorlib + the PIO library
tests/  per-sample integration tests — boot the nanoCLR on the RP2040 emulator and assert
tools/  vendored nanoFramework build system + PIO library assembly (so the apps build standalone)
```

## Running on your hardware

The managed app needs an `nf-interpreter` firmware that **includes** the `nanoFramework.Hardware.Rp2040`
assembly and its PIO native interop. Once that firmware is flashed:

1. Build/flash the firmware for your target (`RP_PICO_RP2040` for Pico 1, `RP_PICO2_RP2350` for Pico 2).
2. Open the solution with the nanoFramework VS extension, set a sample as startup, and Deploy.
3. Wire the peripheral to the GPIO in `Program.cs` (default GP25 = on-board LED; WS2812 default GP2).

## Validation (CI)

Each sample is validated end-to-end **without a physical board**: the deployed app is booted on the
RP2040 nanoCLR running inside the [RP2040Sharp](https://www.nuget.org/packages/RP2040Sharp) emulator,
then driven until it exercises the PIO. The integration tests use the `RP2040Sharp.TestKit.NanoFramework`
NuGet test kit, and run on every push (see the CI badge above). The headline programs are also confirmed
on real silicon — a Pico 2 blinking and fading its on-board LED via a C#-assembled PIO program.

## License

The **sample apps and the PIO library** are **MIT** (see [LICENSE](LICENSE)). The **test kits** they use
for the emulator validation (`RP2040Sharp.TestKit*`) are **BUSL-1.1** — source-available, converting to
MIT on the Change Date; they are the author's specialized validation IP, not a dependency of the apps or
the library.
