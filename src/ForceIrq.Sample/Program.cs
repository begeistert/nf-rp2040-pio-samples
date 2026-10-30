//
// Sample — raising a PIO interrupt from the CPU side: the app calls ForceIrq and the managed
// PioBlock.Interrupt handler fires (the state machine raises nothing here). Useful for CPU->SM
// signalling.
//
// A state machine is claimed even though the program does nothing: the block's interrupt is
// delivered through the callbacks of its claimed state machines, so with none claimed the flag is
// raised in hardware but never reaches managed code.
//

using System;
using System.Threading;
using nanoFramework.Hardware.Pico.Pio;

namespace ForceIrqSample
{
    public class Program
    {
        private const int ForcedIrqFlag = 0;
        private const int PeriodMs = 500;
        private const int Rounds = 10;

        public static int IrqCount;
        public static PioInterruptFlags LastFlags;

        private static void BeaconForcedIrqDelivered()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconForcedIrqLost()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconForcedIrqFlagZero()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconAllRoundsDelivered()
        {
            throw new InvalidOperationException();
        }

        private static void Beacon(int which)
        {
            try
            {
                switch (which)
                {
                    case 0:
                        BeaconForcedIrqDelivered();
                        break;
                    case 1:
                        BeaconForcedIrqLost();
                        break;
                    case 2:
                        BeaconForcedIrqFlagZero();
                        break;
                    default:
                        BeaconAllRoundsDelivered();
                        break;
                }
            }
            catch (Exception)
            {
            }
        }

        public static void Main()
        {
            var asm = new PioAssembler();
            asm.WrapTarget();
            asm.Nop();
            asm.Wrap();
            PioProgram program = asm.Build();

            PioBlock pio = Pio.Get(Pio.MinIndex);
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

            int delivered = 0;

            for (int round = 0; round < Rounds; round++)
            {
                int before = IrqCount;

                pio.ForceIrq(ForcedIrqFlag);
                Thread.Sleep(PeriodMs);

                if (IrqCount > before)
                {
                    delivered++;
                    Beacon(0);

                    if (((int)LastFlags & (1 << ForcedIrqFlag)) == 0)
                    {
                        Beacon(2);
                    }
                }
                else
                {
                    Beacon(1);
                }
            }

            if (delivered == Rounds)
            {
                Beacon(3);
            }

            while (true)
            {
                Thread.Sleep(PeriodMs);
            }
        }
    }
}
