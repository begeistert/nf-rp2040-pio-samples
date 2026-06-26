using System;
using System.IO;
using RP2040Sharp.NanoFramework.TestKit;
using Xunit;

namespace Coverage.Integration.Tests;

/// <summary>
/// Boots the Coverage sample on the emulated RP2040 nanoCLR and checks the native PIO methods the
/// other showcases don't exercise (RemoveProgram, Exec, Restart, ClockDivRestart, DrainTxFifo,
/// Unclaim, GetRxLevel, SetClockDivisor, ClearIrq) plus the input-validation paths. The sample
/// records its results into static fields and sets Done last. (Test kit BUSL; sample + library MIT.)
/// </summary>
public class CoverageIntegrationTests
{
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

    private static NanoClrHarness BootUntilDone()
    {
        var clr = NanoClrHarness.Boot(Firmware(), App());
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
    public void Exercises_the_remaining_native_methods()
    {
        using var clr = BootUntilDone();

        Assert.False(clr.IsLockedUp, "nanoCLR locked up");
        Assert.Equal(1, Read(clr, AppSymbols.Fields.Done));

        // RemoveProgram reclaimed the slots, so the re-added program reuses offset 0.
        Assert.Equal(0, Read(clr, AppSymbols.Fields.RemoveReuseOffset));
        // Unclaim (Dispose) freed a state machine on an otherwise-full block.
        Assert.Equal(1, Read(clr, AppSymbols.Fields.UnclaimWorks));
        // Exec / Restart / ClockDivRestart / DrainTxFifo / SetClockDivisor / ClearIrq all ran.
        Assert.Equal(6, Read(clr, AppSymbols.Fields.MethodsRun));
        // GetRxLevel returned a valid FIFO level.
        Assert.InRange(Read(clr, AppSymbols.Fields.RxLevel), 0, 8);
    }

    [Fact]
    public void Rejects_invalid_input()
    {
        using var clr = BootUntilDone();

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
