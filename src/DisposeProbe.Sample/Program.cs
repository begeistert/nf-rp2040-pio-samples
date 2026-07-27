using System.Threading;
using nanoFramework.Hardware.Pico.Pio;

namespace DisposeProbeSample
{
    public class Program
    {
        private const int ReadWords = 4;
        private const int ReadTimeoutMs = 30000;
        private const int DisposeDelayMs = 3000;
        private const int HeartbeatPeriodMs = 200;
        private const int UnusedIrqFlag = 7;

        public static int ReadResult = -1;
        public static int DisposeThrew;
        public static int ReadThrew;
        public static int BeatsAfter;

        public static void Main()
        {
            var asm = new PioAssembler();
            asm.WrapTarget();
            asm.Pull(ifEmpty: false, block: true);
            asm.Push(ifFull: false, block: true);
            asm.Wrap();
            PioProgram program = asm.Build();

            PioBlock pio = Pio.Get(0);
            uint offset = pio.AddProgram(program);
            PioStateMachine sm = pio.ClaimStateMachine();
            sm.Init(offset, PioStateMachineConfig.FromProgram(program, (int)offset)
                .ClockFromFrequency(1_000_000f));
            sm.ClearFifos();

            var disposer = new Thread(() =>
            {
                Thread.Sleep(DisposeDelayMs);

                try
                {
                    sm.Dispose();
                }
                catch
                {
                    DisposeThrew = 1;
                }
            });
            disposer.Start();

            try
            {
                ReadResult = sm.Read(new uint[ReadWords], 0, ReadWords, ReadTimeoutMs);
            }
            catch
            {
                ReadThrew = 1;
            }

            disposer.Join();

            while (true)
            {
                pio.ClearIrq(UnusedIrqFlag);
                Thread.Sleep(HeartbeatPeriodMs);
                BeatsAfter++;
            }
        }
    }
}
