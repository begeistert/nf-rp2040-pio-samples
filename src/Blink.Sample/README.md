# Blink.Sample — Blink

The "hello world" of PIO: the program is just `pull; out pins, 1`. A state machine drives the LED pin; the CPU pushes `1`/`0` into the FIFO. The smallest possible PIO program.

The PIO program is **assembled in C# at runtime** with `PioAssembler` (no external `pioasm`, no raw
`ushort[]`); `Program.cs` shows the full flow: assemble → `AddProgram` → `ClaimStateMachine` → `Init`
→ `Enabled` → `Put`.

## Demo

<!-- Add a GIF / photo of it running on hardware here, e.g.: -->
<!-- ![Blink running on a Pico 2](demo.gif) -->

## Wiring

- `LedPin = 25` (on-board LED) in `Program.cs` — change to match your board.

## Validated

Booted on the RP2040 nanoCLR in the RP2040Sharp emulator (see
[`tests/Blink.Integration.Tests`](../../tests/Blink.Integration.Tests)) and confirmed on a real Pico 2.
