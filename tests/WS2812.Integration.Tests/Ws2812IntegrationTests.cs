using System;
using System.Collections.Generic;
using System.IO;
using RP2040Sharp.NanoFramework.TestKit;
using RP2040.TestKit.Boards;
using RP2040.TestKit.Probes;
using Xunit;

namespace WS2812.Integration.Tests;

/// <summary>
/// Boots the RP2040 nanoCLR firmware on the RP2040Sharp emulator, runs the deployed WS2812 app, and
/// watches the PIO peripheral itself — the GRB words crossing into the TX FIFO and the data pins being
/// driven — which an on-device managed test can't observe. The native-checksum guard rounds it out.
/// </summary>
public class Ws2812IntegrationTests
{
    private const int Strip0Pin = 2;
    private const int Strip1Pin = 3;

    private static string FirmwareDir => Path.Combine(AppContext.BaseDirectory, "firmware");
    private static string PeDir => Path.Combine(AppContext.BaseDirectory, "pe");

    private static NanoFirmware Firmware() => NanoFirmware.FromDirectory(FirmwareDir);
    private static NanoApp App() => NanoApp.FromPeDirectory(PeDir, appAssemblyName: "WS2812.Sample");

    [Fact]
    public void Serialises_two_WS2812_strips_onto_their_pins()
    {
        using var clr = NanoClrHarness.Boot(Firmware(), App());
        clr.Pico.AddPioProbe(0, out PioProbe pio);

        // Watch the PIO drive each data pin high and low as it clocks the bits out (side-set output).
        int hi0 = 0, lo0 = 0, hi1 = 0, lo1 = 0;
        clr.Pico.Rp2040.Pio0.WriteGpioPins = (value, mask) =>
        {
            if ((mask & (1u << Strip0Pin)) != 0) { if (((value >> Strip0Pin) & 1) != 0) hi0++; else lo0++; }
            if ((mask & (1u << Strip1Pin)) != 0) { if (((value >> Strip1Pin) & 1) != 0) hi1++; else lo1++; }
        };

        for (int i = 0; i < 40000 &&
             (pio.TxOf(0).Count < 3 || pio.TxOf(1).Count < 3 || hi0 == 0 || lo0 == 0 || hi1 == 0 || lo1 == 0); i++)
        {
            clr.Pico.RunMicroseconds(20);
        }

        Assert.False(clr.IsLockedUp, "nanoCLR locked up");

        // The exact GRB words the app pushed crossed into each SM's TX FIFO, in order.
        Assert.Equal(new[] { Grb(64, 0, 0), Grb(0, 64, 0), Grb(0, 0, 64) }, First3(pio.TxOf(0)));
        Assert.Equal(new[] { Grb(0, 0, 64), Grb(64, 0, 0), Grb(0, 64, 0) }, First3(pio.TxOf(1)));

        // Each data pin is owned by the PIO (funcsel 6) and is driven both high and low — the SM serialises.
        Assert.Equal(6u, clr.Pico.Rp2040.IoBank0.GetFuncSel(Strip0Pin));
        Assert.Equal(6u, clr.Pico.Rp2040.IoBank0.GetFuncSel(Strip1Pin));
        Assert.True(hi0 > 0 && lo0 > 0 && hi1 > 0 && lo1 > 0, "the PIO never toggled the data pins");
    }

    [Fact]
    public void Crosses_into_native_AddProgram()
    {
        using var clr = NanoClrHarness.Boot(Firmware(), App());

        // The one host-side CLR hook worth keeping: drive the interpreter to the managed -> native PIO
        // boundary (pio.AddProgram's InternalCall), located by symbol.
        Assert.True(clr.RunUntilNativeCall("PioBlock.NativeAddProgram"),
            "the CLR never crossed into PioBlock.NativeAddProgram");
        Assert.Equal("PioBlock.NativeAddProgram", clr.LastReachedSymbol);
    }

    [Fact]
    public void Deployment_native_checksums_match_the_firmware()
    {
        Firmware().AssertCompatible(App());
    }

    [Fact]
    public void Checksum_guard_rejects_a_drifted_library()
    {
        NanoApp app = App();
        string tmp = Directory.CreateTempSubdirectory("nf-fw").FullName;
        foreach (string bin in Directory.GetFiles(FirmwareDir, "*.bin"))
        {
            File.Copy(bin, Path.Combine(tmp, Path.GetFileName(bin)));
        }

        File.WriteAllText(Path.Combine(tmp, "firmware.manifest.json"),
            "{ \"nativeChecksums\": { \"nanoFramework.Hardware.Rpi\": \"0xDEADBEEF\" } }");

        var ex = Assert.Throws<NanoChecksumMismatchException>(
            () => NanoFirmware.FromDirectory(tmp).AssertCompatible(app));
        Assert.Equal("nanoFramework.Hardware.Rpi", ex.Assembly);
        Assert.Equal(0xDEADBEEFu, ex.FirmwareChecksum);
    }

    // Mirrors the app: GRB packed, then shifted left 8 because the SM clocks out the top 24 bits.
    private static uint Grb(byte r, byte g, byte b) => (uint)(((g << 16) | (r << 8) | b) << 8);
    private static uint[] First3(IReadOnlyList<uint> w) => new[] { w[0], w[1], w[2] };
}
