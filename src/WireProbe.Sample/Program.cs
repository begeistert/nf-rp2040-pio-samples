using System;
using System.Threading;
using nanoFramework.Hardware.Pico.Pio;

namespace WireProbeSample
{
    public class Program
    {
        private static void BeaconPinHigh()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconPinLow()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconSelfHigh()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconSelfLow()
        {
            throw new InvalidOperationException();
        }

        private static uint ReadPin(PioBlock pio, int pin)
        {
            var asm = new PioAssembler();
            asm.SetInShift(ShiftDirection.Left, autoPush: true, threshold: 32);
            PioLabel high = asm.DefineLabel();
            PioLabel done = asm.DefineLabel();
            asm.WrapTarget();
            asm.Set(DestinationOperand.RegisterY, 0);
            asm.Jmp(PioCondition.Pin, high);
            asm.Jmp(done);
            asm.MarkLabel(high);
            asm.Set(DestinationOperand.RegisterY, 1);
            asm.MarkLabel(done);
            asm.In(SourceOperand.RegisterY, 32);
            asm.Wrap();
            PioProgram program = asm.Build();

            uint offset = pio.AddProgram(program);
            PioStateMachine sm = pio.ClaimStateMachine();

            try
            {
                pio.InitGpio(pin, PioPinPull.Down);
                sm.Init(offset, PioStateMachineConfig.FromProgram(program, (int)offset)
                    .JmpPin(pin)
                    .ClockDivisor(1000f));
                sm.Enabled = true;
                Thread.Sleep(20);
                return sm.Get();
            }
            finally
            {
                sm.Dispose();
                pio.RemoveProgram(program, offset);
            }
        }

        private static void DriveHigh(PioBlock pio, int pin)
        {
            var asm = new PioAssembler();
            asm.WrapTarget();
            asm.Set(DestinationOperand.Pins, 1);
            asm.Wrap();
            PioProgram program = asm.Build();

            uint offset = pio.AddProgram(program);
            PioStateMachine sm = pio.ClaimStateMachine();

            pio.InitGpio(pin);
            sm.Init(offset, PioStateMachineConfig.FromProgram(program, (int)offset)
                .SetPins(pin, 1));
            sm.SetConsecutivePinDirs(pin, 1, true);
            sm.Enabled = true;
        }

        public static void Main()
        {
            PioBlock pio = Pio.Get(Pio.MinIndex);

            DriveHigh(pio, 2);

            while (true)
            {
                uint self = ReadPin(pio, 2);
                uint level = ReadPin(pio, 3);

                try
                {
                    if (self != 0)
                    {
                        BeaconSelfHigh();
                    }
                    else
                    {
                        BeaconSelfLow();
                    }
                }
                catch (InvalidOperationException)
                {
                }

                try
                {
                    if (level != 0)
                    {
                        BeaconPinHigh();
                    }
                    else
                    {
                        BeaconPinLow();
                    }
                }
                catch (InvalidOperationException)
                {
                }

                Thread.Sleep(2000);
            }
        }
    }
}
