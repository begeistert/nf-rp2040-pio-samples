using System;
using System.IO;
using RP2040Sharp.NanoFramework.TestKit;
using Xunit;

namespace ForceIrq.Integration.Tests;

/// <summary>
/// Integration test for the ForceIrq sample. Boots the RP2040 nanoCLR firmware on the RP2040Sharp
/// emulator and drives the CLR until a CPU-side <c>ForceIrq</c> has been delivered to the managed
/// handler — validating PioBlock.ForceIrq + the PioIrqDriver event path end to end.
/// </summary>
public class ForceIrqIntegrationTests
{
    private static string FirmwareDir => Path.Combine(AppContext.BaseDirectory, "firmware");
    private static string PeDir => Path.Combine(AppContext.BaseDirectory, "pe");

    private static NanoFirmware Firmware() => NanoFirmware.FromDirectory(FirmwareDir);
    private static NanoApp App() => NanoApp.FromPeDirectory(PeDir, appAssemblyName: "ForceIrq.Sample");


    [Fact]
    public void Delivers_CPU_forced_interrupts_to_a_managed_handler()
    {
        using var clr = NanoClrHarness.Boot(Firmware(), App());

        bool reached = clr.RunUntilStatic(AppSymbols.Assembly, AppSymbols.Fields.IrqCount, v => v.AsInt32 >= 2);
        int irqCount = clr.ReadStaticInt32(AppSymbols.Assembly, AppSymbols.Fields.IrqCount);

        Assert.False(clr.IsLockedUp, "nanoCLR locked up");
        Assert.True(reached, "the CPU-forced interrupt never reached the managed handler");
        Assert.True(irqCount >= 2);
    }

    [Fact]
    public void Deployment_is_compatible_with_the_firmware()
    {
        NanoApp app = App();
        Firmware().AssertCompatible(app); // must not throw
    }
}
