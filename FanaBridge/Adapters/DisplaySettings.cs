namespace FanaBridge.Adapters
{
    /// <summary>
    /// Type-safe display configuration.
    /// Serialized to/from the device instance's JObject settings.
    /// </summary>
    public class DisplaySettings
    {
        public const string DefaultMode = "Gear";

        /// <summary>
        /// Display mode: "Gear", "Speed", "GearAndSpeed", or "GearUpshiftBrackets".
        /// </summary>
        public string DisplayMode { get; set; } = DefaultMode;

        /// <summary>
        /// When true and the wheel has an ITM-capable display, drive it with
        /// the ITM page layout instead of the basic 7-seg display.
        /// </summary>
        public bool ItmEnabled { get; set; } = false;

        /// <summary>
        /// The ITM page to display: 1 ("Lap Info"), 2 ("Fuel / ERS"), or
        /// 4 ("Lap Times"). 0 selects "Auto", where the driver chooses
        /// between those pages each frame based on telemetry.
        /// </summary>
        public int ItmPage { get; set; } = 1;
    }
}
