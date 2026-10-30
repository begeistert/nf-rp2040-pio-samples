//
// Sample — an I2C master built from a PIO state machine, on any pair of pins.
//
// I2C is an open-drain bus: nobody ever drives a line high. A device pulls it low or lets go, and a
// pull-up returns it to idle. A PIO state machine expresses that by driving the pin DIRECTION rather
// than its level -- the output register stays at zero, so making the pin an output pulls the line
// low and making it an input releases it. That is why the program shifts into PINDIRS and why the
// side-set is configured for pin directions too.
//
// The pull comes from the pad, and routing a pin to the PIO rewrites the pad, so it has to be asked
// for at that moment. That is what PioPinPull.Up does here. The internal pull is weak -- tens of
// kilohms -- so the rise time is worth measuring before trusting it on a real bus.
//
// One transaction, driven from the FIFO one byte at a time:
//
//   start:  SDA falls while SCL is high
//   byte:   eight bits, MSB first; SDA changes while SCL is low and is read while SCL is high
//   ack:    the master releases SDA for a ninth clock and the addressed device pulls it low
//   stop:   SDA rises while SCL is high
//
using System;
using System.Threading;
using System.Diagnostics;
using nanoFramework.Hardware.Pico.Pio;

namespace PioI2cMasterSample
{
    public sealed class PioI2cMaster : IDisposable
    {
        private const int CyclesPerBit = 4;

        private readonly PioBlock _pio;
        private readonly PioStateMachine _sm;
        private readonly PioProgram _program;
        private readonly uint _offset;
        private readonly int _sdaPin;
        private readonly int _sclPin;
        private bool _disposed;

        /// <summary>Initializes a new instance of the <see cref="PioI2cMaster"/> class.</summary>
        /// <param name="pio">The PIO block to run on.</param>
        /// <param name="sdaPin">The GPIO carrying SDA.</param>
        /// <param name="sclPin">The GPIO carrying SCL; must be <paramref name="sdaPin"/> + 1.</param>
        /// <param name="busHz">Bus clock in Hz.</param>
        public PioI2cMaster(PioBlock pio, int sdaPin, int sclPin, int busHz)
        {
            _pio = pio;
            _sdaPin = sdaPin;
            _sclPin = sclPin;

            _program = BuildProgram();
            _offset = _pio.AddProgram(_program);

            _pio.InitGpio(sdaPin, PioPinPull.Up);
            _pio.InitGpio(sclPin, PioPinPull.Up);

            _sm = _pio.ClaimStateMachine();

            _sm.SetConsecutivePinDirs(sdaPin, 2, false);

            _sm.Init(_offset, PioStateMachineConfig.FromProgram(_program, (int)_offset)
                .OutPins(sdaPin, 1)
                .SetPins(sdaPin, 1)
                .SideSetPins(sclPin)
                .InPins(sdaPin)
                .ClockFromFrequency(busHz * CyclesPerBit, Pio.SystemClock));

            _sm.Exec(PioEncoder.Set(DestinationOperand.Pins, 0));

            _sm.Enabled = true;
        }

        private static PioProgram BuildProgram()
        {
            var asm = new PioAssembler(new PioAssemblerOptions
            {
                SideSetCount = 1,
                SideSetOption = true,
                SideSetPinDirs = true,
            });

            asm.SetOutShift(ShiftDirection.Left, autoPull: false, threshold: 32);
            asm.SetInShift(ShiftDirection.Left, autoPush: false, threshold: 32);

            PioLabel bitloop = asm.DefineLabel();

            asm.WrapTarget();

            asm.Pull();
            asm.Set(DestinationOperand.PinDirs, 1).Side(0).Delay(1);
            asm.Set(DestinationOperand.RegisterX, 7).Side(1).Delay(1);

            asm.MarkLabel(bitloop);
            asm.Out(DestinationOperand.PinDirs, 1).Side(1).Delay(1);
            asm.Nop().Side(0).Delay(1);
            asm.In(SourceOperand.Pins, 1);
            asm.Jmp(PioCondition.XPostDec, bitloop).Side(1).Delay(1);

            asm.Set(DestinationOperand.PinDirs, 0).Side(1).Delay(1);
            asm.Nop().Side(0).Delay(1);
            asm.In(SourceOperand.Pins, 1);
            asm.Push();

            asm.Set(DestinationOperand.PinDirs, 1).Side(1).Delay(1);
            asm.Set(DestinationOperand.PinDirs, 0).Side(0).Delay(1);

            asm.Wrap();

            return asm.Build();
        }

        /// <summary>
        /// Runs one transaction: a start, the address byte, the payload, and a stop.
        /// </summary>
        /// <param name="address">The 7-bit device address.</param>
        /// <param name="data">The bytes to write.</param>
        /// <returns>The number of bytes the state machine consumed.</returns>
        public int Write(int address, byte[] data)
        {
            uint[] words = new uint[data.Length + 1];

            words[0] = (uint)((address << 1) & 0xFE) << 24;
            for (int i = 0; i < data.Length; i++)
            {
                words[i + 1] = (uint)data[i] << 24;
            }

            return _sm.Write(words, 0, words.Length, 100);
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _sm.Dispose();
            _pio.RemoveProgram(_program, _offset);
        }
    }

    public class Program
    {
        private const int SdaPin = 2;
        private const int SclPin = 3;
        private const int BusHz = 100_000;
        private const int Address = 0x42;

        public static int Transactions;

        public static void Main()
        {
            PioBlock pio = Pio.Get(Pio.MinIndex);

            Debug.WriteLine($"PIO I2C master on PIO{pio.Index}, SDA GP{SdaPin}, SCL GP{SclPin}, {BusHz} Hz");

            var i2c = new PioI2cMaster(pio, SdaPin, SclPin, BusHz);
            byte[] payload = new byte[] { 0xA5, 0x5A };

            while (true)
            {
                i2c.Write(Address, payload);
                Transactions++;
                Thread.Sleep(50);
            }
        }
    }
}
