namespace FanaBridge.Protocol
{
    /// <summary>
    /// ITM device IDs, per docs/reference/protocol.md "ITM Supported Devices".
    /// </summary>
    public static class ItmDeviceId
    {
        /// <summary>Wheelbase's own display (PDD1/PDD2).</summary>
        public const byte Base = 1;

        /// <summary>PBME's large OLED, or GTSWX's built-in display (shared ID).</summary>
        public const byte Bme = 3;

        /// <summary>Bentley GT3 steering wheel's built-in display.</summary>
        public const byte Bentley = 4;
    }

    /// <summary>
    /// ITM parameter IDs, per docs/reference/protocol.md "ITM Parameter IDs".
    /// Only the subset confirmed to render is included.
    /// </summary>
    public static class ItmParameterId
    {
        public const ushort Speed = 1;
        public const ushort Gear = 4;
        public const ushort Fuel = 5;
        public const ushort ErsLevel = 9;
        public const ushort Position = 501;
        public const ushort Lap = 505;
        public const ushort LapTime = 509;
        public const ushort LastLapTime = 510;
        public const ushort BestLapTime = 511;
        public const ushort CarAhead = 519;
        public const ushort CarBehind = 520;
    }

    /// <summary>
    /// "Auto" page mode: the driver chooses between Page 1, 2, and 4 each
    /// frame based on telemetry, per <see cref="FanaBridge.Adapters.FanatecItmDriver"/>.
    /// Not a real ITM page — never sent as a PageSet value.
    /// </summary>
    public static class ItmPageAuto
    {
        public const byte Page = 0;
    }

    /// <summary>
    /// Page 1 ("Lap Info") layout for Base/BME displays, per
    /// docs/reference/protocol.md "ITM Page Layouts" and
    /// "Slot &amp; Handle Mapping (Raw HID)".
    ///
    /// Confirmed working on a PBME: SPEED, GEAR, LAP, POSITION, LAP_TIME, and
    /// LAST_LAP_TIME all render correctly with this slot/handle mapping.
    /// </summary>
    public static class ItmPage1
    {
        public const byte Page = 1;

        /// <summary>Slot IDs sent via ParamDefs before switching to this page.</summary>
        public const byte SlotLap = 0x82;
        public const byte SlotPosition = 0x83;
        public const byte SlotLapTime = 0x84;
        public const byte SlotLastLapTime = 0x85;

        // Handles 0-5, matching the documented handle range for Page 1/5.
        public const byte HandleSpeed = 0;
        public const byte HandleGear = 1;
        public const byte HandleLap = 2;
        public const byte HandlePosition = 3;
        public const byte HandleLapTime = 4;
        public const byte HandleLastLapTime = 5;
    }

    /// <summary>
    /// Page 2 ("Fuel / ERS / DRS") layout for Base/BME displays, per
    /// docs/reference/protocol.md "ITM Page Layouts".
    ///
    /// Confirmed working on a PBME: FUEL and ERS_LEVEL render correctly with
    /// this slot/handle mapping. DRS_ZONE, DRS_ACTIVE, and DELTA_OWN_BEST
    /// were tried across handles 8-14 and positions 2-4 with no visible
    /// effect, so they are left unconfigured.
    /// All entries share slot 0x88, distinguished by position. Handles are
    /// assigned sequentially starting at 8 (unlike Page 4, which starts at 2)
    /// — the reason for this difference is unconfirmed.
    /// </summary>
    public static class ItmPage2
    {
        public const byte Page = 2;

        /// <summary>Shared slot ID for the dynamic fields on this page.</summary>
        public const byte Slot = 0x88;

        public const ushort PositionFuel = 0;
        public const ushort PositionErsLevel = 1;

        public const byte HandleFuel = 8;
        public const byte HandleErsLevel = 9;
    }

    /// <summary>
    /// Page 4 ("Lap Times") layout for Base/BME displays, per
    /// docs/reference/protocol.md "ITM Page Layouts".
    ///
    /// Confirmed working on a PBME: LAST_LAP_TIME, BEST_LAP_TIME, CAR_AHEAD,
    /// and CAR_BEHIND all render correctly with this slot/handle mapping.
    /// All four entries share slot 0x88, distinguished by position (0-3);
    /// handles are assigned sequentially starting at 2 (matching Page 1's
    /// scheme, where 0/1 are reserved for the persistent SPEED/GEAR header).
    /// </summary>
    public static class ItmPage4
    {
        public const byte Page = 4;

        /// <summary>Shared slot ID for all four dynamic fields on this page.</summary>
        public const byte Slot = 0x88;

        // Position values (posLo) distinguishing the four dynamic fields.
        public const ushort PositionLastLapTime = 0;
        public const ushort PositionBestLapTime = 1;
        public const ushort PositionCarAhead = 2;
        public const ushort PositionCarBehind = 3;

        public const byte HandleLastLapTime = 2;
        public const byte HandleBestLapTime = 3;
        public const byte HandleCarAhead = 4;
        public const byte HandleCarBehind = 5;
    }
}
