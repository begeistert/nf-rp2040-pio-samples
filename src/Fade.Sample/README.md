# Fade.Sample — Fade

PWM in PIO: the side-set pin is held high for `(duty+1)/32` of each period, so the LED brightness follows the streamed duty. The state machine generates the PWM in hardware (no flicker, no CPU timing); the app just ramps the duty up and down.

The PIO program is **assembled in C# at runtime** with `PioAssembler` (no external `pioasm`, no raw
`ushort[]`); `Program.cs` shows the full flow: assemble → `AddProgram` → `ClaimStateMachine` → `Init`
→ `Enabled` → `Put`.

## Demo

<!-- Add a GIF / photo of it running on hardware here, e.g.: -->
<!-- ![Fade running on a Pico 2](demo.gif) -->

## Wiring

- `LedPin = 25` (on-board LED) in `Program.cs` — change to match your board.

## Validated

Booted on the RP2040 nanoCLR in the RP2040Sharp emulator (see
[`tests/Fade.Integration.Tests`](../../tests/Fade.Integration.Tests)) and confirmed on a real Pico 2.
