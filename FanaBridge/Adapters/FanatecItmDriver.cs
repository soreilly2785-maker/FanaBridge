using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FanaBridge.Protocol;
using FanaBridge.Transport;
using GameReaderCommon;

namespace FanaBridge.Adapters
{
    /// <summary>
    /// Drives the ITM display (col03). SPEED and GEAR are sent as persistent
    /// header fields on every page. The active page's other fields are sent
    /// per its confirmed layout:
    /// - Page 1 ("Lap Info"): LAP, POSITION, LAP_TIME, LAST_LAP_TIME.
    /// - Page 2 ("Fuel / ERS / DRS"): FUEL, ERS_LEVEL.
    /// - Page 4 ("Lap Times"): LAST_LAP_TIME, BEST_LAP_TIME, CAR_AHEAD, CAR_BEHIND.
    ///
    /// Sends Activate + PageSet + ParamDefs once on init (or page change), a
    /// Keepalive every ~100ms, and ValueUpdate (for whichever tracked values
    /// have changed) at most every <see cref="ValueUpdateInterval"/>.
    /// </summary>
    public class FanatecItmDriver
    {
        private static readonly TimeSpan KeepaliveInterval = TimeSpan.FromMilliseconds(100);
        private static readonly TimeSpan ValueUpdateInterval = TimeSpan.FromMilliseconds(100);

        // After (re)init (Activate/PageSet/ParamDefs), give the display a
        // moment to apply the new page/slot layout before writing values —
        // ValueUpdates sent immediately after a page switch appear to be
        // dropped, leaving the display "frozen" until the next switch.
        private static readonly TimeSpan PostInitSettleDelay = TimeSpan.FromMilliseconds(300);

        // "Auto" page mode timing/thresholds. Enter/exit pairs use hysteresis
        // so a value hovering near the threshold doesn't flip the page (and
        // trigger a full re-init) every frame.
        private static readonly TimeSpan AutoPostLapPage2Duration = TimeSpan.FromSeconds(6);
        private const double AutoCarNearEnterSeconds = 4.0;
        private const double AutoCarNearExitSeconds = 5.0;
        private const double AutoLowFuelEnterLitres = 6.0;
        private const double AutoLowFuelExitLitres = 7.0;

        // Minimum time between Auto-mode page switches triggered by the
        // near-car/low-fuel checks (each switch is a full re-init, which
        // risks the "frozen" settle window above). Lap-triggered switches
        // (post-lap Page 1/Page 2) are exempt — they're already paced by lap
        // timing and are core to the requested behaviour.
        private static readonly TimeSpan AutoSwitchMinDwell = TimeSpan.FromSeconds(5);

        private readonly ItmDisplayController _itm;
        private readonly byte _deviceId;

        // The page selected in settings: ItmPage1/2/4.Page, or
        // ItmPageAuto.Page (0) to let the driver choose each frame.
        private byte _configuredPage = ItmPage1.Page;

        // The page currently being driven (always 1, 2, or 4).
        private byte _page = ItmPage1.Page;
        private bool _activated;
        private bool _everInitialized;
        private bool _initialized;
        private bool _page1TotalsSent;
        private DateTime _page1TotalsCheckUntil = DateTime.MinValue;
        private bool _page2CapacitySent;
        private int _page2LastSentMaxFuelInt = -1;
        private DateTime _page2CapacityCheckUntil = DateTime.MinValue;
        private DateTime _lastKeepalive = DateTime.MinValue;
        private DateTime _lastValueUpdate = DateTime.MinValue;
        private DateTime _initializedAt = DateTime.MinValue;

        // "Auto" page mode state.
        private int _autoLastLap = int.MinValue;
        private DateTime _autoLapChangedAt = DateTime.MinValue;
        private DateTime _lastAutoSwitchAt = DateTime.MinValue;

        private int _lastSpeed = int.MinValue;
        private int _lastGear = int.MinValue;
        private int _lastLap = int.MinValue;
        private int _lastPosition = int.MinValue;
        private float _lastLapTime = float.MinValue;
        private float _lastLastLapTime = float.MinValue;
        private float _lastBestLapTime = float.MinValue;
        private float _lastCarAhead = float.MinValue;
        private float _lastCarBehind = float.MinValue;
        private float _lastFuel = float.MinValue;
        private int _lastErsLevel = int.MinValue;

        public FanatecItmDriver(IDeviceTransport transport, byte deviceId = ItmDeviceId.Bme)
        {
            _itm = new ItmDisplayController(transport);
            _deviceId = deviceId;
        }

        /// <summary>
        /// Updates the ITM display from telemetry. Called once per frame.
        /// </summary>
        public void Update(GameData data)
        {
            if (data.NewData == null || !_itm.IsConnected) return;

            var now = DateTime.UtcNow;

            if (_configuredPage == ItmPageAuto.Page)
                SwitchActivePage(ComputeAutoPage(data, now), now);

            EnsureInitialized(data, now);
            ResendPage1ParamDefsIfTotalsNowKnown(data);
            ResendPage2ParamDefsIfCapacityNowKnown(data);
            SendKeepaliveIfDue();

            if (now - _lastValueUpdate >= ValueUpdateInterval && now - _initializedAt >= PostInitSettleDelay)
            {
                SendValueUpdates(data);
                _lastValueUpdate = now;
            }
        }

        /// <summary>
        /// Sets the configured ITM page: 1, 2, or 4 to pin the display to
        /// that page, or 0 ("Auto") to let the driver choose between them
        /// each frame based on telemetry (see <see cref="ComputeAutoPage"/>).
        /// Takes effect on the next Update() call.
        /// </summary>
        public void SetPage(int page)
        {
            byte newConfiguredPage = page == ItmPageAuto.Page || page == ItmPage1.Page
                || page == ItmPage2.Page || page == ItmPage4.Page
                ? (byte)page
                : ItmPage1.Page;

            _configuredPage = newConfiguredPage;

            if (newConfiguredPage != ItmPageAuto.Page)
                SwitchActivePage(newConfiguredPage, DateTime.UtcNow);
        }

        /// <summary>Switches the page currently being driven (1, 2, or 4).</summary>
        private void SwitchActivePage(byte newPage, DateTime now)
        {
            if (newPage == _page) return;

            _page = newPage;
            _initialized = false;
            _page1TotalsSent = false;
            _page2CapacitySent = false;
            _lastAutoSwitchAt = now;
        }

        /// <summary>
        /// "Auto" page mode: shows Page 2 for a few seconds after crossing
        /// the line, then Page 4 if a car is close ahead/behind, then Page 2
        /// if fuel is low, otherwise Page 1.
        /// </summary>
        private byte ComputeAutoPage(GameData data, DateTime now)
        {
            int lap = data.NewData.CurrentLap;
            if (_autoLastLap == int.MinValue)
            {
                _autoLastLap = lap;
            }
            else if (lap != _autoLastLap)
            {
                _autoLastLap = lap;
                _autoLapChangedAt = now;
            }

            if (_autoLapChangedAt != DateTime.MinValue)
            {
                if (now - _autoLapChangedAt < AutoPostLapPage2Duration)
                    return ItmPage2.Page;
            }

            // Hysteresis: once on Page 4 for a near car, stay until the gap
            // widens past the (larger) exit threshold; only switch in once
            // it's inside the (smaller) enter threshold.
            double carNearThreshold = _page == ItmPage4.Page ? AutoCarNearExitSeconds : AutoCarNearEnterSeconds;
            double? aheadGap = data.NewData.OpponentsAheadOnTrack?.FirstOrDefault()?.GaptoPlayer;
            double? behindGap = data.NewData.OpponentsBehindOnTrack?.FirstOrDefault()?.GaptoPlayer;

            // Ignore proximity during lap 1 — the grid starts bunched up, so
            // gaps to the cars ahead/behind are meaningless and would
            // otherwise force Page 4 for the whole opening lap.
            bool carNear = lap > 1
                && ((aheadGap.HasValue && Math.Abs(aheadGap.Value) < carNearThreshold)
                    || (behindGap.HasValue && Math.Abs(behindGap.Value) < carNearThreshold));

            // Same hysteresis for the low-fuel check.
            double lowFuelThreshold = _page == ItmPage2.Page ? AutoLowFuelExitLitres : AutoLowFuelEnterLitres;

            byte proposed = carNear ? ItmPage4.Page
                : data.NewData.Fuel < lowFuelThreshold ? ItmPage2.Page
                : ItmPage1.Page;

            // These checks run every frame and can flap near a threshold even
            // with hysteresis. Cap how often they're allowed to actually
            // switch pages, since each switch is a full re-init.
            if (proposed != _page && now - _lastAutoSwitchAt < AutoSwitchMinDwell)
                return _page;

            return proposed;
        }

        /// <summary>Resets cached state so the next Update() re-sends Activate/PageSet/ParamDefs.</summary>
        public void Clear()
        {
            _activated = false;
            _initialized = false;
            ResetValueTrackers();
            _autoLastLap = int.MinValue;
            _autoLapChangedAt = DateTime.MinValue;
            _lastAutoSwitchAt = DateTime.MinValue;
            _page1TotalsSent = false;
            _page1TotalsCheckUntil = DateTime.MinValue;
        }

        /// <summary>
        /// Turns off ITM rendering while not in a race session (e.g. at the
        /// main menu or between sessions), returning the display to its
        /// normal firmware-driven content instead of leaving the last
        /// telemetry values frozen on screen. The next Update() while in a
        /// session re-activates and re-initializes from scratch.
        /// </summary>
        public void Deactivate()
        {
            if (!_activated || !_itm.IsConnected) return;

            _itm.SendItmActivate(false);
            _activated = false;
            _initialized = false;
        }

        /// <summary>
        /// Clears the "last sent" trackers for all telemetry fields, so the
        /// next SendValueUpdates() resends the current values regardless of
        /// what was last written to the display.
        /// </summary>
        private void ResetValueTrackers()
        {
            _lastSpeed = int.MinValue;
            _lastGear = int.MinValue;
            _lastLap = int.MinValue;
            _lastPosition = int.MinValue;
            _lastLapTime = float.MinValue;
            _lastLastLapTime = float.MinValue;
            _lastBestLapTime = float.MinValue;
            _lastCarAhead = float.MinValue;
            _lastCarBehind = float.MinValue;
            _lastFuel = float.MinValue;
            _lastErsLevel = int.MinValue;
        }

        private void EnsureInitialized(GameData data, DateTime now)
        {
            if (_initialized) return;

            // Activate only needs to be sent once — it switches the display
            // into ITM rendering mode and stays there. Re-sending it on every
            // page switch is unnecessary extra HID traffic.
            bool okActivate = true;
            if (!_activated)
            {
                okActivate = _itm.SendItmActivate();
                _activated = okActivate;
            }

            // Page 1's ParamDefs are the only ones confirmed to correctly set
            // up the persistent SPEED/GEAR header. If Auto mode picks a
            // different page for the very first init of a session, bootstrap
            // Page 1 first so that header is established correctly, then
            // immediately switch to the actually-requested page below.
            if (!_everInitialized && _page != ItmPage1.Page)
            {
                _itm.SendPageSet(_deviceId, ItmPage1.Page);
                _itm.SendParamDefs(BuildPage1ParamDefs(data));
            }
            _everInitialized = true;

            bool okPageSet = _itm.SendPageSet(_deviceId, _page);

            // Only Page 1 ("Lap Info"), Page 2 ("Fuel / ERS / DRS"), and
            // Page 4 ("Lap Times") have a confirmed slot/handle mapping.
            bool okParamDefs = true;
            if (_page == ItmPage1.Page)
            {
                okParamDefs = _itm.SendParamDefs(BuildPage1ParamDefs(data));
                // OpponentsCount == 0 is valid for a solo session ("/1" is
                // correct), so only TotalLaps gates the resend below — it's
                // reliably 0 until the session fully loads. Give up after 10s
                // (time-based races may have TotalLaps == 0 indefinitely).
                _page1TotalsSent = data.NewData.TotalLaps > 0;
                _page1TotalsCheckUntil = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            }
            else if (_page == ItmPage2.Page)
            {
                int maxFuelInt = ComputeMaxFuelInt(data);
                okParamDefs = _itm.SendParamDefs(BuildPage2ParamDefs(maxFuelInt));
                _page2LastSentMaxFuelInt = maxFuelInt;
                _page2CapacitySent = false;
                _page2CapacityCheckUntil = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            }
            else if (_page == ItmPage4.Page)
                okParamDefs = _itm.SendParamDefs(BuildPage4ParamDefs());

            // Clear the "last sent" trackers so the next SendValueUpdates()
            // resends the real current values for this page even if they
            // happen to match whatever was last sent on the previous page.
            ResetValueTrackers();

            SimHub.Logging.Current.Info(
                "FanatecItmDriver: init sends - activate=" + okActivate +
                " pageSet=" + okPageSet + " paramDefs=" + okParamDefs +
                " page=" + _page + " deviceId=" + _deviceId);

            _initialized = true;
            _initializedAt = now;
        }

        /// <summary>
        /// Builds the Page 1 slot layout. The firmware always renders a "/"
        /// after LAP and POSITION; appending the total as an ASCII suffix
        /// (e.g. "/20") fills in the value after it.
        /// </summary>
        private static IReadOnlyList<ItmDisplayController.ParamDefEntry> BuildPage1ParamDefs(GameData data)
        {
            int totalLaps = Clamp(data.NewData.TotalLaps, 0, byte.MaxValue);
            int totalCars = Clamp(data.NewData.OpponentsCount + 1, 0, byte.MaxValue);

            return new[]
            {
                new ItmDisplayController.ParamDefEntry(ItmPage1.SlotLap, 0x0000, SuffixFor(totalLaps)),
                new ItmDisplayController.ParamDefEntry(ItmPage1.SlotPosition, 0x0000, SuffixFor(totalCars)),
                new ItmDisplayController.ParamDefEntry(ItmPage1.SlotLapTime, 0x0000),
                new ItmDisplayController.ParamDefEntry(ItmPage1.SlotLastLapTime, 0x0000),
            };
        }

        /// <summary>
        /// Builds the Page 2 slot layout. FUEL gets a "/&lt;capacity&gt;"
        /// suffix (tank capacity), matching Page 1's LAP/POSITION suffix
        /// pattern.
        /// </summary>
        private static IReadOnlyList<ItmDisplayController.ParamDefEntry> BuildPage2ParamDefs(int maxFuelInt)
        {
            return new[]
            {
                new ItmDisplayController.ParamDefEntry(ItmPage2.Slot, ItmPage2.PositionFuel, SuffixFor(maxFuelInt)),
                new ItmDisplayController.ParamDefEntry(ItmPage2.Slot, ItmPage2.PositionErsLevel),
            };
        }

        /// <summary>
        /// TotalLaps (and thus the LAP field's "/&lt;totalLaps&gt;" suffix) may
        /// read 0 on the frame Page 1's ParamDefs are first sent, while the
        /// session is still loading. Once it becomes nonzero, resend
        /// ParamDefs so the suffix appears. Gives up after 10s in case this
        /// is a time-based race with no lap limit.
        /// </summary>
        private void ResendPage1ParamDefsIfTotalsNowKnown(GameData data)
        {
            if (_page != ItmPage1.Page || _page1TotalsSent) return;

            if (data.NewData.TotalLaps <= 0)
            {
                if (DateTime.UtcNow >= _page1TotalsCheckUntil)
                    _page1TotalsSent = true;
                return;
            }

            _itm.SendParamDefs(BuildPage1ParamDefs(data));
            _page1TotalsSent = true;
        }

        /// <summary>
        /// Tank capacity for the Page 2 FUEL suffix. MaxFuel and
        /// CarSettings_MaxFUEL are sometimes 0 on the first telemetry frames
        /// (and in some replays indefinitely); fall back to deriving capacity
        /// from Fuel / FuelPercent when those are unavailable.
        /// </summary>
        private static int ComputeMaxFuelInt(GameData data)
        {
            double maxFuel = data.NewData.MaxFuel;
            if (maxFuel <= 0) maxFuel = data.NewData.CarSettings_MaxFUEL;
            if (maxFuel <= 0 && data.NewData.FuelPercent > 0)
                maxFuel = data.NewData.Fuel / (data.NewData.FuelPercent / 100.0);

            return Clamp((int)Math.Round(maxFuel), 0, byte.MaxValue);
        }

        /// <summary>
        /// MaxFuel/CarSettings_MaxFUEL/FuelPercent may all read 0 — or an
        /// implausibly small transient value derived from early Fuel/FuelPercent
        /// readings — on the frames right after Page 2's ParamDefs are first
        /// sent. Keep resending ParamDefs whenever the computed capacity
        /// changes, until it's been stable for 10s, so a wrong early "/&lt;n&gt;"
        /// suffix gets corrected once the real value settles.
        /// </summary>
        private void ResendPage2ParamDefsIfCapacityNowKnown(GameData data)
        {
            if (_page != ItmPage2.Page || _page2CapacitySent) return;

            if (DateTime.UtcNow >= _page2CapacityCheckUntil)
            {
                _page2CapacitySent = true;
                return;
            }

            int maxFuelInt = ComputeMaxFuelInt(data);
            if (maxFuelInt <= 0 || maxFuelInt == _page2LastSentMaxFuelInt) return;

            _itm.SendParamDefs(BuildPage2ParamDefs(maxFuelInt));
            _page2LastSentMaxFuelInt = maxFuelInt;
        }

        /// <summary>
        /// Builds the Page 4 slot layout: four entries sharing slot 0x88,
        /// distinguished by position (0-3).
        /// </summary>
        private static IReadOnlyList<ItmDisplayController.ParamDefEntry> BuildPage4ParamDefs()
        {
            return new[]
            {
                new ItmDisplayController.ParamDefEntry(ItmPage4.Slot, ItmPage4.PositionLastLapTime),
                new ItmDisplayController.ParamDefEntry(ItmPage4.Slot, ItmPage4.PositionBestLapTime),
                new ItmDisplayController.ParamDefEntry(ItmPage4.Slot, ItmPage4.PositionCarAhead),
                new ItmDisplayController.ParamDefEntry(ItmPage4.Slot, ItmPage4.PositionCarBehind),
            };
        }

        private static byte[] SuffixFor(int total)
        {
            return total > 0 ? Encoding.ASCII.GetBytes("/" + total) : Array.Empty<byte>();
        }

        private void SendKeepaliveIfDue()
        {
            var now = DateTime.UtcNow;
            if (now - _lastKeepalive < KeepaliveInterval) return;

            _itm.SendKeepalive();
            _lastKeepalive = now;
        }

        private void SendValueUpdates(GameData data)
        {
            // Only Page 1 ("Lap Info"), Page 2 ("Fuel / ERS / DRS"), and
            // Page 4 ("Lap Times") have a confirmed slot/handle mapping.
            if (_page != ItmPage1.Page && _page != ItmPage2.Page && _page != ItmPage4.Page) return;

            var entries = new List<ItmDisplayController.ValueUpdateEntry>();

            // SPEED and GEAR are persistent header fields on every page.
            int speed = Clamp((int)Math.Round(data.NewData.SpeedKmh), 0, short.MaxValue);
            if (speed != _lastSpeed)
            {
                entries.Add(new ItmDisplayController.ValueUpdateEntry(
                    ItmPage1.HandleSpeed, ItmParameterId.Speed, BitConverter.GetBytes((short)speed)));
                _lastSpeed = speed;
            }

            int gear = GearParser.ParseGear(data.NewData.Gear);
            if (gear != _lastGear)
            {
                // Reverse (-1) has no confirmed ITM encoding yet; send as neutral
                // until verified on hardware.
                byte gearByte = (byte)Clamp(gear, 0, 9);
                entries.Add(new ItmDisplayController.ValueUpdateEntry(
                    ItmPage1.HandleGear, ItmParameterId.Gear, new[] { gearByte }));
                _lastGear = gear;
            }

            if (_page == ItmPage1.Page)
                AddPage1ValueUpdates(data, entries);
            else if (_page == ItmPage2.Page)
                AddPage2ValueUpdates(data, entries);
            else if (_page == ItmPage4.Page)
                AddPage4ValueUpdates(data, entries);

            if (entries.Count > 0)
            {
                bool ok = _itm.SendValueUpdate(entries);
                SimHub.Logging.Current.Info(
                    "FanatecItmDriver: ValueUpdate (" + entries.Count + " entries) ok=" + ok);
            }
        }

        private void AddPage1ValueUpdates(GameData data, List<ItmDisplayController.ValueUpdateEntry> entries)
        {
            int lap = data.NewData.CurrentLap;
            if (lap != _lastLap)
            {
                entries.Add(new ItmDisplayController.ValueUpdateEntry(
                    ItmPage1.HandleLap, ItmParameterId.Lap, new[] { (byte)Clamp(lap, 0, byte.MaxValue) }));
                _lastLap = lap;
            }

            int position = data.NewData.PlayerLeaderboardPosition;
            if (position != _lastPosition)
            {
                entries.Add(new ItmDisplayController.ValueUpdateEntry(
                    ItmPage1.HandlePosition, ItmParameterId.Position, new[] { (byte)Clamp(position, 0, byte.MaxValue) }));
                _lastPosition = position;
            }

            float lapTime = (float)data.NewData.CurrentLapTime.TotalSeconds;
            if (Math.Abs(lapTime - _lastLapTime) > float.Epsilon)
            {
                entries.Add(new ItmDisplayController.ValueUpdateEntry(
                    ItmPage1.HandleLapTime, ItmParameterId.LapTime, BitConverter.GetBytes(lapTime)));
                _lastLapTime = lapTime;
            }

            float lastLapTime = (float)data.NewData.LastLapTime.TotalSeconds;
            if (Math.Abs(lastLapTime - _lastLastLapTime) > float.Epsilon)
            {
                entries.Add(new ItmDisplayController.ValueUpdateEntry(
                    ItmPage1.HandleLastLapTime, ItmParameterId.LastLapTime, BitConverter.GetBytes(lastLapTime)));
                _lastLastLapTime = lastLapTime;
            }
        }

        private void AddPage2ValueUpdates(GameData data, List<ItmDisplayController.ValueUpdateEntry> entries)
        {
            float fuel = (float)data.NewData.Fuel;
            if (Math.Abs(fuel - _lastFuel) > float.Epsilon)
            {
                entries.Add(new ItmDisplayController.ValueUpdateEntry(
                    ItmPage2.HandleFuel, ItmParameterId.Fuel, BitConverter.GetBytes(fuel)));
                _lastFuel = fuel;
            }

            // Cars without ERS report ERSMax == 0; show remaining fuel % in
            // that slot instead so it isn't just stuck at 0.
            double ersOrFuelPercent = data.NewData.ERSMax > 0
                ? data.NewData.ERSPercent
                : data.NewData.FuelPercent;

            int ersLevel = Clamp((int)Math.Round(ersOrFuelPercent), 0, 100);
            if (ersLevel != _lastErsLevel)
            {
                entries.Add(new ItmDisplayController.ValueUpdateEntry(
                    ItmPage2.HandleErsLevel, ItmParameterId.ErsLevel, BitConverter.GetBytes(ersLevel)));
                _lastErsLevel = ersLevel;
            }
        }

        private void AddPage4ValueUpdates(GameData data, List<ItmDisplayController.ValueUpdateEntry> entries)
        {
            float lastLapTime = (float)data.NewData.LastLapTime.TotalSeconds;
            if (Math.Abs(lastLapTime - _lastLastLapTime) > float.Epsilon)
            {
                entries.Add(new ItmDisplayController.ValueUpdateEntry(
                    ItmPage4.HandleLastLapTime, ItmParameterId.LastLapTime, BitConverter.GetBytes(lastLapTime)));
                _lastLastLapTime = lastLapTime;
            }

            float bestLapTime = (float)data.NewData.BestLapTime.TotalSeconds;
            if (Math.Abs(bestLapTime - _lastBestLapTime) > float.Epsilon)
            {
                entries.Add(new ItmDisplayController.ValueUpdateEntry(
                    ItmPage4.HandleBestLapTime, ItmParameterId.BestLapTime, BitConverter.GetBytes(bestLapTime)));
                _lastBestLapTime = bestLapTime;
            }

            float carAhead = (float)(data.NewData.OpponentsAheadOnTrack?.FirstOrDefault()?.GaptoPlayer ?? 0.0);
            if (Math.Abs(carAhead - _lastCarAhead) > float.Epsilon)
            {
                entries.Add(new ItmDisplayController.ValueUpdateEntry(
                    ItmPage4.HandleCarAhead, ItmParameterId.CarAhead, BitConverter.GetBytes(carAhead)));
                _lastCarAhead = carAhead;
            }

            float carBehind = (float)(data.NewData.OpponentsBehindOnTrack?.FirstOrDefault()?.GaptoPlayer ?? 0.0);
            if (Math.Abs(carBehind - _lastCarBehind) > float.Epsilon)
            {
                entries.Add(new ItmDisplayController.ValueUpdateEntry(
                    ItmPage4.HandleCarBehind, ItmParameterId.CarBehind, BitConverter.GetBytes(carBehind)));
                _lastCarBehind = carBehind;
            }
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }
}
