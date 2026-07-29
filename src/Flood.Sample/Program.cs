using System;
using System.Threading;
using nanoFramework.Hardware.Pico.Pio;

namespace FloodSample
{
    public class Program
    {
        public static int Ticks;

        private static void BeaconFloodTick()
        {
            throw new InvalidOperationException();
        }

        public static void Main()
        {
            PioBlock pio = Pio.Get(Pio.MinIndex);

            var asm = new PioAssembler();
            asm.OutShift(ShiftDirection.Left, autoPull: true, threshold: 32);
            asm.InShift(ShiftDirection.Left, autoPush: true, threshold: 32);
            asm.WrapTarget();
            asm.Out(DestinationOperand.RegisterX, 32);
            asm.In(SourceOperand.RegisterX, 32);
            asm.Wrap();
            PioProgram program = asm.Build();

            uint offset = pio.AddProgram(program);
            PioStateMachine sm = pio.ClaimStateMachine();
            sm.Init(offset, PioStateMachineConfig.FromProgram(program, (int)offset));
            sm.Enabled = true;

            while (true)
            {
                sm.Put((uint)Ticks);
                sm.Get();
                Ticks++;

                try
                {
                    BeaconFloodTick();
                }
                catch (InvalidOperationException)
                {
                }

                Thread.Sleep(10);
            }
        }
    }
}
