//
// Sample — using a PIO state machine as a hardware FIFO loopback ("echo") on a Raspberry Pi
// Pico 1 (RP2040) / Pico 2 (RP2350) from C# via the nanoFramework.Hardware.Rp2040 PIO assembler.
//
// No pins are involved: the program just shuttles each word the CPU pushes (TX FIFO -> OSR ->
// ISR -> RX FIFO), so the app can push a value and read the same value back. It is the smallest
// program that exercises BOTH FIFO directions, and it showcases the non-blocking FIFO API
// (TryPut/TryGet), ClearFifos, and clock setup by target frequency (ClockFromFrequency).
//
//   .wrap_target
//     pull block      ; TX FIFO -> OSR
//     mov  isr, osr   ; OSR -> ISR
//     push block      ; ISR -> RX FIFO
//   .wrap
//

using System.Threading;
using nanoFramework.Hardware.Rp2040.Pio;

namespace EchoSample
{
    public class Program
    {
        // Words successfully pushed and echoed back unchanged. Exposed so the integration test can
        // drive the CLR until the loopback has round-tripped a couple of words.
        public static int Echoed;

        public static void Main()
        {
            var asm = new PioAssembler();
            asm.WrapTarget();
            asm.Pull(ifEmpty: false, block: true); // TX FIFO -> OSR
            asm.Mov(PioDest.Isr, PioSrc.Osr);      // OSR -> ISR
            asm.Push(ifFull: false, block: true);  // ISR -> RX FIFO
            asm.Wrap();
            PioProgram program = asm.Build();

            PioBlock pio = Pio.Get(0);
            uint offset = pio.AddProgram(program);
            PioStateMachine sm = pio.ClaimStateMachine();

            // Run the loopback at 1 MHz: fast enough that a pushed word is back in the RX FIFO long
            // before the next loop iteration. ClockFromFrequency derives the divider from the sysclk.
            sm.Init(offset, PioStateMachineConfig.FromProgram(program, (int)offset)
                .ClockFromFrequency(1_000_000f));

            sm.ClearFifos(); // start from a known-empty state
            sm.Enabled = true;

            uint n = 1;
            while (true)
            {
                // Non-blocking push: if the TX FIFO is momentarily full, just try again next loop
                // instead of stalling the CLR thread.
                if (sm.TryPut(n))
                {
                    // Non-blocking pop, bounded so a stalled SM can never hang the app.
                    uint echoed;
                    int guard = 0;
                    while (!sm.TryGet(out echoed) && guard++ < 1000)
                    {
                    }

                    if (echoed == n)
                    {
                        Echoed++;
                    }

                    n++;
                }

                Thread.Sleep(50);
            }
        }
    }
}
