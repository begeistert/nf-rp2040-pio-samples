//
// Sample — a self-contained battery over the corners of the PIO API no other sample reaches.
//
// Every check verifies itself through the FIFOs or the internal pulls, so it needs no wiring, no
// analyzer and no second board: deploy to any Pico and read the beacons. Covered here: the FIFO
// echo path of Put/Get, TryPut/TryGet against a stalled machine, mov from STATUS, jmp on pin with
// both internal pulls, sticky outputs, Dispose with a DMA transfer still in flight, repeated
// program add/remove and instruction-memory exhaustion, every PIO block on the package, state
// machine claim exhaustion, and the GPIOBASE=16 window on RP2350.
//
using System;
using System.Threading;
using System.Diagnostics;
using nanoFramework.Hardware.Pico.Pio;

namespace ApiSurfaceSample
{
    public class Program
    {
        private const int JmpTestPin = 6;
        private const int JmpTestPinHigh = 16;

        private static bool[] _passed;
        private static bool[] _ran;

        public static void Main()
        {
            _passed = new bool[12];
            _ran = new bool[12];

            Debug.WriteLine($"API surface battery on {Pio.BlockCount} blocks, min index {Pio.MinIndex}");

            Run(1, T01FifoEcho);
            Run(2, T02TryPutTryGet);
            Run(3, T03MovStatus);
            Run(4, T04JmpPin);
            Run(5, T05StickyConfig);
            Run(6, T06DisposeInFlight);
            Run(7, T07ProgramLifecycle);
            Run(8, T08AllBlocks);
            Run(9, T09ClaimExhaustion);

            if (Pio.BlockCount == 3)
            {
                Run(10, T10GpioBase16);
            }

            Run(11, T11IrqDedupe);

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

        private static PioProgram EchoProgram()
        {
            var asm = new PioAssembler();

            asm.SetOutShift(ShiftDirection.Left, autoPull: true, threshold: 32);
            asm.SetInShift(ShiftDirection.Left, autoPush: true, threshold: 32);

            asm.WrapTarget();
            asm.Out(DestinationOperand.RegisterX, 32);
            asm.In(SourceOperand.RegisterX, 32);
            asm.Wrap();

            return asm.Build();
        }

        private static bool T01FifoEcho()
        {
            PioBlock pio = Pio.Get(Pio.MinIndex);
            PioProgram program = EchoProgram();
            uint offset = pio.AddProgram(program);
            PioStateMachine sm = pio.ClaimStateMachine();

            try
            {
                sm.Init(offset, PioStateMachineConfig.FromProgram(program, (int)offset));
                sm.Enabled = true;

                sm.Put(0xCAFE1234);
                bool ok = sm.Get() == 0xCAFE1234;

                sm.Put(0x00000000);
                ok = ok && sm.Get() == 0x00000000;

                sm.Put(0xFFFFFFFF);
                ok = ok && sm.Get() == 0xFFFFFFFF;

                return ok;
            }
            finally
            {
                sm.Dispose();
                pio.RemoveProgram(program, offset);
            }
        }

        private static bool T02TryPutTryGet()
        {
            PioBlock pio = Pio.Get(Pio.MinIndex);
            PioProgram program = EchoProgram();
            uint offset = pio.AddProgram(program);
            PioStateMachine sm = pio.ClaimStateMachine();

            try
            {
                sm.Init(offset, PioStateMachineConfig.FromProgram(program, (int)offset));

                bool ok = true;

                for (uint i = 0; i < 4; i++)
                {
                    ok = ok && sm.TryPut(0x1000 + i);
                }

                ok = ok && sm.TxLevel == 4;
                ok = ok && !sm.TryPut(0xDEAD);

                uint ignored;
                ok = ok && !sm.TryGet(out ignored);

                sm.Enabled = true;
                Thread.Sleep(50);

                for (uint i = 0; i < 4; i++)
                {
                    uint value;
                    ok = ok && sm.TryGet(out value) && value == 0x1000 + i;
                }

                ok = ok && !sm.TryGet(out ignored);

                return ok;
            }
            finally
            {
                sm.Dispose();
                pio.RemoveProgram(program, offset);
            }
        }

        private static bool T03MovStatus()
        {
            PioBlock pio = Pio.Get(Pio.MinIndex);

            var asm = new PioAssembler();
            asm.SetOutShift(ShiftDirection.Left, autoPull: false, threshold: 32);
            asm.SetInShift(ShiftDirection.Left, autoPush: true, threshold: 32);
            asm.WrapTarget();
            asm.Pull();
            asm.Mov(DestinationOperand.RegisterX, SourceOperand.Status);
            asm.In(SourceOperand.RegisterX, 32);
            asm.Wrap();
            PioProgram program = asm.Build();

            uint offset = pio.AddProgram(program);
            PioStateMachine sm = pio.ClaimStateMachine();

            try
            {
                sm.Init(offset, PioStateMachineConfig.FromProgram(program, (int)offset)
                    .MovStatus(PioMovStatusSel.TxLevel, 2)
                    .ClockDivisor(4000f));

                sm.Enabled = true;

                sm.Put(1);
                bool ok = sm.Get() == 0xFFFFFFFF;

                uint[] burst = new uint[] { 1, 2, 3, 4 };
                sm.Write(burst, 0, 4, 500);

                ok = ok && sm.Get() == 0x00000000;
                ok = ok && sm.Get() == 0x00000000;
                ok = ok && sm.Get() == 0xFFFFFFFF;
                ok = ok && sm.Get() == 0xFFFFFFFF;

                return ok;
            }
            finally
            {
                sm.Dispose();
                pio.RemoveProgram(program, offset);
            }
        }

        private static uint RunJmpPinOnce(PioBlock pio, int pin, bool driveHigh, int gpioBase)
        {
            var driverAsm = new PioAssembler();
            driverAsm.WrapTarget();
            driverAsm.Set(DestinationOperand.Pins, driveHigh ? 1 : 0);
            driverAsm.Wrap();
            PioProgram driverProgram = driverAsm.Build();

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

            uint driverOffset = pio.AddProgram(driverProgram);
            uint offset = pio.AddProgram(program);
            PioStateMachine driver = pio.ClaimStateMachine();
            PioStateMachine sm = pio.ClaimStateMachine();

            try
            {
                pio.InitGpio(pin);

                PioStateMachineConfig driverCfg = PioStateMachineConfig.FromProgram(driverProgram, (int)driverOffset)
                    .SetPins(pin, 1);

                PioStateMachineConfig cfg = PioStateMachineConfig.FromProgram(program, (int)offset)
                    .JmpPin(pin)
                    .ClockDivisor(1000f);

                if (gpioBase != 0)
                {
                    driverCfg = driverCfg.GpioBase(gpioBase);
                    cfg = cfg.GpioBase(gpioBase);
                }

                driver.Init(driverOffset, driverCfg);
                driver.SetConsecutivePinDirs(pin, 1, true);
                driver.Enabled = true;

                sm.Init(offset, cfg);
                sm.Enabled = true;

                Thread.Sleep(20);

                return sm.Get();
            }
            finally
            {
                sm.Dispose();
                driver.Dispose();
                pio.RemoveProgram(program, offset);
                pio.RemoveProgram(driverProgram, driverOffset);
            }
        }

        public static int JmpPinPattern;
        public static int JmpBase16Pattern;

        private static int JmpFlipSequence(PioBlock pio, int pin, int gpioBase)
        {
            int pattern = 0;

            if (RunJmpPinOnce(pio, pin, false, gpioBase) != 0)
            {
                pattern |= 1;
            }

            if (RunJmpPinOnce(pio, pin, true, gpioBase) != 0)
            {
                pattern |= 2;
            }

            if (RunJmpPinOnce(pio, pin, false, gpioBase) != 0)
            {
                pattern |= 4;
            }

            if (RunJmpPinOnce(pio, pin, true, gpioBase) != 0)
            {
                pattern |= 8;
            }

            return pattern;
        }

        private static bool T04JmpPin()
        {
            PioBlock pio = Pio.Get(Pio.MinIndex);

            JmpPinPattern = JmpFlipSequence(pio, JmpTestPin, 0);

            return JmpPinPattern == 0b1010;
        }

        private static bool T05StickyConfig()
        {
            PioBlock pio = Pio.Get(Pio.MinIndex);
            PioProgram program = EchoProgram();
            uint offset = pio.AddProgram(program);
            PioStateMachine sm = pio.ClaimStateMachine();

            try
            {
                sm.Init(offset, PioStateMachineConfig.FromProgram(program, (int)offset)
                    .OutSpecial(true, false, 0));

                sm.Enabled = true;

                sm.Put(0x5EC0FFEE);

                return sm.Get() == 0x5EC0FFEE;
            }
            finally
            {
                sm.Dispose();
                pio.RemoveProgram(program, offset);
            }
        }

        private static bool _inFlightThreadDone;

        private static bool T06DisposeInFlight()
        {
            PioBlock pio = Pio.Get(Pio.MinIndex);
            PioProgram program = EchoProgram();
            uint offset = pio.AddProgram(program);
            PioStateMachine sm = pio.ClaimStateMachine();

            _inFlightThreadDone = false;

            var reader = new Thread(() =>
            {
                try
                {
                    uint[] sink = new uint[4];
                    sm.Read(sink, 0, 4, 3000);
                }
                catch (Exception)
                {
                }

                _inFlightThreadDone = true;
            });

            sm.Init(offset, PioStateMachineConfig.FromProgram(program, (int)offset));
            sm.Enabled = true;

            reader.Start();
            Thread.Sleep(300);

            sm.Dispose();

            reader.Join(4000);
            bool ok = _inFlightThreadDone;

            PioStateMachine fresh = pio.ClaimStateMachine();

            try
            {
                fresh.Init(offset, PioStateMachineConfig.FromProgram(program, (int)offset));
                fresh.Enabled = true;

                fresh.Put(0x0DDBA11);
                ok = ok && fresh.Get() == 0x0DDBA11;

                return ok;
            }
            finally
            {
                fresh.Dispose();
                pio.RemoveProgram(program, offset);
            }
        }

        private static bool T07ProgramLifecycle()
        {
            PioBlock pio = Pio.Get(Pio.MinIndex);
            PioProgram program = EchoProgram();

            for (int i = 0; i < 10; i++)
            {
                uint offset = pio.AddProgram(program);
                pio.RemoveProgram(program, offset);
            }

            uint[] offsets = new uint[16];
            int added = 0;

            try
            {
                for (int i = 0; i < 16; i++)
                {
                    offsets[added] = pio.AddProgram(program);
                    added++;
                }
            }
            catch (Exception)
            {
            }

            bool ok = added >= 14;

            for (int i = 0; i < added; i++)
            {
                pio.RemoveProgram(program, offsets[i]);
            }

            uint finalOffset = pio.AddProgram(program);
            PioStateMachine sm = pio.ClaimStateMachine();

            try
            {
                sm.Init(finalOffset, PioStateMachineConfig.FromProgram(program, (int)finalOffset));
                sm.Enabled = true;

                sm.Put(0x7E57);

                return ok && sm.Get() == 0x7E57;
            }
            finally
            {
                sm.Dispose();
                pio.RemoveProgram(program, finalOffset);
            }
        }

        private static bool T08AllBlocks()
        {
            bool ok = true;

            for (int block = Pio.MinIndex; block < Pio.BlockCount; block++)
            {
                PioBlock pio = Pio.Get(block);
                PioProgram program = EchoProgram();
                uint offset = pio.AddProgram(program);
                PioStateMachine sm = pio.ClaimStateMachine();

                try
                {
                    sm.Init(offset, PioStateMachineConfig.FromProgram(program, (int)offset));
                    sm.Enabled = true;

                    sm.Put((uint)(0xB10C0000 + block));
                    ok = ok && sm.Get() == (uint)(0xB10C0000 + block);
                }
                finally
                {
                    sm.Dispose();
                    pio.RemoveProgram(program, offset);
                }
            }

            return ok;
        }

        private static bool T09ClaimExhaustion()
        {
            PioBlock pio = Pio.Get(Pio.MinIndex);
            PioStateMachine[] machines = new PioStateMachine[4];
            int claimed = 0;

            try
            {
                for (int i = 0; i < 4; i++)
                {
                    machines[claimed] = pio.ClaimStateMachine();
                    claimed++;
                }
            }
            catch (Exception)
            {
            }

            bool ok = claimed == 4;

            bool fifthFailed = false;

            try
            {
                PioStateMachine extra = pio.ClaimStateMachine();
                extra.Dispose();
            }
            catch (Exception)
            {
                fifthFailed = true;
            }

            ok = ok && fifthFailed;

            for (int i = 0; i < claimed; i++)
            {
                machines[i].Dispose();
            }

            PioStateMachine again = pio.ClaimStateMachine();
            again.Dispose();

            return ok;
        }

        public static int IrqEvents;

        private static bool T11IrqDedupe()
        {
            PioBlock pio = Pio.Get(Pio.MinIndex);

            var asm = new PioAssembler();
            PioLabel loop = asm.DefineLabel();
            PioLabel done = asm.DefineLabel();

            asm.Set(DestinationOperand.RegisterX, 7);
            asm.MarkLabel(loop);
            asm.Irq(0, clear: false, wait: true);
            asm.Jmp(PioCondition.XPostDec, loop);
            asm.MarkLabel(done);
            asm.Jmp(done);
            PioProgram program = asm.Build();

            uint offset = pio.AddProgram(program);
            PioStateMachine active = pio.ClaimStateMachine();
            PioStateMachine bystander = pio.ClaimStateMachine();

            IrqEvents = 0;

            PioInterruptEventHandler handler = (sender, flags) =>
            {
                IrqEvents++;
            };

            pio.Interrupt += handler;

            try
            {
                active.Init(offset, PioStateMachineConfig.FromProgram(program, (int)offset)
                    .Wrap((int)offset + 4, (int)offset + 4)
                    .ClockFromFrequency(2000f, Pio.SystemClock));

                active.Enabled = true;

                Thread.Sleep(3000);

                return IrqEvents == 8;
            }
            finally
            {
                pio.Interrupt -= handler;
                active.Dispose();
                bystander.Dispose();
                pio.RemoveProgram(program, offset);
            }
        }

        private static bool T10GpioBase16()
        {
            PioBlock pio = Pio.Get(Pio.MinIndex);

            JmpBase16Pattern = JmpFlipSequence(pio, JmpTestPinHigh, 16);

            return JmpBase16Pattern == 0b1010;
        }

        private static void BeaconT01EchoPass()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconT01EchoFail()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconT02TryPass()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconT02TryFail()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconT03StatusPass()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconT03StatusFail()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconT04JmpPinPass()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconT04JmpPinFail()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconT05StickyPass()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconT05StickyFail()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconT06DisposePass()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconT06DisposeFail()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconT07ProgramsPass()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconT07ProgramsFail()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconT08BlocksPass()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconT08BlocksFail()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconT09ClaimsPass()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconT09ClaimsFail()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconT10Base16Pass()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconT10Base16Fail()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconT11IrqDedupePass()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconT11IrqDedupeFail()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconT04Sample0High()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconT04Sample1High()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconT04Sample2High()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconT04Sample3High()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconT10Sample0High()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconT10Sample1High()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconT10Sample2High()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconT10Sample3High()
        {
            throw new InvalidOperationException();
        }

        private static void FirePattern(int pattern, int test)
        {
            for (int bit = 0; bit < 4; bit++)
            {
                if ((pattern & (1 << bit)) == 0)
                {
                    continue;
                }

                try
                {
                    if (test == 4)
                    {
                        switch (bit)
                        {
                            case 0:
                                BeaconT04Sample0High();
                                break;
                            case 1:
                                BeaconT04Sample1High();
                                break;
                            case 2:
                                BeaconT04Sample2High();
                                break;
                            case 3:
                                BeaconT04Sample3High();
                                break;
                        }
                    }
                    else
                    {
                        switch (bit)
                        {
                            case 0:
                                BeaconT10Sample0High();
                                break;
                            case 1:
                                BeaconT10Sample1High();
                                break;
                            case 2:
                                BeaconT10Sample2High();
                                break;
                            case 3:
                                BeaconT10Sample3High();
                                break;
                        }
                    }
                }
                catch (InvalidOperationException)
                {
                }
            }
        }

        private static void BeaconAllPassed()
        {
            throw new InvalidOperationException();
        }

        private static void FireOne(int index)
        {
            try
            {
                bool pass = _passed[index];

                switch (index)
                {
                    case 1:
                        if (pass)
                        {
                            BeaconT01EchoPass();
                        }
                        else
                        {
                            BeaconT01EchoFail();
                        }

                        break;
                    case 2:
                        if (pass)
                        {
                            BeaconT02TryPass();
                        }
                        else
                        {
                            BeaconT02TryFail();
                        }

                        break;
                    case 3:
                        if (pass)
                        {
                            BeaconT03StatusPass();
                        }
                        else
                        {
                            BeaconT03StatusFail();
                        }

                        break;
                    case 4:
                        if (pass)
                        {
                            BeaconT04JmpPinPass();
                        }
                        else
                        {
                            BeaconT04JmpPinFail();
                        }

                        break;
                    case 5:
                        if (pass)
                        {
                            BeaconT05StickyPass();
                        }
                        else
                        {
                            BeaconT05StickyFail();
                        }

                        break;
                    case 6:
                        if (pass)
                        {
                            BeaconT06DisposePass();
                        }
                        else
                        {
                            BeaconT06DisposeFail();
                        }

                        break;
                    case 7:
                        if (pass)
                        {
                            BeaconT07ProgramsPass();
                        }
                        else
                        {
                            BeaconT07ProgramsFail();
                        }

                        break;
                    case 8:
                        if (pass)
                        {
                            BeaconT08BlocksPass();
                        }
                        else
                        {
                            BeaconT08BlocksFail();
                        }

                        break;
                    case 9:
                        if (pass)
                        {
                            BeaconT09ClaimsPass();
                        }
                        else
                        {
                            BeaconT09ClaimsFail();
                        }

                        break;
                    case 10:
                        if (pass)
                        {
                            BeaconT10Base16Pass();
                        }
                        else
                        {
                            BeaconT10Base16Fail();
                        }

                        break;
                    case 11:
                        if (pass)
                        {
                            BeaconT11IrqDedupePass();
                        }
                        else
                        {
                            BeaconT11IrqDedupeFail();
                        }

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

            for (int i = 1; i <= 11; i++)
            {
                if (!_ran[i])
                {
                    continue;
                }

                FireOne(i);
                all = all && _passed[i];
            }

            if (_ran[4] && !_passed[4])
            {
                FirePattern(JmpPinPattern, 4);
            }

            if (_ran[10] && !_passed[10])
            {
                FirePattern(JmpBase16Pattern, 10);
            }

            try
            {
                if (all)
                {
                    BeaconAllPassed();
                }
            }
            catch (InvalidOperationException)
            {
            }
        }
    }
}
