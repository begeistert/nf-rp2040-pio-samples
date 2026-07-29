//
// Sample — second battery over the PIO API: everything the first battery left untouched.
//
// Same rules as ApiSurface.Sample: every check verifies itself through the FIFOs, scratch
// registers or internal machinery, so it needs no wiring and no second board. Covered here:
// right-shifting paths, non-32 thresholds, the ClockDivisor property and restarts, claiming a
// specific state machine, ProgramCounter, DrainTxFifo, the four FIFO flags, ForceIrq/ClearIrq,
// inter-machine handshakes over IRQ flags, Read/Write with buffer offsets, mov invert and
// bit-reverse, jump conditions, a fixed program origin, blocking Put/Get wake-ups, optional
// side-set, program metadata, and InitGpioRange.
//
using System;
using System.Threading;
using System.Diagnostics;
using nanoFramework.Hardware.Pico.Pio;

namespace ApiSurface2Sample
{
    public class Program
    {
        private static bool[] _passed;
        private static bool[] _ran;
        private static string _failDetail = "";

        public static void Main()
        {
            _passed = new bool[16];
            _ran = new bool[16];

            Debug.WriteLine($"API surface battery 2 on {Pio.BlockCount} blocks");

            Run(1, T12ShiftRight);
            Run(2, T13Threshold16);
            Run(3, T14ClockDivisorProperty);
            Run(4, T15ClaimSpecific);
            Run(5, T16ProgramCounter);
            Run(6, T17DrainTxFifo);
            Run(7, T18FifoFlags);
            Run(8, T19ForceIrq);
            Run(9, T20InterSmIrqHandshake);
            Run(10, T21ReadWriteOffsets);
            Run(11, T22MovOps);
            Run(12, T23JmpConditions);
            Run(13, T24Origin);
            Run(14, T25BlockingWake);
            Run(15, T26SideSetOptional);

            while (true)
            {
                FireResults();
                Thread.Sleep(10_000);
            }
        }

        private static void Run(int index, TestMethod test)
        {
            _ran[index] = true;

            try
            {
                _passed[index] = test();
            }
            catch (Exception)
            {
                _passed[index] = false;
            }
        }

        private delegate bool TestMethod();

        private static PioProgram EchoProgram(ShiftDirection dir, int threshold)
        {
            var asm = new PioAssembler();

            asm.OutShift(dir, autoPull: true, threshold: threshold);
            asm.InShift(dir, autoPush: true, threshold: threshold);

            asm.WrapTarget();
            asm.Out(DestinationOperand.RegisterX, threshold);
            asm.In(SourceOperand.RegisterX, threshold);
            asm.Wrap();

            return asm.Build();
        }

        private static bool T12ShiftRight()
        {
            PioBlock pio = Pio.Get(Pio.MinIndex);
            PioProgram program = EchoProgram(ShiftDirection.Right, 32);
            uint offset = pio.AddProgram(program);
            PioStateMachine sm = pio.ClaimStateMachine();

            try
            {
                sm.Init(offset, PioStateMachineConfig.FromProgram(program, (int)offset));
                sm.Enabled = true;

                sm.Put(0xDEADBEEF);

                return sm.Get() == 0xDEADBEEF;
            }
            finally
            {
                sm.Dispose();
                pio.RemoveProgram(program, offset);
            }
        }

        private static bool T13Threshold16()
        {
            PioBlock pio = Pio.Get(Pio.MinIndex);
            PioProgram program = EchoProgram(ShiftDirection.Left, 16);
            uint offset = pio.AddProgram(program);
            PioStateMachine sm = pio.ClaimStateMachine();

            try
            {
                sm.Init(offset, PioStateMachineConfig.FromProgram(program, (int)offset));
                sm.Enabled = true;

                sm.Put(0xAAAA5555);

                bool ok = sm.Get() == 0x0000AAAA;

                sm.Put(0x12340000);
                ok = ok && sm.Get() == 0x00001234;

                return ok;
            }
            finally
            {
                sm.Dispose();
                pio.RemoveProgram(program, offset);
            }
        }

        private static bool T14ClockDivisorProperty()
        {
            PioBlock pio = Pio.Get(Pio.MinIndex);
            PioProgram program = EchoProgram(ShiftDirection.Left, 32);
            uint offset = pio.AddProgram(program);
            PioStateMachine sm = pio.ClaimStateMachine();

            try
            {
                sm.Init(offset, PioStateMachineConfig.FromProgram(program, (int)offset));
                sm.Enabled = true;

                sm.ClockDivisor = 100f;
                sm.ClockDivRestart();

                float readBack = sm.ClockDivisor;
                bool ok = readBack > 99f && readBack < 101f;

                sm.Put(0x11112222);
                ok = ok && sm.Get() == 0x11112222;

                sm.ClockDivisor = 65535f;
                sm.ClockDivRestart();

                sm.Put(0x33334444);
                ok = ok && sm.Get() == 0x33334444;

                sm.ClockDivisor = 1f;
                sm.ClockDivRestart();

                sm.Put(0x55556666);
                ok = ok && sm.Get() == 0x55556666;

                sm.Restart();
                sm.Put(0x9999AAAA);
                ok = ok && sm.Get() == 0x9999AAAA;

                return ok;
            }
            finally
            {
                sm.Dispose();
                pio.RemoveProgram(program, offset);
            }
        }

        private static bool T15ClaimSpecific()
        {
            PioBlock pio = Pio.Get(Pio.MinIndex);

            PioStateMachine sm2 = pio.ClaimStateMachine(PioStateMachineIndex.Sm2);

            try
            {
                bool ok = sm2.Index == PioStateMachineIndex.Sm2;

                bool duplicateFailed = false;

                try
                {
                    PioStateMachine dup = pio.ClaimStateMachine(PioStateMachineIndex.Sm2);
                    dup.Dispose();
                }
                catch (Exception)
                {
                    duplicateFailed = true;
                }

                ok = ok && duplicateFailed;

                PioStateMachine other = pio.ClaimStateMachine();
                ok = ok && other.Index != PioStateMachineIndex.Sm2;
                other.Dispose();

                return ok;
            }
            finally
            {
                sm2.Dispose();
            }
        }

        private static bool T16ProgramCounter()
        {
            PioBlock pio = Pio.Get(Pio.MinIndex);

            var asm = new PioAssembler();
            asm.WrapTarget();
            asm.Nop();
            asm.Nop();
            asm.Nop();
            asm.Nop();
            asm.Wrap();
            PioProgram program = asm.Build();

            uint offset = pio.AddProgram(program);
            PioStateMachine sm = pio.ClaimStateMachine();

            try
            {
                sm.Init(offset, PioStateMachineConfig.FromProgram(program, (int)offset)
                    .ClockDivisor(65535f));
                sm.Enabled = true;

                bool ok = true;

                for (int i = 0; i < 8; i++)
                {
                    uint pc = sm.ProgramCounter;
                    ok = ok && pc >= offset && pc < offset + 4;
                    Thread.Sleep(5);
                }

                return ok;
            }
            finally
            {
                sm.Dispose();
                pio.RemoveProgram(program, offset);
            }
        }

        private static bool T17DrainTxFifo()
        {
            PioBlock pio = Pio.Get(Pio.MinIndex);
            PioProgram program = EchoProgram(ShiftDirection.Left, 32);
            uint offset = pio.AddProgram(program);
            PioStateMachine sm = pio.ClaimStateMachine();

            try
            {
                sm.Init(offset, PioStateMachineConfig.FromProgram(program, (int)offset));

                for (uint i = 0; i < 4; i++)
                {
                    sm.TryPut(i);
                }

                bool ok = sm.TxLevel == 4;

                sm.DrainTxFifo();

                ok = ok && sm.TxLevel == 0;

                sm.Enabled = true;
                sm.Put(0x77778888);
                ok = ok && sm.Get() == 0x77778888;

                return ok;
            }
            finally
            {
                sm.Dispose();
                pio.RemoveProgram(program, offset);
            }
        }

        private static bool T18FifoFlags()
        {
            PioBlock pio = Pio.Get(Pio.MinIndex);
            PioProgram program = EchoProgram(ShiftDirection.Left, 32);
            uint offset = pio.AddProgram(program);
            PioStateMachine sm = pio.ClaimStateMachine();

            try
            {
                sm.Init(offset, PioStateMachineConfig.FromProgram(program, (int)offset));

                bool ok = sm.IsRxEmpty;
                ok = ok && !sm.IsTxFull;

                for (uint i = 0; i < 4; i++)
                {
                    sm.TryPut(0xC0DE0000 + i);
                }

                ok = ok && sm.IsTxFull;

                sm.Enabled = true;
                Thread.Sleep(50);

                ok = ok && !sm.IsTxFull;
                ok = ok && !sm.IsRxEmpty;

                sm.ClearFifos();

                ok = ok && sm.IsRxEmpty;

                return ok;
            }
            finally
            {
                sm.Dispose();
                pio.RemoveProgram(program, offset);
            }
        }

        public static int ForcedEvents;
        public static PioInterruptFlags LastForcedFlags;
        public static int T19EventsSeen = -1;
        public static int T19FlagsSeen = -1;
        public static int T25Elapsed = -1;
        public static uint T25Value;
        public static long ProducerPutTicks;

        private static bool T19ForceIrq()
        {
            PioBlock pio = Pio.Get(Pio.MinIndex);

            ForcedEvents = 0;
            LastForcedFlags = 0;

            PioStateMachine anchor = pio.ClaimStateMachine();

            PioInterruptEventHandler handler = (sender, flags) =>
            {
                ForcedEvents++;
                LastForcedFlags = flags;
            };

            pio.Interrupt += handler;

            try
            {
                pio.ClearIrq(1);

                pio.ForceIrq(1);
                Thread.Sleep(300);

                bool ok = ForcedEvents == 1;
                ok = ok && ((int)LastForcedFlags & 0x2) != 0;

                pio.ClearIrq(3);
                Thread.Sleep(100);
                ok = ok && ForcedEvents == 1;

                T19EventsSeen = ForcedEvents;
                T19FlagsSeen = (int)LastForcedFlags;

                return ok;
            }
            finally
            {
                pio.Interrupt -= handler;
                anchor.Dispose();
            }
        }

        private static bool T20InterSmIrqHandshake()
        {
            PioBlock pio = Pio.Get(Pio.MinIndex);

            var waiterAsm = new PioAssembler();
            waiterAsm.InShift(ShiftDirection.Left, autoPush: true, threshold: 32);
            waiterAsm.WrapTarget();
            waiterAsm.WaitIrq(true, 4);
            waiterAsm.In(SourceOperand.RegisterY, 32);
            waiterAsm.Wrap();
            PioProgram waiterProgram = waiterAsm.Build();

            var pokerAsm = new PioAssembler();
            pokerAsm.WrapTarget();
            pokerAsm.Irq(4).Delay(31);
            pokerAsm.Nop().Delay(31);
            pokerAsm.Wrap();
            PioProgram pokerProgram = pokerAsm.Build();

            uint waiterOffset = pio.AddProgram(waiterProgram);
            uint pokerOffset = pio.AddProgram(pokerProgram);
            PioStateMachine waiter = pio.ClaimStateMachine();
            PioStateMachine poker = pio.ClaimStateMachine();

            try
            {
                waiter.Init(waiterOffset, PioStateMachineConfig.FromProgram(waiterProgram, (int)waiterOffset));
                waiter.Enabled = true;

                Thread.Sleep(100);
                bool ok = waiter.RxLevel == 0;

                poker.Init(pokerOffset, PioStateMachineConfig.FromProgram(pokerProgram, (int)pokerOffset)
                    .ClockDivisor(65535f));
                poker.Enabled = true;

                Thread.Sleep(300);
                ok = ok && waiter.RxLevel > 0;

                return ok;
            }
            finally
            {
                waiter.Dispose();
                poker.Dispose();
                pio.RemoveProgram(waiterProgram, waiterOffset);
                pio.RemoveProgram(pokerProgram, pokerOffset);
            }
        }

        private static bool T21ReadWriteOffsets()
        {
            PioBlock pio = Pio.Get(Pio.MinIndex);
            PioProgram program = EchoProgram(ShiftDirection.Left, 32);
            uint offset = pio.AddProgram(program);
            PioStateMachine sm = pio.ClaimStateMachine();

            try
            {
                sm.Init(offset, PioStateMachineConfig.FromProgram(program, (int)offset));
                sm.Enabled = true;

                uint[] source = new uint[8];
                for (uint i = 0; i < 8; i++)
                {
                    source[i] = 0xFEED0000 + i;
                }

                int wrote = sm.Write(source, 3, 4, 500);
                bool ok = wrote == 4;

                uint[] sink = new uint[8];
                int read = sm.Read(sink, 2, 4, 500);
                ok = ok && read == 4;

                for (uint i = 0; i < 4; i++)
                {
                    ok = ok && sink[2 + i] == 0xFEED0000 + 3 + i;
                }

                ok = ok && sink[0] == 0 && sink[1] == 0 && sink[6] == 0 && sink[7] == 0;

                return ok;
            }
            finally
            {
                sm.Dispose();
                pio.RemoveProgram(program, offset);
            }
        }

        private static bool T22MovOps()
        {
            PioBlock pio = Pio.Get(Pio.MinIndex);

            var asm = new PioAssembler();
            asm.OutShift(ShiftDirection.Left, autoPull: false, threshold: 32);
            asm.InShift(ShiftDirection.Left, autoPush: true, threshold: 32);
            asm.WrapTarget();
            asm.Pull();
            asm.Mov(DestinationOperand.RegisterX, SourceOperand.OutputShiftRegister);
            asm.Mov(DestinationOperand.RegisterY, PioMovOp.Invert, SourceOperand.RegisterX);
            asm.In(SourceOperand.RegisterY, 32);
            asm.Mov(DestinationOperand.RegisterY, PioMovOp.BitReverse, SourceOperand.RegisterX);
            asm.In(SourceOperand.RegisterY, 32);
            asm.Wrap();
            PioProgram program = asm.Build();

            uint offset = pio.AddProgram(program);
            PioStateMachine sm = pio.ClaimStateMachine();

            try
            {
                sm.Init(offset, PioStateMachineConfig.FromProgram(program, (int)offset));
                sm.Enabled = true;

                sm.Put(0xA0000001);

                bool ok = sm.Get() == 0x5FFFFFFE;
                ok = ok && sm.Get() == 0x80000005;

                return ok;
            }
            finally
            {
                sm.Dispose();
                pio.RemoveProgram(program, offset);
            }
        }

        private static bool T23JmpConditions()
        {
            PioBlock pio = Pio.Get(Pio.MinIndex);

            var asm = new PioAssembler();
            asm.InShift(ShiftDirection.Left, autoPush: true, threshold: 32);

            PioLabel countdown = asm.DefineLabel();
            PioLabel notZero = asm.DefineLabel();
            PioLabel emit = asm.DefineLabel();
            PioLabel park = asm.DefineLabel();

            asm.Set(DestinationOperand.RegisterX, 5);
            asm.MarkLabel(countdown);
            asm.Jmp(PioCondition.XPostDec, countdown);
            asm.Set(DestinationOperand.RegisterX, 0);
            asm.Jmp(PioCondition.XZero, emit);
            asm.MarkLabel(notZero);
            asm.Set(DestinationOperand.RegisterY, 0);
            asm.Jmp(park);
            asm.MarkLabel(emit);
            asm.Set(DestinationOperand.RegisterY, 1);
            asm.MarkLabel(park);
            asm.In(SourceOperand.RegisterY, 32);
            asm.Jmp(park);
            PioProgram program = asm.Build();

            uint offset = pio.AddProgram(program);
            PioStateMachine sm = pio.ClaimStateMachine();

            try
            {
                sm.Init(offset, PioStateMachineConfig.FromProgram(program, (int)offset)
                    .Wrap((int)offset + 8, (int)offset + 8));
                sm.Enabled = true;

                Thread.Sleep(50);

                return sm.Get() == 1;
            }
            finally
            {
                sm.Dispose();
                pio.RemoveProgram(program, offset);
            }
        }

        private static bool T24Origin()
        {
            PioBlock pio = Pio.Get(Pio.MinIndex);

            var asm = new PioAssembler();
            asm.SetOrigin(9);
            asm.OutShift(ShiftDirection.Left, autoPull: true, threshold: 32);
            asm.InShift(ShiftDirection.Left, autoPush: true, threshold: 32);
            asm.WrapTarget();
            asm.Out(DestinationOperand.RegisterX, 32);
            asm.In(SourceOperand.RegisterX, 32);
            asm.Wrap();
            PioProgram program = asm.Build();

            bool ok = program.Origin == 9;
            ok = ok && program.Length == 2;
            ok = ok && program.WrapTarget == 0;
            ok = ok && program.Wrap == 1;

            uint offset = pio.AddProgram(program);
            ok = ok && offset == 9;

            PioStateMachine sm = pio.ClaimStateMachine();

            try
            {
                sm.Init(offset, PioStateMachineConfig.FromProgram(program, (int)offset));
                sm.Enabled = true;

                sm.Put(0xBBBBCCCC);
                ok = ok && sm.Get() == 0xBBBBCCCC;

                return ok;
            }
            finally
            {
                sm.Dispose();
                pio.RemoveProgram(program, offset);
            }
        }

        public static uint WokenValue;

        private static bool T25BlockingWake()
        {
            PioBlock pio = Pio.Get(Pio.MinIndex);
            PioProgram program = EchoProgram(ShiftDirection.Left, 32);
            uint offset = pio.AddProgram(program);
            PioStateMachine sm = pio.ClaimStateMachine();

            try
            {
                sm.Init(offset, PioStateMachineConfig.FromProgram(program, (int)offset));
                sm.Enabled = true;

                WokenValue = 0;

                var producer = new Thread(() =>
                {
                    Thread.Sleep(400);
                    ProducerPutTicks = DateTime.UtcNow.Ticks;
                    sm.Put(0x0BADF00D);
                });

                ProducerPutTicks = 0;
                producer.Start();

                WokenValue = sm.Get();

                long wakeTicks = DateTime.UtcNow.Ticks;
                long elapsedMs = ProducerPutTicks == 0
                    ? -1
                    : (wakeTicks - ProducerPutTicks) / TimeSpan.TicksPerMillisecond;
                producer.Join();

                T25Elapsed = (int)elapsedMs;
                T25Value = WokenValue;

                bool ok = WokenValue == 0x0BADF00D;
                ok = ok && elapsedMs >= 0 && elapsedMs < 500;

                return ok;
            }
            finally
            {
                sm.Dispose();
                pio.RemoveProgram(program, offset);
            }
        }

        private static bool T26SideSetOptional()
        {
            PioBlock pio = Pio.Get(Pio.MinIndex);

            var asm = new PioAssembler(new PioAssemblerOptions
            {
                SideSetCount = 1,
                SideSetOption = true,
                SideSetPinDirs = true,
            });

            asm.OutShift(ShiftDirection.Left, autoPull: true, threshold: 32);
            asm.InShift(ShiftDirection.Left, autoPush: true, threshold: 32);

            asm.WrapTarget();
            asm.Out(DestinationOperand.RegisterX, 32).Side(1);
            asm.In(SourceOperand.RegisterX, 32);
            asm.Wrap();
            PioProgram program = asm.Build();

            bool ok = program.SideSetOptional && program.SideSetPinDirs && program.SideSetCount == 1;

            uint offset = pio.AddProgram(program);
            PioStateMachine sm = pio.ClaimStateMachine();

            try
            {
                pio.InitGpioRange(7, 1);

                sm.Init(offset, PioStateMachineConfig.FromProgram(program, (int)offset)
                    .SideSetPins(7));
                sm.Enabled = true;

                sm.Put(0xD00DFEED);
                ok = ok && sm.Get() == 0xD00DFEED;

                return ok;
            }
            finally
            {
                sm.Dispose();
                pio.RemoveProgram(program, offset);
            }
        }

        private static void BeaconPassMask1()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconPassMask2()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconPassMask4()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconPassMask8()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconFailMask1()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconFailMask2()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconFailMask4()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconFailMask8()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconAll15Passed()
        {
            throw new InvalidOperationException();
        }

        private static void FireNibble(int nibble, bool fail)
        {
            for (int bit = 0; bit < 4; bit++)
            {
                if ((nibble & (1 << bit)) == 0)
                {
                    continue;
                }

                try
                {
                    if (fail)
                    {
                        switch (bit)
                        {
                            case 0:
                                BeaconFailMask1();
                                break;
                            case 1:
                                BeaconFailMask2();
                                break;
                            case 2:
                                BeaconFailMask4();
                                break;
                            case 3:
                                BeaconFailMask8();
                                break;
                        }
                    }
                    else
                    {
                        switch (bit)
                        {
                            case 0:
                                BeaconPassMask1();
                                break;
                            case 1:
                                BeaconPassMask2();
                                break;
                            case 2:
                                BeaconPassMask4();
                                break;
                            case 3:
                                BeaconPassMask8();
                                break;
                        }
                    }
                }
                catch (InvalidOperationException)
                {
                }
            }
        }

        private static void BeaconT19Ev0()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconT19Ev1()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconT19EvMany()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconT19FlagBit1()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconT19FlagOther()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconT25ValueOk()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconT25ValueBad()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconT25Fast()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconT25Slow()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconT25Threw()
        {
            throw new InvalidOperationException();
        }

        private static void FireDiagnostics()
        {
            if (_ran[8] && !_passed[8])
            {
                try
                {
                    if (T19EventsSeen == 0)
                    {
                        BeaconT19Ev0();
                    }
                    else if (T19EventsSeen == 1)
                    {
                        BeaconT19Ev1();
                    }
                    else
                    {
                        BeaconT19EvMany();
                    }
                }
                catch (InvalidOperationException)
                {
                }

                try
                {
                    if ((T19FlagsSeen & 0x2) != 0)
                    {
                        BeaconT19FlagBit1();
                    }
                    else
                    {
                        BeaconT19FlagOther();
                    }
                }
                catch (InvalidOperationException)
                {
                }
            }

            if (_ran[14] && !_passed[14])
            {
                try
                {
                    if (T25Elapsed < 0)
                    {
                        BeaconT25Threw();
                    }
                }
                catch (InvalidOperationException)
                {
                }

                if (T25Elapsed >= 0)
                {
                    try
                    {
                        if (T25Value == 0x0BADF00D)
                        {
                            BeaconT25ValueOk();
                        }
                        else
                        {
                            BeaconT25ValueBad();
                        }
                    }
                    catch (InvalidOperationException)
                    {
                    }

                    try
                    {
                        if (T25Elapsed < 0)
                        {
                            BeaconT25Fast();
                        }
                        else
                        {
                            BeaconT25Slow();
                        }
                    }
                    catch (InvalidOperationException)
                    {
                    }
                }
            }
        }

        private static void BeaconGroupA()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconGroupB()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconGroupC()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconGroupD()
        {
            throw new InvalidOperationException();
        }

        private static void FireGroup(int group)
        {
            try
            {
                switch (group)
                {
                    case 0:
                        BeaconGroupA();
                        break;
                    case 1:
                        BeaconGroupB();
                        break;
                    case 2:
                        BeaconGroupC();
                        break;
                    case 3:
                        BeaconGroupD();
                        break;
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        private static void FireResults()
        {
            bool all = true;

            for (int group = 0; group < 4; group++)
            {
                int failNibble = 0;

                for (int bit = 0; bit < 4; bit++)
                {
                    int index = group * 4 + bit + 1;

                    if (index > 15 || !_ran[index])
                    {
                        continue;
                    }

                    if (!_passed[index])
                    {
                        failNibble |= 1 << bit;
                        all = false;
                    }
                }

                if (failNibble != 0)
                {
                    FireGroup(group);
                    FireNibble(failNibble, true);
                }
            }

            FireDiagnostics();

            try
            {
                if (all)
                {
                    BeaconAll15Passed();
                }
            }
            catch (InvalidOperationException)
            {
            }
        }
    }
}
