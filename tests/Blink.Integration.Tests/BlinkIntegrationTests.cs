using System;
using System.Collections.Generic;
using System.IO;
using RP2040Sharp.NanoFramework.TestKit;
using RP2040.TestKit.Probes;
using Xunit;
using Xunit.Abstractions;

namespace Blink.Integration.Tests;

/// <summary>
/// Integration test for the Blink sample. Boots the RP2040 nanoCLR firmware on the RP2040Sharp
/// emulator, deploys the managed app, and drives the CLR until the app has toggled the LED through
/// a PIO state machine — validating the assembler + native interop end to end (the test kit is the
/// author's BUSL offering; the sample app and the PIO library are MIT).
/// </summary>
public class BlinkIntegrationTests
{
    private const int LedPin = 25;

    private static string FirmwareDir => Path.Combine(AppContext.BaseDirectory, "firmware");
    private static string PeDir => Path.Combine(AppContext.BaseDirectory, "pe");

    private static NanoFirmware Firmware() => NanoFirmware.FromDirectory(FirmwareDir);
    private static NanoApp App() => NanoApp.FromPeDirectory(PeDir, appAssemblyName: "Blink.Sample");

    private readonly ITestOutputHelper _out;
    public BlinkIntegrationTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Drives_the_LED_pin_through_a_PIO_state_machine()
    {
        using var clr = NanoClrHarness.Boot(Firmware(), App());

        // Run the deployed C# app until it has pushed a couple of toggles through the PIO FIFO.
        bool reached = clr.RunUntilStatic(AppSymbols.Assembly, AppSymbols.Fields.Toggles, v => v.AsInt32 >= 2);
        int toggles = clr.ReadStaticInt32(AppSymbols.Assembly, AppSymbols.Fields.Toggles);

        _out.WriteLine($"Toggles = {toggles} after {clr.InstructionCount} instructions");
        Assert.True(reached, "the blink app never toggled the LED");
        Assert.True(toggles >= 2);
    }

    [Fact]
    public void Drives_the_LED_pin_with_alternating_levels()
    {
        using var clr = NanoClrHarness.Boot(Firmware(), App());
        clr.Pico.AddPioProbe(0, out PioProbe pio);

        // Toggles are 500 ms apart (Thread.Sleep), so step in coarse chunks until four have landed.
        for (int i = 0; i < 8000 && pio.TxOf(0).Count < 4; i++)
            clr.Pico.RunMicroseconds(500);

        Assert.False(clr.IsLockedUp, "nanoCLR locked up");
        Assert.True(pio.TxOf(0).Count >= 4, "no toggles reached the PIO FIFO");

        // The app pushes 1,0,1,0 — exactly the levels that crossed into the SM's TX FIFO.
        Assert.Equal(new uint[] { 1, 0, 1, 0 }, First(pio.TxOf(0), 4));
        Assert.Equal(6u, clr.Pico.Rp2040.IoBank0.GetFuncSel(LedPin));
    }

    [Fact]
    public void Deployment_native_checksums_match_the_firmware()
    {
        NanoApp app = App();
        Firmware().AssertCompatible(app); // must not throw
    }

    private static uint[] First(IReadOnlyList<uint> w, int n)
    {
        var r = new uint[n];
        for (int i = 0; i < n; i++) r[i] = w[i];
        return r;
    }
}
