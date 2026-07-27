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
// Two state machines run that program on the same block, because a block has a single interrupt
// line shared by all four of them. The flag they raise is the same one, so the number of managed
// events must track the number of interrupts the block actually raised — not the number of state
// machines that happen to be claimed on it.
//

using System.Threading;
using nanoFramework.Hardware.Pico.Pio;

namespace IrqSample
{
    public class Program
    {
        private const float IrqClockHz = 50_000f;
        private const int StateMachines = 2;

        // PIO interrupts observed by the managed handler. Exposed so the integration test can drive
        // the CLR until the IRQ -> event path has fired a couple of times.
        public static int IrqCount;

        // Flags carried by the last event. Only bit 0 is ever raised by this program, so anything
        // else means the mask was assembled wrong.
        public static PioInterruptFlags LastFlags;

        public static void Main()
        {
            var asm = new PioAssembler();
            asm.WrapTarget();
            asm.Irq(0).Delay(15); // raise SM irq flag 0, hold for 15 cycles
            asm.Wrap();
            PioProgram program = asm.Build();

            PioBlock pio = Pio.Get(Pio.MinIndex);
            uint offset = pio.AddProgram(program);

            // Subscribe before starting anything: subscribing arms the IRQ0 -> NVIC routing, and the
            // handler receives the raised SM flag mask (bit n => irq n).
            pio.Interrupt += (sender, flags) =>
            {
                LastFlags = flags;
                IrqCount++;
            };

            var machines = new PioStateMachine[StateMachines];
            for (int i = 0; i < StateMachines; i++)
            {
                machines[i] = pio.ClaimStateMachine();

                // Slow the SM right down so interrupts arrive at a human pace (~a few kHz of raw loops).
                machines[i].Init(offset, PioStateMachineConfig.FromProgram(program, (int)offset)
                    .ClockFromFrequency(IrqClockHz));
            }

            for (int i = 0; i < StateMachines; i++)
            {
                machines[i].Enabled = true;
            }

            // The handler runs on its own; the main thread just stays alive.
            while (true)
            {
                Thread.Sleep(1000);
            }
        }
    }
}
