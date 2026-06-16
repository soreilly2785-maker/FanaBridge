using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using HidSharp;

namespace FanaBridge.ItmTool
{
    /// <summary>
    /// Standalone REPL for experimenting with the Fanatec col03 ITM display
    /// protocol without rebuilding/restarting SimHub.
    ///
    /// IMPORTANT: Close SimHub (or at least disable "Enable ITM Display" in
    /// FanaBridge) before using this tool — both will otherwise be writing
    /// to the same HID interface.
    /// </summary>
    internal static class Program
    {
        private const ushort FANATEC_VENDOR_ID = 0x0EB7;
        private const int COL03_LEN = 64;
        private const int COL01_LEN = 8;

        private static HidStream _col03;
        private static HidStream _col01;

        private static Thread _keepaliveThread;
        private static volatile bool _keepaliveRunning;

        private static void Main()
        {
            Console.WriteLine("FanaBridge ITM test tool");
            Console.WriteLine("Type 'help' for commands. Type 'connect' to open the device.");

            while (true)
            {
                Console.Write("> ");
                string line = Console.ReadLine();
                if (line == null) break;
                line = line.Trim();
                if (line.Length == 0) continue;

                var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                try
                {
                    if (!Dispatch(parts)) break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Error: " + ex.Message);
                }
            }

            StopKeepalive();
            _col03?.Close();
            _col01?.Close();
        }

        private static bool Dispatch(string[] p)
        {
            switch (p[0].ToLowerInvariant())
            {
                case "help":
                    PrintHelp();
                    return true;

                case "devices":
                    ListDevices();
                    return true;

                case "connect":
                    Connect();
                    return true;

                case "activate":
                case "enable":
                    {
                        bool on = p.Length < 2 || !(p[1].Equals("off", StringComparison.OrdinalIgnoreCase) || p[1] == "0");
                        var buf = NewCol03();
                        buf[1] = 0x05;
                        buf[2] = 0x02;
                        buf[3] = on ? (byte)0x01 : (byte)0x00;
                        SendCol03(buf, "ITM Activate (" + (on ? "on" : "off") + ")");
                        return true;
                    }

                case "ownership":
                    {
                        if (p.Length < 2) { Console.WriteLine("usage: ownership host|fw"); return true; }
                        bool host = p[1].Equals("host", StringComparison.OrdinalIgnoreCase);
                        var buf = NewCol01();
                        buf[0] = 0x01;
                        buf[1] = 0xF8;
                        buf[2] = 0x09;
                        buf[3] = 0x01;
                        buf[4] = 0x18;
                        buf[5] = host ? (byte)0x02 : (byte)0x01;
                        SendCol01(buf, "Display Ownership (" + (host ? "host" : "firmware") + ")");
                        return true;
                    }

                case "pageset":
                    {
                        if (p.Length < 3) { Console.WriteLine("usage: pageset <deviceId> <page>"); return true; }
                        byte deviceId = ParseByte(p[1]);
                        byte page = ParseByte(p[2]);
                        var buf = NewCol03();
                        buf[1] = 0x05;
                        buf[2] = 0x04;
                        buf[3] = deviceId;
                        buf[4] = page;
                        SendCol03(buf, "PageSet (device=" + deviceId + " page=" + page + ")");
                        return true;
                    }

                case "keepalive":
                    SendKeepalive();
                    return true;

                case "auto":
                    {
                        if (p.Length < 2) { Console.WriteLine("usage: auto on|off"); return true; }
                        if (p[1].Equals("on", StringComparison.OrdinalIgnoreCase)) StartKeepalive();
                        else StopKeepalive();
                        return true;
                    }

                case "paramdefs":
                    // paramdefs <slotId> <posLo> <posHi> <suffix> [<slotId> <posLo> <posHi> <suffix> ...]
                    // suffix: literal text, or '-' for none
                    {
                        if (p.Length < 5 || (p.Length - 1) % 4 != 0)
                        {
                            Console.WriteLine("usage: paramdefs <slotId> <posLo> <posHi> <suffix|-> [...]");
                            return true;
                        }

                        var buf = NewCol03();
                        buf[1] = 0x05;
                        buf[2] = 0x03;
                        int offset = 3;

                        for (int i = 1; i < p.Length; i += 4)
                        {
                            byte slotId = ParseByte(p[i]);
                            byte posLo = ParseByte(p[i + 1]);
                            byte posHi = ParseByte(p[i + 2]);
                            string suffixArg = p[i + 3];
                            byte[] suffix = suffixArg == "-" ? Array.Empty<byte>() : Encoding.ASCII.GetBytes(suffixArg);

                            int entryLength = 5 + suffix.Length;
                            if (offset + entryLength > COL03_LEN)
                                throw new InvalidOperationException("ParamDefs entries exceed 64 bytes");

                            buf[offset] = 0x03;
                            buf[offset + 1] = slotId;
                            buf[offset + 2] = posLo;
                            buf[offset + 3] = posHi;
                            buf[offset + 4] = (byte)suffix.Length;
                            Array.Copy(suffix, 0, buf, offset + 5, suffix.Length);

                            offset += entryLength;
                        }

                        SendCol03(buf, "ParamDefs (" + ((p.Length - 1) / 4) + " entries)");
                        return true;
                    }

                case "value":
                    // value <handle> <paramId> <type> <number>
                    // type: u8, i16, u16, i32, u32, f32
                    {
                        if (p.Length != 5)
                        {
                            Console.WriteLine("usage: value <handle> <paramId> <u8|i16|u16|i32|u32|f32> <number>");
                            return true;
                        }

                        byte handle = ParseByte(p[1]);
                        ushort paramId = (ushort)ParseInt(p[2]);
                        byte[] value = EncodeValue(p[3], p[4]);

                        var buf = NewCol03();
                        buf[1] = 0x05;
                        buf[2] = 0x01;

                        int entryLength = 5 + value.Length;
                        buf[3] = 0x03;
                        buf[4] = handle;
                        buf[5] = (byte)(paramId & 0xFF);
                        buf[6] = (byte)((paramId >> 8) & 0xFF);
                        buf[7] = (byte)value.Length;
                        Array.Copy(value, 0, buf, 8, value.Length);

                        SendCol03(buf, "ValueUpdate (handle=" + handle + " paramId=" + paramId + " value=" + p[4] + ")");
                        return true;
                    }

                case "read03":
                    {
                        int timeoutMs = p.Length > 1 ? (int)ParseInt(p[1]) : 500;
                        ReadCol03(timeoutMs);
                        return true;
                    }

                case "raw03":
                    {
                        var buf = NewCol03();
                        FillFromHex(buf, p.Skip(1));
                        SendCol03(buf, "raw03");
                        return true;
                    }

                case "raw01":
                    {
                        var buf = NewCol01();
                        FillFromHex(buf, p.Skip(1));
                        SendCol01(buf, "raw01");
                        return true;
                    }

                case "sleep":
                    {
                        if (p.Length < 2) { Console.WriteLine("usage: sleep <ms>"); return true; }
                        int ms = (int)ParseInt(p[1]);
                        Console.WriteLine("Sleeping " + ms + "ms...");
                        Thread.Sleep(ms);
                        return true;
                    }

                case "script":
                    {
                        if (p.Length < 2) { Console.WriteLine("usage: script <file>"); return true; }
                        RunScript(p[1]);
                        return true;
                    }

                case "quit":
                case "exit":
                    return false;

                default:
                    Console.WriteLine("Unknown command: " + p[0] + " (type 'help')");
                    return true;
            }
        }

        /// <summary>
        /// Runs each line of <paramref name="path"/> as if typed at the
        /// prompt. Blank lines and lines starting with '#' are skipped.
        /// Use 'sleep &lt;ms&gt;' lines to pace timing-sensitive tests.
        /// </summary>
        private static void RunScript(string path)
        {
            string[] lines;
            try
            {
                lines = System.IO.File.ReadAllLines(path);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Could not read script file: " + ex.Message);
                return;
            }

            foreach (var rawLine in lines)
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;

                Console.WriteLine("> " + line);
                var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                try
                {
                    if (!Dispatch(parts)) return;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Error: " + ex.Message);
                }
            }
        }

        private static void PrintHelp()
        {
            Console.WriteLine(@"
Commands:
  devices                          List Fanatec HID devices (VID 0x0EB7)
  connect                          Open col03 (64-byte) and col01 (8-byte) interfaces

  activate [on|off]                 FF 05 02 <01|00>  -- activate/deactivate ITM rendering (default on)
  ownership host|fw                Take/release OLED ownership (col01, group 0x01 subcmd 0x18)
  pageset <deviceId> <page>        FF 05 04 <deviceId> <page>  -- deviceId 3 = BME/PBME
  keepalive                        FF 05 04 02 0B  -- send once
  auto on|off                      Background thread sending keepalive every 100ms

  paramdefs <slot> <posLo> <posHi> <suffix|-> [...]
                                    FF 05 03 ... -- define slot layout (suffix '-' = none, e.g. '/0' for total)
  value <handle> <paramId> <type> <number>
                                    FF 05 01 ... -- type: u8, i16, u16, i32, u32, f32

  raw03 <hex bytes...>              Send raw col03 frame (zero-padded to 64 bytes), e.g. raw03 FF 05 04 03 01
  raw01 <hex bytes...>              Send raw col01 frame (zero-padded to 8 bytes)
  read03 [timeoutMs]                Read one IN report from col03 (default timeout 500ms)

  sleep <ms>                        Pause (useful in scripts)
  script <file>                     Run each line of <file> as a command (# comments, blank lines skipped)

  quit / exit

Example sequence to try Page 1 (Lap Info) with current best-guess mapping:
  connect
  activate on
  pageset 3 1
  paramdefs 82 00 00 /0 83 00 00 /0 84 00 00 - 85 00 00 -
  value 0 1 i16 123        (SPEED=123)
  value 1 4 u8 3            (GEAR=3)
  value 2 505 u8 5          (LAP=5)
  value 3 501 u8 2          (POSITION=2)
  value 4 509 f32 65.4      (LAP_TIME)
  value 5 510 f32 64.1      (LAST_LAP_TIME)
  auto on                    (keep the display alive while you look)
");
        }

        private static void ListDevices()
        {
            var devices = DeviceList.Local.GetHidDevices()
                .Where(d => d.VendorID == FANATEC_VENDOR_ID)
                .ToList();

            if (devices.Count == 0)
            {
                Console.WriteLine("No Fanatec (VID 0x0EB7) HID devices found.");
                return;
            }

            foreach (var d in devices)
            {
                string product;
                try { product = d.GetProductName(); } catch { product = "?"; }

                Console.WriteLine(string.Format(
                    "PID=0x{0:X4}  MaxOut={1,3}  MaxIn={2,3}  {3}  {4}",
                    d.ProductID, d.GetMaxOutputReportLength(), d.GetMaxInputReportLength(), product, d.DevicePath));
            }
        }

        private static void Connect()
        {
            var devices = DeviceList.Local.GetHidDevices()
                .Where(d => d.VendorID == FANATEC_VENDOR_ID)
                .ToList();

            if (devices.Count == 0)
            {
                Console.WriteLine("No Fanatec devices found.");
                return;
            }

            var col03Device = devices.FirstOrDefault(d => d.DevicePath.Contains("col03"))
                ?? devices.FirstOrDefault(d => d.GetMaxOutputReportLength() == 64 || d.GetMaxOutputReportLength() == 65);
            var col01Device = devices.FirstOrDefault(d => d.DevicePath.Contains("col01"));

            if (col03Device == null)
            {
                Console.WriteLine("Could not find a col03 (64-byte) interface.");
                return;
            }

            _col03?.Close();
            _col01?.Close();

            _col03 = col03Device.Open();
            Console.WriteLine("col03 opened: " + col03Device.DevicePath);

            if (col01Device != null)
            {
                try
                {
                    _col01 = col01Device.Open();
                    Console.WriteLine("col01 opened: " + col01Device.DevicePath);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("col01 open failed (ownership command unavailable): " + ex.Message);
                }
            }
            else
            {
                Console.WriteLine("col01 interface not found (ownership command unavailable).");
            }
        }

        private static void SendKeepalive()
        {
            var buf = NewCol03();
            buf[1] = 0x05;
            buf[2] = 0x04;
            buf[3] = 0x02;
            buf[4] = 0x0B;
            SendCol03(buf, "Keepalive");
        }

        private static void StartKeepalive()
        {
            if (_keepaliveRunning) return;
            _keepaliveRunning = true;
            _keepaliveThread = new Thread(() =>
            {
                while (_keepaliveRunning)
                {
                    try
                    {
                        var buf = NewCol03();
                        buf[1] = 0x05;
                        buf[2] = 0x04;
                        buf[3] = 0x02;
                        buf[4] = 0x0B;
                        _col03?.Write(buf);
                    }
                    catch { /* ignore */ }

                    Thread.Sleep(100);
                }
            });
            _keepaliveThread.IsBackground = true;
            _keepaliveThread.Start();
            Console.WriteLine("Keepalive thread started (every 100ms).");
        }

        private static void StopKeepalive()
        {
            if (!_keepaliveRunning) return;
            _keepaliveRunning = false;
            _keepaliveThread?.Join(500);
            _keepaliveThread = null;
            Console.WriteLine("Keepalive thread stopped.");
        }

        private static byte[] NewCol03() => new byte[COL03_LEN];
        private static byte[] NewCol01() => new byte[COL01_LEN];

        private static void SendCol03(byte[] buf, string description)
        {
            buf[0] = 0xFF;
            if (_col03 == null)
            {
                Console.WriteLine("Not connected (run 'connect' first). Frame: " + ToHex(buf));
                return;
            }

            _col03.Write(buf);
            Console.WriteLine("Sent " + description + ": " + ToHex(buf.Take(16).ToArray()) + " ...");
        }

        private static void ReadCol03(int timeoutMs)
        {
            if (_col03 == null)
            {
                Console.WriteLine("Not connected (run 'connect' first).");
                return;
            }

            var buf = new byte[COL03_LEN + 1];
            try
            {
                _col03.ReadTimeout = timeoutMs;
                int n = _col03.Read(buf);
                Console.WriteLine("Read " + n + " bytes: " + ToHex(buf.Take(n).ToArray()));
            }
            catch (TimeoutException)
            {
                Console.WriteLine("Read timed out (no IN report within " + timeoutMs + "ms).");
            }
        }

        private static void SendCol01(byte[] buf, string description)
        {
            if (_col01 == null)
            {
                Console.WriteLine("col01 not connected. Frame: " + ToHex(buf));
                return;
            }

            _col01.Write(buf);
            Console.WriteLine("Sent " + description + ": " + ToHex(buf));
        }

        private static void FillFromHex(byte[] buf, System.Collections.Generic.IEnumerable<string> hexBytes)
        {
            int i = 0;
            foreach (var h in hexBytes)
            {
                if (i >= buf.Length) break;
                buf[i++] = ParseByte(h);
            }
        }

        private static byte[] EncodeValue(string type, string number)
        {
            switch (type.ToLowerInvariant())
            {
                case "u8":
                    return new[] { (byte)ParseInt(number) };
                case "i16":
                    return BitConverter.GetBytes((short)ParseInt(number));
                case "u16":
                    return BitConverter.GetBytes((ushort)ParseInt(number));
                case "i32":
                    return BitConverter.GetBytes(ParseInt(number));
                case "u32":
                    return BitConverter.GetBytes((uint)ParseInt(number));
                case "f32":
                    return BitConverter.GetBytes(float.Parse(number, CultureInfo.InvariantCulture));
                default:
                    throw new ArgumentException("Unknown type: " + type);
            }
        }

        private static byte ParseByte(string s)
        {
            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
            return byte.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        private static int ParseInt(string s)
        {
            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return int.Parse(s.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return int.Parse(s, CultureInfo.InvariantCulture);
        }

        private static string ToHex(byte[] data)
        {
            return string.Join(" ", data.Select(b => b.ToString("X2")));
        }
    }
}
