using System;
using System.IO;
using RP2040Sharp.NanoFramework.TestKit;
using RP2040.TestKit.Probes;
using Xunit;

namespace Coverage.Integration.Tests;

/// <summary>
/// Boots the Coverage sample on the emulated RP2040 nanoCLR and verifies the native PIO methods the
/// other showcases don't reach. Where a method has a FIFO-observable effect we assert it through the
/// <see cref="PioProbe"/> (the actual words the firmware pushed/pulled), exactly like Echo — not just a
/// value the app recorded. The methods with no FIFO-observable effect (SetClockDivisor/ClockDivRestart,
/// ClearIrq, Unclaim) are honest smoke checks, documented as such. (Test kit BUSL; sample + library MIT.)
/// </summary>
public class CoverageIntegrationTests
{
    // Mirror of CoverageSample.Program's sentinels (separate assembly, so duplicated here).
    private const uint ExecSentinel = 21;
    private const uint RestartSentinel = 0xBEEF;
    private const uint DrainKept = 0xDDDD0003;
    private const uint DrainDropped1 = 0xDDDD0001;
    private const uint DrainDropped2 = 0xDDDD0002;
    private const uint ClockSentinel = 0xC0DE;
    private const uint ReuseSentinel = 0xABCDABCD;

    private static string FirmwareDir => Path.Combine(AppContext.BaseDirectory, "firmware");
    private static string PeDir => Path.Combine(AppContext.BaseDirectory, "pe");

    private static NanoFirmware Firmware() => NanoFirmware.FromDirectory(FirmwareDir);
    private static NanoApp App() => NanoApp.FromPeDirectory(PeDir, appAssemblyName: "Coverage.Sample");

    private static int Read(NanoClrHarness clr, string field) =>
        clr.ReadStaticInt32(AppSymbols.Assembly, field);

    private static bool IsDone(NanoClrHarness clr)
    {
        // The static read throws until the app assembly has been loaded; treat that as "not done yet".
        try
        {
            return Read(clr, AppSymbols.Fields.Done) != 0;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static NanoClrHarness BootUntilDone(out PioProbe pio)
    {
        var clr = NanoClrHarness.Boot(Firmware(), App());
        clr.Pico.AddPioProbe(0, out pio); // capture block-0 FIFO traffic from the very first cycle
        for (int i = 0; i < 30000 && !clr.IsLockedUp; i++)
        {
            clr.Pico.RunMicroseconds(100);
            if (IsDone(clr))
            {
                break;
            }
        }

        return clr;
    }

    [Fact]
    public void Exercises_the_remaining_native_methods_observed_through_the_FIFO()
    {
        using var clr = BootUntilDone(out PioProbe pio);

        Assert.False(clr.IsLockedUp, "nanoCLR locked up");
        Assert.Equal(1, Read(clr, AppSymbols.Fields.Done));

        var rx = pio.RxOf(0);
        var tx = pio.TxOf(0);

        // Exec ran out of band: its sentinel is in RX but never in TX.
        Assert.Contains(ExecSentinel, rx);
        Assert.DoesNotContain(ExecSentinel, tx);

        // RemoveProgram: slots reclaimed (reuse offset 0) and the reloaded program echoed (RemoveEchoOk).
        Assert.Equal(0, Read(clr, AppSymbols.Fields.RemoveReuseOffset));
        Assert.Equal(1, Read(clr, AppSymbols.Fields.RemoveEchoOk));

        // Restart: the state machine resumed and still echoed after a restart.
        Assert.Contains(RestartSentinel, rx);

        // DrainTxFifo (smoke): ran without lockup; the drop isn't observable on this emulator, so the
        // 0x8000 (PULL NoBlock) native fix is verified by semantics and real hardware.
        Assert.Contains(DrainDropped1, tx);
        Assert.Contains(DrainKept, rx);

        // SetClockDivisor / ClockDivRestart (smoke): SM still echoes after re-clocking.
        Assert.Contains(ClockSentinel, rx);

        // GetRxLevel cross-check: the native FLEVEL read saw words actually sitting in RX.
        Assert.True(Read(clr, AppSymbols.Fields.RxLevelSeen) > 0, "GetRxLevel reported nothing in RX");

        // Unclaim (Dispose) freed a state machine on an otherwise-full block (bookkeeping, not FIFO).
        Assert.Equal(1, Read(clr, AppSymbols.Fields.UnclaimWorks));
    }

    [Fact]
    public void Rejects_invalid_input()
    {
        using var clr = BootUntilDone(out _);

        Assert.False(clr.IsLockedUp, "nanoCLR locked up");
        Assert.Equal(1, Read(clr, AppSymbols.Fields.Done));

        // Reaches the native validation (not pre-checked by the managed layer):
        Assert.Equal(1, Read(clr, AppSymbols.Fields.RemoveUnownedThrew)); // RemoveProgram ownership check
        Assert.Equal(1, Read(clr, AppSymbols.Fields.InitBadOffsetThrew)); // Init offset 0..31 check
        // Caught at the managed layer (the native overflow guards sit behind these):
        Assert.Equal(1, Read(clr, AppSymbols.Fields.PinDirsBadThrew));
        Assert.Equal(1, Read(clr, AppSymbols.Fields.InitGpioBadThrew));
    }

    [Fact]
    public void Deployment_is_compatible_with_the_firmware()
    {
        Firmware().AssertCompatible(App());
    }
}
