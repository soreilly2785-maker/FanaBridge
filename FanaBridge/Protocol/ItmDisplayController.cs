using System;
using System.Collections.Generic;
using FanaBridge.Transport;

namespace FanaBridge.Protocol
{
    /// <summary>
    /// Sends ITM display frames (col03, cmd class 0x05) — PageSet, Keepalive,
    /// ParamDefs, and ValueUpdate — per docs/reference/protocol.md "0x05 — ITM Display".
    ///
    /// All HID I/O goes through <see cref="IDeviceTransport"/>, making the
    /// protocol logic testable without hardware.
    /// </summary>
    public class ItmDisplayController
    {
        internal const int REPORT_LENGTH = 64;
        internal const byte CMD_CLASS = 0x05;

        internal const byte SUBCMD_VALUE_UPDATE = 0x01;
        internal const byte SUBCMD_PARAM_DEFS = 0x03;
        internal const byte SUBCMD_PAGE_CONFIG = 0x04;

        // Each ParamDefs/ValueUpdate entry within a report is prefixed with
        // this marker byte, confirmed against real ValueUpdate traffic
        // (FF 05 01 03 <handle> <paramIdLo> <paramIdHi> <len> <value...>).
        internal const byte ENTRY_MARKER = 0x03;

        internal const byte KEEPALIVE_CONFIG_TYPE = 0x02;
        internal const byte KEEPALIVE_CONFIG_VALUE = 0x0B;

        internal const byte SUBCMD_ACTIVATE = 0x02;
        internal const byte ACTIVATE_VALUE = 0x01;

        private readonly IDeviceTransport _transport;

        public ItmDisplayController(IDeviceTransport transport)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        }

        /// <summary>Whether the underlying transport is connected.</summary>
        public bool IsConnected => _transport.IsConnected;

        /// <summary>
        /// A single ParamDefs entry: assigns a display slot to a position,
        /// with an optional ASCII suffix (e.g. "/0" for "x / total").
        /// </summary>
        public readonly struct ParamDefEntry
        {
            public byte SlotId { get; }
            public ushort Position { get; }
            public byte[] Suffix { get; }

            public ParamDefEntry(byte slotId, ushort position = 0, byte[] suffix = null)
            {
                SlotId = slotId;
                Position = position;
                Suffix = suffix ?? Array.Empty<byte>();
            }
        }

        /// <summary>
        /// A single ValueUpdate entry: the value for a parameter handle
        /// previously assigned via ParamDefs.
        /// </summary>
        public readonly struct ValueUpdateEntry
        {
            public byte Handle { get; }
            public ushort ParamId { get; }
            public byte[] Value { get; }

            public ValueUpdateEntry(byte handle, ushort paramId, byte[] value)
            {
                Handle = handle;
                ParamId = paramId;
                Value = value ?? Array.Empty<byte>();
            }
        }

        internal const int COL01_REPORT_LENGTH = 8;
        internal const byte COL01_REPORT_ID = 0x01;
        internal const byte COL01_GROUP = 0x01;
        internal const byte COL01_SUBCMD_DISPLAY_OWNERSHIP = 0x18;
        internal const byte DISPLAY_OWNERSHIP_HOST = 0x02;
        internal const byte DISPLAY_OWNERSHIP_FIRMWARE = 0x01;

        /// <summary>
        /// Takes or releases host control of the OLED (col01, group 0x01,
        /// subcmd 0x18). The firmware keeps driving its own display content
        /// until the host explicitly takes ownership — required on the PBME
        /// before ITM output becomes visible. <c>RID F8 09 01 18 &lt;mode&gt;</c>
        /// </summary>
        public bool SendDisplayOwnership(bool hostControl)
        {
            var buf = new byte[COL01_REPORT_LENGTH];
            buf[0] = COL01_REPORT_ID;
            buf[1] = 0xF8;
            buf[2] = 0x09;
            buf[3] = COL01_GROUP;
            buf[4] = COL01_SUBCMD_DISPLAY_OWNERSHIP;
            buf[5] = hostControl ? DISPLAY_OWNERSHIP_HOST : DISPLAY_OWNERSHIP_FIRMWARE;
            return _transport.SendCol01(buf);
        }

        /// <summary>
        /// Activates or deactivates ITM rendering on the display. Must be
        /// activated before PageSet, ParamDefs, or ValueUpdate will have any
        /// visible effect — the firmware accepts those frames without error
        /// even while ITM is inactive, but renders nothing until activated.
        /// Deactivating returns the display to its normal firmware-driven
        /// content (e.g. while not in a race session).
        /// <c>FF 05 02 01</c> (activate) / <c>FF 05 02 00</c> (deactivate)
        /// </summary>
        public bool SendItmActivate(bool activate = true)
        {
            var buf = new byte[REPORT_LENGTH];
            buf[0] = 0xFF;
            buf[1] = CMD_CLASS;
            buf[2] = SUBCMD_ACTIVATE;
            buf[3] = activate ? ACTIVATE_VALUE : (byte)0x00;
            return _transport.SendCol03(buf);
        }

        /// <summary>
        /// Selects which ITM page is active on the given display device.
        /// <c>FF 05 04 &lt;deviceId&gt; &lt;page&gt;</c>
        /// </summary>
        public bool SendPageSet(byte deviceId, byte page)
        {
            var buf = new byte[REPORT_LENGTH];
            buf[0] = 0xFF;
            buf[1] = CMD_CLASS;
            buf[2] = SUBCMD_PAGE_CONFIG;
            buf[3] = deviceId;
            buf[4] = page;
            return _transport.SendCol03(buf);
        }

        /// <summary>
        /// Sends the ITM keepalive. Must be sent every ~100ms to keep the
        /// display alive. <c>FF 05 04 02 0B</c>
        /// </summary>
        public bool SendKeepalive()
        {
            var buf = new byte[REPORT_LENGTH];
            buf[0] = 0xFF;
            buf[1] = CMD_CLASS;
            buf[2] = SUBCMD_PAGE_CONFIG;
            buf[3] = KEEPALIVE_CONFIG_TYPE;
            buf[4] = KEEPALIVE_CONFIG_VALUE;
            return _transport.SendCol03(buf);
        }

        /// <summary>
        /// Defines the display slot layout. <c>FF 05 03 &lt;entries...&gt;</c>
        /// where each entry is <c>03 slotId posLo posHi suffixLen [suffix...]</c>.
        /// </summary>
        public bool SendParamDefs(IReadOnlyList<ParamDefEntry> entries)
        {
            var buf = new byte[REPORT_LENGTH];
            buf[0] = 0xFF;
            buf[1] = CMD_CLASS;
            buf[2] = SUBCMD_PARAM_DEFS;

            int offset = 3;
            foreach (var entry in entries)
            {
                int entryLength = 5 + entry.Suffix.Length;
                if (offset + entryLength > REPORT_LENGTH)
                    throw new InvalidOperationException("ParamDefs entries exceed the 64-byte report");

                buf[offset] = ENTRY_MARKER;
                buf[offset + 1] = entry.SlotId;
                buf[offset + 2] = (byte)(entry.Position & 0xFF);
                buf[offset + 3] = (byte)((entry.Position >> 8) & 0xFF);
                buf[offset + 4] = (byte)entry.Suffix.Length;
                Array.Copy(entry.Suffix, 0, buf, offset + 5, entry.Suffix.Length);

                offset += entryLength;
            }

            return _transport.SendCol03(buf);
        }

        /// <summary>
        /// Sends telemetry values for one or more previously-defined handles.
        /// <c>FF 05 01 &lt;entries...&gt;</c> where each entry is
        /// <c>01 handle paramIdLo paramIdHi size [value...]</c>.
        /// </summary>
        public bool SendValueUpdate(IReadOnlyList<ValueUpdateEntry> entries)
        {
            var buf = new byte[REPORT_LENGTH];
            buf[0] = 0xFF;
            buf[1] = CMD_CLASS;
            buf[2] = SUBCMD_VALUE_UPDATE;

            int offset = 3;
            foreach (var entry in entries)
            {
                int entryLength = 5 + entry.Value.Length;
                if (offset + entryLength > REPORT_LENGTH)
                    throw new InvalidOperationException("ValueUpdate entries exceed the 64-byte report");

                buf[offset] = ENTRY_MARKER;
                buf[offset + 1] = entry.Handle;
                buf[offset + 2] = (byte)(entry.ParamId & 0xFF);
                buf[offset + 3] = (byte)((entry.ParamId >> 8) & 0xFF);
                buf[offset + 4] = (byte)entry.Value.Length;
                Array.Copy(entry.Value, 0, buf, offset + 5, entry.Value.Length);

                offset += entryLength;
            }

            return _transport.SendCol03(buf);
        }
    }
}
