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
        public const ushort DrsZone = 14;
        public const ushort DrsActive = 15;
        public const ushort AbsSetting = 18;
        public const ushort TcSetting = 20;
        public const ushort BrakeBias = 25;
        public const ushort OilTemp = 33;
        public const ushort TyreFlTemp = 42;
        public const ushort TyreFrTemp = 45;
        public const ushort TyreRlTemp = 48;
        public const ushort TyreRrTemp = 51;
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
    /// All confirmed working on a PBME. All dynamic fields share slot 0x88,
    /// distinguished by position (0-3). Handle numbering starts at 2 (same
    /// as all other pages) — SPEED/GEAR use handles 0/1 like every other page.
    ///
    /// Note: earlier probe sessions observed handles 6/7 for SPEED/GEAR and
    /// 8/9 for FUEL/ERS. This was an artefact of the firmware's global handle
    /// table being contaminated by prior page activations. With an
    /// activate-off → 300ms → activate-on reset before every page switch,
    /// the table is cleared and all pages assign handles sequentially from 2.
    /// </summary>
    public static class ItmPage2
    {
        public const byte Page = 2;

        /// <summary>Shared slot ID for all dynamic fields on this page.</summary>
        public const byte Slot = 0x88;

        public const ushort PositionFuel = 0;
        public const ushort PositionErsLevel = 1;
        public const ushort PositionDrsZone = 2;
        public const ushort PositionDrsActive = 3;

        public const byte HandleFuel = 2;
        public const byte HandleErsLevel = 3;
        public const byte HandleDrsZone = 4;
        public const byte HandleDrsActive = 5;
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

    /// <summary>
    /// Page 3 ("Car Settings") layout for Base/BME displays, per
    /// docs/reference/protocol.md "ITM Page Layouts".
    ///
    /// Confirmed working on a PBME: TC_SETTING, ABS_SETTING, OIL_TEMP, and
    /// BRAKE_BIAS all render. The main slot is 0x85 (handles 2-5); brake bias
    /// uses a separate slot 0x86 (handle 6). Position 2 within slot 0x85 is
    /// declared as a placeholder to push OIL_TEMP to handle 5 — the field at
    /// position 2 is unresponsive on a PBME.
    ///
    /// BRAKE_BIAS is sent as i16 with ×10 scaling (send 543 to display 54.3%).
    /// Values above 80.0 have been observed to cause firmware instability on
    /// the PBME — cap at <see cref="BrakeBiasMaxSafe"/>.
    /// </summary>
    public static class ItmPage3
    {
        public const byte Page = 3;

        public const byte Slot = 0x85;
        public const byte SlotBrakeBias = 0x86;

        public const ushort PositionTc = 0;
        public const ushort PositionAbs = 1;
        public const ushort PositionOilTemp = 3;

        public const byte HandleTc = 2;
        public const byte HandleAbs = 3;
        public const byte HandleOilTemp = 5;
        public const byte HandleBrakeBias = 6;

        public const float BrakeBiasMaxSafe = 80.0f;
    }

    /// <summary>
    /// Page 5 ("Tyre Temps") layout for Base/BME displays, per
    /// docs/reference/protocol.md "ITM Page Layouts".
    ///
    /// Confirmed working on a PBME: all four tyre temps render correctly.
    /// Each corner has its own dedicated slot (0x82-0x85), matching Page 1's
    /// per-field scheme. Handles are assigned sequentially starting at 2.
    ///
    /// The firmware validates paramId against the slot — sending a
    /// mismatched paramId (e.g. TYRE_FR to handle 3) produces no output.
    ///
    /// Physical screen layout: handles group by column, not row —
    /// FL(2)/RL(3) are the left column, FR(4)/RR(5) the right column.
    /// No suffix needed; the firmware appends "C" (Celsius) automatically.
    /// </summary>
    public static class ItmPage5
    {
        public const byte Page = 5;

        public const byte SlotFl = 0x82;
        public const byte SlotRl = 0x83;
        public const byte SlotFr = 0x84;
        public const byte SlotRr = 0x85;

        public const byte HandleFl = 2;
        public const byte HandleRl = 3;
        public const byte HandleFr = 4;
        public const byte HandleRr = 5;
    }
}
