using FanaBridge.Transport;

namespace FanaBridge.Protocol
{
    /// <summary>
    /// Reads the FF 08 system identity report from col03 and decodes the
    /// attached wheel module. Used to gate ITM features on PBME hardware.
    ///
    /// Byte offsets are relative to the leading 0xFF byte:
    ///   [0x02] BaseType  — wheelbase model
    ///   [0x18] WheelCode — wheel or hub type
    ///   [0x1F] Module    — attached button module (0x01 = PBME)
    ///
    /// Confirmed on: ClubSport DD+ / Podium Hub / PBME (identifier PHUB_PBME).
    /// </summary>
    public static class ItmIdentity
    {
        private const byte ReportId = 0xFF;
        private const byte ReportType = 0x08;
        private const int ModuleOffset = 0x1F;
        private const byte ModulePbme = 0x01;
        private const int ReadTimeoutMs = 500;

        /// <summary>
        /// Returns true if the connected device has a PBME module attached.
        /// Sends an FF 08 request and reads the response from col03.
        /// </summary>
        public static bool IsPbmeAttached(IDeviceTransport transport)
        {
            if (!transport.IsConnected) return false;

            var request = new byte[64];
            request[0] = ReportId;
            request[1] = ReportType;

            if (!transport.SendCol03(request)) return false;

            var response = new byte[transport.Col03MaxInputReportLength];
            int read = transport.ReadCol03(response, ReadTimeoutMs);
            if (read < ModuleOffset + 1) return false;

            return response[ModuleOffset] == ModulePbme;
        }
    }
}
