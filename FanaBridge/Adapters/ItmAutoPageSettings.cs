namespace FanaBridge.Adapters
{
    /// <summary>
    /// What the pit box rule shows on the display.
    /// </summary>
    public enum PitDisplayMode
    {
        Tyres,
        Fuel,
        Both
    }

    /// <summary>
    /// Configurable thresholds and rule toggles for the ITM auto page switcher.
    /// Serialized as part of DisplaySettings.
    /// </summary>
    public class ItmAutoPageSettings
    {
        // ── Rule 1: BB/TC/ABS changed → page 3 ──────────────────────────────
        public bool Page3OnControlChange { get; set; } = true;
        public double Page3ChangedDurationSeconds { get; set; } = 6.0;

        // ── Rule 2: In pit box ───────────────────────────────────────────────
        public bool PitRuleEnabled { get; set; } = true;
        public PitDisplayMode PitDisplayMode { get; set; } = PitDisplayMode.Both;
        public double PitCycleIntervalSeconds { get; set; } = 5.0;

        // ── Rule 3: Post-lap sequence ────────────────────────────────────────
        public bool PostLapRuleEnabled { get; set; } = true;
        public int PostLapPageA { get; set; } = 1;          // page number, 1-5
        public double PostLapPageADurationSeconds { get; set; } = 4.0;
        public int PostLapPageB { get; set; } = 2;          // 0 = none
        public double PostLapPageBDurationSeconds { get; set; } = 4.0;

        // ── Rule 4: Car proximity → page 4 ──────────────────────────────────
        public bool CarProximityRuleEnabled { get; set; } = true;
        public double CarProximityEnterSeconds { get; set; } = 3.0;
        public double CarProximityExitSeconds { get; set; } = 4.0;

        // ── Rule 5: Low fuel ─────────────────────────────────────────────────
        public bool LowFuelRuleEnabled { get; set; } = true;
        public double LowFuelThresholdLitres { get; set; } = 6.0;
        public int LowFuelPage { get; set; } = 2;

        // ── Session type overrides ───────────────────────────────────────────
        // Value = page number (1-5), or 0 = Auto (fall through to rules).
        public int SessionRace { get; set; } = 0;
        public int SessionQualify { get; set; } = 0;
        public int SessionPractice { get; set; } = 0;

        // ── Fallback default ─────────────────────────────────────────────────
        public int DefaultPage { get; set; } = 1;
    }
}
