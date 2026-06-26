//
// Sample — interrupt-driven PIO on a Raspberry Pi Pico 1 (RP2040) from C#: a state machine raises a
// PIO IRQ in hardware and the CPU is notified through a managed event, instead of polling a FIFO.
//
// The PIO program just raises SM IRQ flag 0 on a loop (paced by the clock divider). The block's IRQ0
// line is routed to the NVIC by the native driver; each raised flag is delivered to managed code
// through the PioBlock.Interrupt event, so the handler runs with zero CPU polling.
//
//   .wrap_target
//     irq 0  [15]      ; raise SM irq flag 0, then idle 15 cycles
//   .wrap
//

using System.Threading;
using nanoFramework.Hardware.Rpi.Pio;

namespace IrqSample
{
    public class Program
    {
        // PIO interrupts observed by the managed handler. Exposed so the integration test can drive
        // the CLR until the IRQ → event path has fired a couple of times.
        public static int IrqCount;

        public static void Main()
        {
            var asm = new PioAssembler();
            asm.WrapTarget();
            asm.Irq(0).Delay(15); // raise SM irq flag 0, hold for 15 cycles
            asm.Wrap();
            PioProgram program = asm.Build();

            PioBlock pio = Pio.Get(0);
            uint offset = pio.AddProgram(program);
            PioStateMachine sm = pio.ClaimStateMachine();

            // Slow the SM right down so interrupts arrive at a human pace (~a few kHz of raw loops).
            sm.Init(offset, PioStateMachineConfig.FromProgram(program, (int)offset)
                .ClockFromFrequency(50_000f));

            // Subscribe to PIO block 0's interrupts. Subscribing arms the IRQ0 -> NVIC routing; the
            // handler receives the raised SM flag mask (bit n => irq n).
            pio.Interrupt += (sender, flags) => { IrqCount++; };

            sm.Enabled = true;

            // The handler runs on its own; the main thread just stays alive.
            while (true)
            {
                Thread.Sleep(1000);
            }
        }
    }
}
