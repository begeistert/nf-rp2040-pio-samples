# WS2812.Sample — WS2812 / NeoPixel

Drives one or more WS2812 / NeoPixel strips. A PIO state machine bit-bangs the 800 kHz protocol in hardware while the CPU only pushes 24-bit GRB colours into the FIFO — so several strips run concurrently, each on its own state machine and data pin.

The PIO program is **assembled in C# at runtime** with `PioAssembler` (no external `pioasm`, no raw
`ushort[]`); `Program.cs` shows the full flow: assemble → `AddProgram` → `ClaimStateMachine` → `Init`
→ `Enabled` → `Put`.

## Demo

<!-- Add a GIF / photo of it running on hardware here, e.g.: -->
<!-- ![WS2812 running on a Pico 2](demo.gif) -->

## Wiring

- `Strip0Pin = 2`, `Strip1Pin = 3` in `Program.cs` — change to match your board.

## Validated

Booted on the RP2040 nanoCLR in the RP2040Sharp emulator (see
[`tests/WS2812.Integration.Tests`](../../tests/WS2812.Integration.Tests)) and confirmed on a real Pico 2.
