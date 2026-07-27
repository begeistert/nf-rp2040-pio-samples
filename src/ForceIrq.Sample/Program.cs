//
// Sample — raising a PIO interrupt from the CPU side: the app calls ForceIrq and the managed
// PioBlock.Interrupt handler fires (the state machine raises nothing here). Useful for CPU->SM
// signalling.
//
// A state machine is claimed even though the program does nothing: the block's interrupt is
// delivered through the callbacks of its claimed state machines, so with none claimed the flag is
// raised in hardware but never reaches managed code.
//

using System.Threading;
using nanoFramework.Hardware.Pico.Pio;

namespace ForceIrqSample
{
    public class Program
    {
        private const int ForcedIrqFlag = 0;
        private const int PeriodMs = 500;

        public static int IrqCount;
        public static PioInterruptFlags LastFlags;

        public static void Main()
        {
            var asm = new PioAssembler();
            asm.WrapTarget();
            asm.Nop();
            asm.Wrap();
            PioProgram program = asm.Build();

            PioBlock pio = Pio.Get(0);
            uint offset = pio.AddProgram(program);

            PioStateMachine sm = pio.ClaimStateMachine();
            sm.Init(offset, PioStateMachineConfig.FromProgram(program, (int)offset)
                .ClockFromFrequency(1_000_000f));

            pio.Interrupt += (sender, flags) =>
            {
                LastFlags = flags;
                IrqCount++;
            };

            sm.Enabled = true;

            while (true)
            {
                pio.ForceIrq(ForcedIrqFlag);
                Thread.Sleep(PeriodMs);
            }
        }
    }
}
