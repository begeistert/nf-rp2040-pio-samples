using System;
using System.Collections.Generic;
using System.IO;
using RP2040Sharp.NanoFramework.TestKit;
using RP2040.TestKit.Boards;
using RP2040.TestKit.Probes;
using Xunit;

namespace Dht.Integration.Tests;

/// <summary>
/// Integration test for the DHT sample. Boots the firmware on the RP2040Sharp emulator, then clocks a
/// synthetic DHT22 pulse train onto the data pin — the sensor's reply — and checks the PIO state
/// machine measured every pulse and decoded it, bit timing and all, into the five bytes it pushes to
/// the RX FIFO. This is what only a host-side, peripheral-accurate emulator can see.
/// </summary>
public class DhtIntegrationTests
{
    private const int DataPin = 15;

    private static string FirmwareDir => Path.Combine(AppContext.BaseDirectory, "firmware");
    private static string PeDir => Path.Combine(AppContext.BaseDirectory, "pe");
    private static NanoFirmware Firmware() => NanoFirmware.FromDirectory(FirmwareDir);
    private static NanoApp App() => NanoApp.FromPeDirectory(PeDir, appAssemblyName: "Dht.Sample");

    [Fact]
    public void Decodes_a_DHT22_pulse_train_into_five_bytes()
    {
        using var clr = NanoClrHarness.Boot(Firmware(), App());
        clr.Pico.AddPioProbe(0, out PioProbe pio);

        // The reply to clock in, MSB-first: humidity 45.6 %RH (456 = 0x01C8), temperature 23.4 °C
        // (234 = 0x00EA), then the trailing checksum byte (sum of the four data bytes).
        byte[] frame = { 0x01, 0xC8, 0x00, 0xEA, 0x00 };
        frame[4] = (byte)(frame[0] + frame[1] + frame[2] + frame[3]);

        // Run until the app has muxed the data pin to PIO, then let the SM enable and finish its
        // ~1 ms start pulse — it then blocks waiting for the line to float up.
        for (int i = 0; i < 16000 && clr.Pico.Rp2040.IoBank0.GetFuncSel(DataPin) != 6; i++)
        {
            clr.Pico.RunMicroseconds(50);
        }

        clr.Pico.RunMicroseconds(5000);

        // Pull-up floats the line high, then the sensor answers: 80 µs low, 80 µs high.
        Drive(clr, true, 60);
        Drive(clr, false, 80);
        Drive(clr, true, 80);

        // 40 data bits: each is ~50 µs low, then a high of 26 µs (0) or 70 µs (1).
        foreach (byte b in frame)
        {
            for (int bit = 7; bit >= 0; bit--)
            {
                Drive(clr, false, 50);
                Drive(clr, true, ((b >> bit) & 1) == 1 ? 70 : 26);
            }
        }

        Drive(clr, true, 200); // line idle

        // The SM measured every pulse and pushed exactly those five bytes.
        IReadOnlyList<uint> rx = pio.RxOf(0);
        Assert.True(rx.Count >= 5, $"the SM pushed {rx.Count} bytes, expected 5");
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(frame[i], (byte)(rx[i] & 0xFF));
        }
    }

    [Fact]
    public void Deployment_is_compatible_with_the_firmware()
    {
        Firmware().AssertCompatible(App());
    }

    // Holds the data line at a level for the given microseconds while the emulator runs.
    private static void Drive(NanoClrHarness clr, bool high, int microseconds)
    {
        clr.Pico.Rp2040.Sio.SetGpioExternalIn(DataPin, high);
        clr.Pico.RunMicroseconds(microseconds);
    }
}
