using System;
using System.Collections.Generic;
using System.IO;
using RP2040Sharp.NanoFramework.TestKit;
using RP2040.TestKit.Probes;
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
    public void Reads_back_FIFO_level_and_program_counter()
    {
        using var clr = NanoClrHarness.Boot(Firmware(), App());
        clr.RunUntilStatic(AppSymbols.Assembly, AppSymbols.Fields.Echoed, v => v.AsInt32 >= 2);

        int pc = clr.ReadStaticInt32(AppSymbols.Assembly, AppSymbols.Fields.LastPc);
        int maxTx = clr.ReadStaticInt32(AppSymbols.Assembly, AppSymbols.Fields.MaxTxLevel);

        _out.WriteLine($"LastPc = {pc}, MaxTxLevel = {maxTx}");
        // PC stays within the 3-instruction loopback program; FIFO level is a valid 0..8 read-back.
        Assert.InRange(pc, 0, 2);
        Assert.InRange(maxTx, 0, 8);
    }

    [Fact]
    public void Echoes_each_word_back_unchanged_through_both_FIFOs()
    {
        using var clr = NanoClrHarness.Boot(Firmware(), App());
        clr.Pico.AddPioProbe(0, out PioProbe pio);

        for (int i = 0; i < 8000 && pio.RxOf(0).Count < 3; i++)
            clr.Pico.RunMicroseconds(100);

        Assert.False(clr.IsLockedUp, "nanoCLR locked up");
        Assert.True(pio.RxOf(0).Count >= 3, "no words came back from the loopback");

        // The app pushes 1,2,3…; the loopback returns each unchanged, so TX and RX carry the same words.
        Assert.Equal(new uint[] { 1, 2, 3 }, First(pio.TxOf(0), 3));
        Assert.Equal(new uint[] { 1, 2, 3 }, First(pio.RxOf(0), 3));
    }

    [Fact]
    public void Deployment_is_compatible_with_the_firmware()
    {
        // The native checksum changes whenever the PIO library's InternalCall surface changes, so we
        // assert compatibility against the bundled firmware rather than hard-coding a literal value.
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
