# ITM Auto Page Switcher — Configurable Settings UI

## Overview

The ITM driver's auto page switching currently uses hardcoded rules and thresholds.
This spec describes a planned settings UI that exposes those rules as configurable,
per-session options without changing the underlying rule logic or priority order.

## Motivation

- Users may want to tune thresholds (e.g. fuel warning level) without recompiling
- Different display preferences per session type (e.g. locked to page 1 in qualify)
- Pit box display preference varies by user (tyres vs. fuel vs. both)

## Rule Set

Rules fire in fixed priority order. Session default (if set to a specific page) bypasses
all rules below it. Within a session set to Auto, rules fire in the order listed.

| Priority | Rule | Notes |
|---|---|---|
| 1 | Session default | Per session type: specific page or Auto |
| 2 | BB/TC/ABS changed | Triggers on any change, no threshold needed |
| 3 | In pit box | Detected via `IsInPit` |
| 4 | Post-lap sequence | Fires on lap increment |
| 5 | Car proximity | Fixed logic (page 4, 3s/4s hysteresis) |
| 6 | Low fuel | Configurable threshold |
| 7 | Fallback default | Shown when no other rule fires |

## Configurable Settings Per Rule

### Session Default
- One entry per session type
- Value: specific page (1–5) or **Auto** (fall through to rules)
- Session types: verify SimHub normalises these across games before implementing.
  Expected values for ACC/iRacing: `Race`, `Qualify`, `Practice`, `Hotlap`, `Drift`
  — **confirm against `SessionTypeName` in SimHub property browser before building**

### BB/TC/ABS Changed
- Enabled: yes/no
- Display duration: seconds (default 6s)
- Target page: fixed (page 3 — Car Settings)

### In Pit Box
- Enabled: yes/no
- Display mode: Tyres only / Fuel only / Both (alternating)
- Cycle interval: seconds (default 5s) — only shown when mode = Both
- Target pages: fixed (page 5 = Tyres, page 2 = Fuel)

### Post-Lap Sequence
- Enabled: yes/no
- Page A: selectable (default: page 1)
- Page A duration: seconds (default 4s)
- Page B: selectable or None (default: page 2)
- Page B duration: seconds (default 4s) — only shown when Page B is set
- Sequence is ordered (A then B), not a cycle

### Car Proximity
- Enabled: yes/no
- All other parameters fixed (page 4, enter threshold 3s, exit threshold 4s)

### Low Fuel
- Enabled: yes/no
- Fuel threshold: litres (default 6L, with 1L hysteresis on exit)
- Target page: configurable (default: page 2)

### Fallback Default
- Page: selectable (default: page 1)
- Always active, no enable/disable

## Settings Model (C#)

```csharp
public class ItmAutoPageSettings
{
    // Session defaults — keyed by session type name
    public Dictionary<string, byte> SessionPageOverrides { get; set; } = new();

    // BB/TC/ABS rule
    public bool Page3OnControlChange { get; set; } = true;
    public double Page3ChangedDurationSeconds { get; set; } = 6.0;

    // Pit box rule
    public bool PitRuleEnabled { get; set; } = true;
    public PitDisplayMode PitDisplayMode { get; set; } = PitDisplayMode.Both;
    public double PitCycleIntervalSeconds { get; set; } = 5.0;

    // Post-lap rule
    public bool PostLapRuleEnabled { get; set; } = true;
    public byte PostLapPageA { get; set; } = ItmPage1.Page;
    public double PostLapPageADurationSeconds { get; set; } = 4.0;
    public byte PostLapPageB { get; set; } = ItmPage2.Page;
    public double PostLapPageBDurationSeconds { get; set; } = 4.0;

    // Car proximity rule
    public bool CarProximityRuleEnabled { get; set; } = true;

    // Low fuel rule
    public bool LowFuelRuleEnabled { get; set; } = true;
    public double LowFuelThresholdLitres { get; set; } = 6.0;
    public byte LowFuelPage { get; set; } = ItmPage2.Page;

    // Fallback
    public byte DefaultPage { get; set; } = ItmPage1.Page;
}

public enum PitDisplayMode { Tyres, Fuel, Both }
```

## UI Layout

A single WPF `UserControl` returned from `GetWPFSettingsControl()`, structured as a
scrollable stack of collapsible rule sections. Each section has:

- A header with the rule name and an enabled toggle (where applicable)
- Threshold/option controls beneath, greyed out when disabled

Session defaults sit at the top as a small table (session type → page dropdown).

## Implementation Notes

- Settings persisted via SimHub's `PluginManager.GetSettings` / `SaveSettings`
- Driver reads settings on each `ComputeAutoPage` call — no restart required
- `PitDisplayMode.Tyres` skips the cycle and always returns page 5;
  `PitDisplayMode.Fuel` always returns page 2; `PitDisplayMode.Both` cycles as today
- Post-lap with `PostLapPageB = 0` (None) skips the second leg of the sequence
- Session type names **must be verified** against live SimHub data before the
  session override feature is built — use SimHub's property browser in ACC and iRacing
  and log `data.NewData.SessionTypeName` to confirm exact strings

## Open Questions

1. Does SimHub normalise `SessionTypeName` across games, or is it game-specific?
   If game-specific, the session override feature may need a different approach
   (e.g. user types the session name string rather than selecting from a fixed list)
2. Should the post-lap sequence support more than 2 pages?
3. Should car proximity thresholds (3s/4s) be exposed, or remain fixed?
