namespace FanaBridge.Protocol
{
    /// <summary>
    /// Shared helper for converting SimHub's gear string representation
    /// to the integer convention used throughout FanaBridge: "R"=-1, "N"=0, "1"-"9"=1-9.
    /// </summary>
    public static class GearParser
    {
        public static int ParseGear(string gear)
        {
            if (string.IsNullOrEmpty(gear)) return 0;

            gear = gear.Trim().ToUpperInvariant();

            if (gear == "R" || gear == "REVERSE") return -1;
            if (gear == "N" || gear == "NEUTRAL") return 0;

            int result;
            if (int.TryParse(gear, out result))
            {
                return result;
            }

            return 0;
        }
    }
}
