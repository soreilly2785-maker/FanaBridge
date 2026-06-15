using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FanaBridge.Protocol;
using FanaBridge.Transport;
using Xunit;

namespace FanaBridge.Tests
{
    public class ItmDisplayControllerTests
    {
        private class StubTransport : IDeviceTransport
        {
            public bool IsConnected { get; set; } = true;
            public int Col03MaxInputReportLength { get; set; } = 64;
            public List<byte[]> SentCol03Reports { get; } = new List<byte[]>();

            public bool SendCol03(byte[] data)
            {
                var copy = new byte[data.Length];
                Array.Copy(data, copy, data.Length);
                SentCol03Reports.Add(copy);
                return true;
            }

            public int ReadCol03(byte[] buffer, int timeoutMs) => -1;

            public List<byte[]> SentCol01Reports { get; } = new List<byte[]>();

            public bool SendCol01(byte[] data)
            {
                var copy = new byte[data.Length];
                Array.Copy(data, copy, data.Length);
                SentCol01Reports.Add(copy);
                return true;
            }

            public IDisposable BeginBatch() => new NoOpDisposable();

            private sealed class NoOpDisposable : IDisposable
            {
                public void Dispose() { }
            }
        }

        private static byte[] Slice(byte[] data, int start, int count)
        {
            return data.Skip(start).Take(count).ToArray();
        }

        private static byte[] SliceToEnd(byte[] data, int start)
        {
            return data.Skip(start).ToArray();
        }

        [Fact]
        public void SendDisplayOwnership_HostControl_BuildsExpectedFrame()
        {
            var transport = new StubTransport();
            var itm = new ItmDisplayController(transport);

            bool ok = itm.SendDisplayOwnership(hostControl: true);

            Assert.True(ok);
            var report = Assert.Single(transport.SentCol01Reports);
            Assert.Equal(new byte[] { 0x01, 0xF8, 0x09, 0x01, 0x18, 0x02, 0x00, 0x00 }, report);
        }

        [Fact]
        public void SendDisplayOwnership_FirmwareControl_BuildsExpectedFrame()
        {
            var transport = new StubTransport();
            var itm = new ItmDisplayController(transport);

            bool ok = itm.SendDisplayOwnership(hostControl: false);

            Assert.True(ok);
            var report = Assert.Single(transport.SentCol01Reports);
            Assert.Equal(new byte[] { 0x01, 0xF8, 0x09, 0x01, 0x18, 0x01, 0x00, 0x00 }, report);
        }

        [Fact]
        public void SendItmActivate_BuildsExpectedFrame()
        {
            var transport = new StubTransport();
            var itm = new ItmDisplayController(transport);

            bool ok = itm.SendItmActivate();

            Assert.True(ok);
            var report = Assert.Single(transport.SentCol03Reports);
            Assert.Equal(64, report.Length);
            Assert.Equal(new byte[] { 0xFF, 0x05, 0x02, 0x01 }, Slice(report, 0, 4));
            Assert.All(SliceToEnd(report, 4), b => Assert.Equal(0, b));
        }

        [Fact]
        public void SendPageSet_BuildsExpectedFrame()
        {
            var transport = new StubTransport();
            var itm = new ItmDisplayController(transport);

            bool ok = itm.SendPageSet(ItmDeviceId.Bme, ItmPage1.Page);

            Assert.True(ok);
            var report = Assert.Single(transport.SentCol03Reports);
            Assert.Equal(64, report.Length);
            Assert.Equal(new byte[] { 0xFF, 0x05, 0x04, ItmDeviceId.Bme, ItmPage1.Page }, Slice(report, 0, 5));
            Assert.All(SliceToEnd(report, 5), b => Assert.Equal(0, b));
        }

        [Fact]
        public void SendKeepalive_BuildsExpectedFrame()
        {
            var transport = new StubTransport();
            var itm = new ItmDisplayController(transport);

            bool ok = itm.SendKeepalive();

            Assert.True(ok);
            var report = Assert.Single(transport.SentCol03Reports);
            Assert.Equal(new byte[] { 0xFF, 0x05, 0x04, 0x02, 0x0B }, Slice(report, 0, 5));
        }

        [Fact]
        public void SendParamDefs_EncodesSlotEntriesWithSuffix()
        {
            var transport = new StubTransport();
            var itm = new ItmDisplayController(transport);

            var entries = new[]
            {
                new ItmDisplayController.ParamDefEntry(0x82, 0x0000, Encoding.ASCII.GetBytes("/0")),
                new ItmDisplayController.ParamDefEntry(0x83, 0x0000, Encoding.ASCII.GetBytes("/0")),
            };

            bool ok = itm.SendParamDefs(entries);

            Assert.True(ok);
            var report = Assert.Single(transport.SentCol03Reports);

            Assert.Equal(new byte[] { 0xFF, 0x05, 0x03 }, Slice(report, 0, 3));

            // Entry 1: 03 82 00 00 02 2F 30  ("/0")
            Assert.Equal(new byte[] { 0x03, 0x82, 0x00, 0x00, 0x02, 0x2F, 0x30 }, Slice(report, 3, 7));

            // Entry 2: 03 83 00 00 02 2F 30
            Assert.Equal(new byte[] { 0x03, 0x83, 0x00, 0x00, 0x02, 0x2F, 0x30 }, Slice(report, 10, 7));

            // Remainder is zero-padded
            Assert.All(SliceToEnd(report, 17), b => Assert.Equal(0, b));
        }

        [Fact]
        public void SendValueUpdate_EncodesMultipleEntries()
        {
            var transport = new StubTransport();
            var itm = new ItmDisplayController(transport);

            var entries = new[]
            {
                // SPEED=1, handle 0, Int16 LE value 123
                new ItmDisplayController.ValueUpdateEntry(0x00, ItmParameterId.Speed, BitConverter.GetBytes((short)123)),
                // GEAR=4, handle 1, Uint8 value 3
                new ItmDisplayController.ValueUpdateEntry(0x01, ItmParameterId.Gear, new byte[] { 3 }),
            };

            bool ok = itm.SendValueUpdate(entries);

            Assert.True(ok);
            var report = Assert.Single(transport.SentCol03Reports);

            Assert.Equal(new byte[] { 0xFF, 0x05, 0x01 }, Slice(report, 0, 3));

            // Entry 1: 03 00 0100 02 7B00  (paramId=1 LE, size=2, value=123 LE)
            Assert.Equal(new byte[] { 0x03, 0x00, 0x01, 0x00, 0x02, 0x7B, 0x00 }, Slice(report, 3, 7));

            // Entry 2: 03 01 0400 01 03  (paramId=4 LE, size=1, value=3)
            Assert.Equal(new byte[] { 0x03, 0x01, 0x04, 0x00, 0x01, 0x03 }, Slice(report, 10, 6));

            Assert.All(SliceToEnd(report, 16), b => Assert.Equal(0, b));
        }

        [Fact]
        public void SendParamDefs_ThrowsWhenEntriesExceedReportSize()
        {
            var transport = new StubTransport();
            var itm = new ItmDisplayController(transport);

            // A 60-byte suffix entry takes 65 bytes — exceeds the 64-byte report
            // even on its own (offset starts at 3).
            var entries = new[]
            {
                new ItmDisplayController.ParamDefEntry(0x82, 0x0000, new byte[60]),
            };

            Assert.Throws<InvalidOperationException>(() => itm.SendParamDefs(entries));
        }
    }
}
