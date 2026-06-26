using System;
using System.IO;
using RP2040.NanoFramework.TestKit;
using Xunit;
using Xunit.Abstractions;

namespace Echo.Integration.Tests;

/// <summary>
/// Integration test for the Echo sample. Boots the RP2040 nanoCLR firmware on the RP2040Sharp
/// emulator, deploys the managed app, and drives the CLR until the app has round-tripped a couple
/// of words through the PIO FIFOs — validating the non-blocking FIFO API (TryPut/TryGet),
/// ClearFifos, and ClockFromFrequency end to end (the test kit is the author's BUSL offering;
/// the sample app and the PIO library are MIT).
/// </summary>
public class EchoIntegrationTests
{
    private static string FirmwareDir => Path.Combine(AppContext.BaseDirectory, "firmware");
    private static string PeDir => Path.Combine(AppContext.BaseDirectory, "pe");

    private static NanoFirmware Firmware() => NanoFirmware.FromDirectory(FirmwareDir);
    private static NanoApp App() => NanoApp.FromPeDirectory(PeDir, appAssemblyName: "Echo.Sample");

    private readonly ITestOutputHelper _out;
    public EchoIntegrationTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Loops_words_through_the_PIO_FIFOs()
    {
        using var clr = NanoClrHarness.Boot(Firmware(), App());

        // Run the deployed C# app until the PIO loopback has echoed a couple of words back.
        bool reached = clr.RunUntilStatic(AppSymbols.Assembly, AppSymbols.Fields.Echoed, v => v.AsInt32 >= 2);
        int echoed = clr.ReadStaticInt32(AppSymbols.Assembly, AppSymbols.Fields.Echoed);

        _out.WriteLine($"Echoed = {echoed} after {clr.InstructionCount} instructions");
        Assert.True(reached, "the echo app never round-tripped a word through the PIO FIFOs");
        Assert.True(echoed >= 2);
    }

    [Fact]
    public void Deployment_is_compatible_with_the_firmware()
    {
        // The native checksum changes whenever the PIO library's InternalCall surface changes, so we
        // assert compatibility against the bundled firmware rather than hard-coding a literal value.
        NanoApp app = App();
        Firmware().AssertCompatible(app); // must not throw
    }
}
