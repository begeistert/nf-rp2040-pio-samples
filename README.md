# RP2040 / RP2350 PIO — C# nanoFramework samples

Sample apps that drive real peripherals from C# on a Raspberry Pi Pico 1 (RP2040) / Pico 2 (RP2350),
using the **PIO (Programmable I/O)** subsystem. Every PIO program is **assembled in C# at runtime** with
an inline `PioAssembler` (no external `pioasm`, no raw `ushort[]`); a state machine runs it in hardware
while the CPU only touches the FIFOs.

These run on top of the `nanoFramework.Hardware.Rp2040` PIO library, which is being proposed for
upstream — discussion: <https://github.com/orgs/nanoframework/discussions/1807>.

## Samples

| Sample | What it shows |
|--------|---------------|
| **WS2812.Sample** | NeoPixel / WS2812 — bit-bangs the 800 kHz protocol on one or more strips, each on its own state machine and data pin. The reason people reach for a Pico. |
| **Blink.Sample** | The "hello world" of PIO: `pull; out pins, 1` — a state machine drives the LED, the CPU just pushes `1`/`0`. |
| **Fade.Sample** | PWM in PIO: the side-set pin is high for `(duty+1)/32` of each period; the app streams the duty to fade the LED smoothly (32 levels, no flicker, no CPU timing). |

### How a sample maps to the library

```
PioAssembler ─Build()→ PioProgram ─FromProgram()→ PioStateMachineConfig   (wrap/side-set/shift auto-applied)
Pio.Get(0) → AddProgram → ClaimStateMachine → Init(offset, cfg) → Enabled → Put(value)
```

## Running on your hardware

The managed app needs an `nf-interpreter` firmware that **includes** the `nanoFramework.Hardware.Rp2040`
assembly and its PIO native interop. Once that firmware is flashed:

1. Build/flash the firmware for your target (`RP_PICO_RP2040` for Pico 1, `RP_PICO2_RP2350` for Pico 2).
2. Open the solution with the nanoFramework VS extension, set a sample as startup, and Deploy.
3. Wire the peripheral to the GPIO in `Program.cs` (default GP25 = on-board LED; WS2812 default GP2).

Each program is validated end-to-end: byte-exact against `pioasm`, behaviourally on the RP2040Sharp /
RP2350Sharp emulators, and on real silicon — a Pico 2 blinking and fading its on-board LED via a
C#-assembled PIO program running through the nanoCLR.

## License

MIT — see [LICENSE](LICENSE).
