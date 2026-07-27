//
// Diagnostic probe — the control experiment for WaitProbe.Sample.
//
// WaitProbe showed that the CLR scheduler stops for good once a native waits with a timeout above
// ~1350 ms, and keeps running fine below it. That native is PioStateMachine.Read(), so the finding
// could still be blamed on the PIO binding: its shared event flag, its Events_Get() call, its
// eval-stack handling.
//
// This probe removes the binding from the picture entirely. AutoResetEvent.WaitOne(timeout) waits
// on the very same CLR machinery (a timed wait that parks the managed thread) without touching PIO,
// DMA, Events_Set, Events_Get or any native of the binding. The event is never signalled, so every
// wait expires exactly like the Read did.
//
// Read the result as:
//
//   stalls at the same threshold  -> the binding is not involved at all; the defect is in how the
//                                    CLR computes or schedules a long timeout
//   never stalls                  -> something specific to the PIO native path matters after all,
//                                    and the binding goes back under the microscope
//
// The heartbeat is Thread.Sleep: breakpoint on
// Library_corlib_native_System_Threading_Thread::Sleep___STATIC__VOID__I4 (0x10027bd8 in the
// current build) and every beat stops there. Nothing is written to the debug channel on purpose,
// so a saturated Wire Protocol buffer cannot be confused with the stall.
//
// Result with this firmware: 1400 ms stalls after the three rounds complete, exactly like the PIO
// probe. Removing the debug output changed nothing, which rules the debug channel out too.
//
using System.Threading;

namespace EventProbeSample
{
    public class Program
    {
        private const int WaitTimeoutMs = 1400;
        private const int WaitRounds = 3;
        private const int HeartbeatPeriodMs = 200;
        private const int HeartbeatsBefore = 3;

        public static int BeatsBefore;
        public static int BeatsAfter;
        public static int WaitsCompleted;

        public static void Main()
        {
            // Never signalled by anyone, so every WaitOne below runs to its full timeout.
            var never = new AutoResetEvent(false);

            for (int i = 0; i < HeartbeatsBefore; i++)
            {
                Thread.Sleep(HeartbeatPeriodMs);
                BeatsBefore++;
            }

            for (int round = 1; round <= WaitRounds; round++)
            {
                bool signalled = never.WaitOne(WaitTimeoutMs, false);
                WaitsCompleted++;

                Thread.Sleep(HeartbeatPeriodMs);
                BeatsAfter++;
            }

            while (true)
            {
                Thread.Sleep(HeartbeatPeriodMs);
                BeatsAfter++;
            }
        }
    }
}
