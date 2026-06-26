using System;
using System.IO;
using RP2040Sharp.NanoFramework.TestKit;
using Xunit;

namespace Irq.Integration.Tests;

/// <summary>
/// Integration test for the Irq sample. Boots the RP2040 nanoCLR firmware on the RP2040Sharp
/// emulator (which models the PIO IRQ → NVIC path), deploys the managed app, and drives the CLR
/// until a PIO state machine's <c>irq</c> instruction has been delivered to the managed handler a
/// couple of times — validating the native PioIrqDriver + NativeEventDispatcher path end to end
/// (the test kit is the author's BUSL offering; the sample app and the PIO library are MIT).
/// </summary>
public class IrqIntegrationTests
{
    private static string FirmwareDir => Path.Combine(AppContext.BaseDirectory, "firmware");
    private static string PeDir => Path.Combine(AppContext.BaseDirectory, "pe");

    private static NanoFirmware Firmware() => NanoFirmware.FromDirectory(FirmwareDir);
    private static NanoApp App() => NanoApp.FromPeDirectory(PeDir, appAssemblyName: "Irq.Sample");


    [Fact]
    public void Delivers_PIO_interrupts_to_a_managed_handler()
    {
        using var clr = NanoClrHarness.Boot(Firmware(), App());

        // Run the deployed C# app until the PIO irq -> NVIC -> managed event path has fired twice.
        bool reached = clr.RunUntilStatic(AppSymbols.Assembly, AppSymbols.Fields.IrqCount, v => v.AsInt32 >= 2);
        int irqCount = clr.ReadStaticInt32(AppSymbols.Assembly, AppSymbols.Fields.IrqCount);

        Assert.False(clr.IsLockedUp, "nanoCLR locked up");
        Assert.True(reached, "the PIO interrupt never reached the managed handler");
        Assert.True(irqCount >= 2);
    }

    [Fact]
    public void Keeps_delivering_interrupts_without_stalling()
    {
        using var clr = NanoClrHarness.Boot(Firmware(), App());

        clr.RunUntilStatic(AppSymbols.Assembly, AppSymbols.Fields.IrqCount, v => v.AsInt32 >= 2);
        int first = clr.ReadStaticInt32(AppSymbols.Assembly, AppSymbols.Fields.IrqCount);

        // The IRQ -> NVIC -> event path must keep firing: the ISR critical section holds up under
        // repeated delivery instead of corrupting the HAL event queue and stalling.
        bool grew = clr.RunUntilStatic(AppSymbols.Assembly, AppSymbols.Fields.IrqCount, v => v.AsInt32 >= first + 5);
        int later = clr.ReadStaticInt32(AppSymbols.Assembly, AppSymbols.Fields.IrqCount);

        Assert.False(clr.IsLockedUp, "nanoCLR locked up");
        Assert.True(grew && later >= first + 5, $"interrupts stopped flowing: {first} -> {later}");
    }

    [Fact]
    public void Deployment_is_compatible_with_the_firmware()
    {
        NanoApp app = App();
        Firmware().AssertCompatible(app); // must not throw
    }
}
