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
    /// Drives the ITM display (col03). All pages use handles 0/1 for the
    /// persistent SPEED/GEAR header, with per-page dynamic fields assigned
    /// sequentially from handle 2 via ParamDefs.
    ///
    /// Page switching uses an activate-off → 300ms delay → activate-on cycle
    /// to clear the firmware's global handle table before each page's ParamDefs
    /// are committed. Without this reset the table gets locked by whichever
    /// page first commits handles 2-5, causing subsequent pages to see null
    /// values for those handles regardless of what ValueUpdates are sent.
    ///
    /// Pages 1/2/3/5 require three keepalive frames (at 100ms intervals) plus
    /// a ParamDefs resend and a 500ms settle before values render reliably.
    /// Page 4 requires only a single keepalive followed by a 1000ms settle —
    /// multiple keepalives corrupt page 4's firmware state on the PBME.
    /// </summary>
    public class FanatecItmDriver
    {
        private static readonly TimeSpan FastValueUpdateInterval = TimeSpan.FromMilliseconds(100);
        private static readonly TimeSpan SlowValueUpdateInterval = TimeSpan.FromMilliseconds(500);

        // Time to wait after sending activate-off before sending activate-on.
        // Confirmed on PBME: instant back-to-back deactivate/activate does not
        // clear the firmware handle table; 300ms is sufficient.
        private static readonly TimeSpan DeactivateDelay = TimeSpan.FromMilliseconds(300);

        // Interval between the three keepalive frames in the kick sequence.
        private static readonly TimeSpan KickInterval = TimeSpan.FromMilliseconds(100);

        // Settle delay after the final kick: 500ms for pages 1/2/3/5,
        // 1000ms for page 4 (longer settle required; shorter causes null values).
        private static readonly TimeSpan KickSettleDelayNormal = TimeSpan.FromMilliseconds(500);
        private static readonly TimeSpan KickSettleDelayPage4 = TimeSpan.FromMilliseconds(1000);

        private static readonly TimeSpan AutoEvalInterval = TimeSpan.FromSeconds(1.5);
        private const double AutoCarNearEnterSeconds = 3.0;
        private const double AutoCarNearExitSeconds = 4.0;

        private readonly ItmDisplayController _itm;
        private readonly byte _deviceId;
        private ItmAutoPageSettings _autoSettings = new ItmAutoPageSettings();

        private byte _configuredPage = ItmPage1.Page;
        private byte _page = ItmPage1.Page;

        private bool _activated;
        private bool _connected;

        // Deactivate phase: sent activate-off, waiting DeactivateDelay before activate-on.
        private bool _deactivating;
        private DateTime _deactivatingAt;

        // Kick phase: counting remaining keepalives after the first.
        // Pages 1/2/3/5: 3 total (kicksRemaining starts at 2 after first).
        // Page 4: 1 total (kicksRemaining starts at 0 after first).
        private int _kicksRemaining;
        private DateTime _nextKickAt;

        // Settle phase: waiting after all kicks + ParamDefs resend before sending values.
        private bool _settling;
        private DateTime _kickedAt;
        private TimeSpan _currentSettleDelay;

        private bool _page1TotalsSent;
        private DateTime _page1TotalsCheckUntil = DateTime.MinValue;
        private bool _page2CapacitySent;
        private int _page2LastSentMaxFuelInt = -1;
        private DateTime _page2CapacityCheckUntil = DateTime.MinValue;
        private DateTime _lastFastUpdate = DateTime.MinValue;
        private DateTime _lastSlowUpdate = DateTime.MinValue;
        private DateTime _lastAutoEvalAt = DateTime.MinValue;

        private int _autoLastLap = int.MinValue;
        private DateTime _autoLapChangedAt = DateTime.MinValue;
        private float _autoBrakeBias = float.MinValue;
        private int _autoTcLevel = int.MinValue;
        private int _autoAbsLevel = int.MinValue;
        private DateTime _autoPage3ChangedAt = DateTime.MinValue;
        private DateTime _autoPitCycleStart = DateTime.MinValue;

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
        private int _lastDrsZone = int.MinValue;
        private int _lastDrsActive = int.MinValue;
        private int _lastTcLevel = int.MinValue;
        private int _lastAbsLevel = int.MinValue;
        private int _lastOilTemp = int.MinValue;
        private int _lastBrakeBias = int.MinValue;
        private int _lastTyreFl = int.MinValue;
        private int _lastTyreRl = int.MinValue;
        private int _lastTyreFr = int.MinValue;
        private int _lastTyreRr = int.MinValue;

        public FanatecItmDriver(IDeviceTransport transport, byte deviceId = ItmDeviceId.Bme)
        {
            _itm = new ItmDisplayController(transport);
            _deviceId = deviceId;
        }

        public void Update(GameData data)
        {
            if (data.NewData == null || !_itm.IsConnected) return;

            var now = DateTime.UtcNow;

            if (!_connected)
            {
                Connect(data, now);
                return;
            }

            // Waiting for handle table to clear after activate-off.
            if (_deactivating)
            {
                if (now - _deactivatingAt >= DeactivateDelay)
                {
                    _deactivating = false;
                    CompleteConnect(data, now);
                }
                return;
            }

            // Sending remaining keepalives in the kick sequence.
            if (_kicksRemaining > 0)
            {
                if (now >= _nextKickAt)
                {
                    _itm.SendKeepalive();
                    _kicksRemaining--;

                    if (_kicksRemaining == 0)
                    {
                        // Final kick — resend ParamDefs then start settle.
                        SendParamDefsForPage(data, now);
                        _kickedAt = now;
                        _settling = true;
                    }
                    else
                    {
                        _nextKickAt = now + KickInterval;
                    }
                }
                return;
            }

            if (_settling)
            {
                if (now - _kickedAt < _currentSettleDelay)
                    return;

                _settling = false;
                _lastFastUpdate = DateTime.MinValue;
                _lastSlowUpdate = DateTime.MinValue;
            }

            if (_configuredPage == ItmPageAuto.Page)
            {
                if (now - _lastAutoEvalAt >= AutoEvalInterval)
                {
                    SwitchActivePage(ComputeAutoPage(data, now), data);
                    _lastAutoEvalAt = now;
                }
            }
            else if (_configuredPage != _page)
            {
                SwitchActivePage(_configuredPage, data);
            }

            if (_settling || _deactivating || _kicksRemaining > 0) return;

            ResendPage1ParamDefsIfTotalsNowKnown(data);
            ResendPage2ParamDefsIfCapacityNowKnown(data);

            if (now - _lastFastUpdate >= FastValueUpdateInterval)
            {
                SendFastValueUpdates(data);
                _lastFastUpdate = now;
            }

            if (now - _lastSlowUpdate >= SlowValueUpdateInterval)
            {
                SendSlowValueUpdates(data);
                _lastSlowUpdate = now;
            }
        }

        public void SetPage(int page)
        {
            _configuredPage = page == ItmPageAuto.Page || page == ItmPage1.Page
                || page == ItmPage2.Page || page == ItmPage3.Page
                || page == ItmPage4.Page || page == ItmPage5.Page
                ? (byte)page
                : ItmPage1.Page;
        }

        public void SetAutoPageSettings(ItmAutoPageSettings settings)
        {
            _autoSettings = settings ?? new ItmAutoPageSettings();
        }

        /// <summary>
        /// Begins the connect sequence: sends activate-off to clear the
        /// firmware handle table, then waits DeactivateDelay before activating.
        /// </summary>
        private void Connect(GameData data, DateTime now)
        {
            _page = _configuredPage == ItmPageAuto.Page
                ? ComputeAutoPage(data, now)
                : _configuredPage;

            _itm.SendItmActivate(false);
            _connected = true;
            _deactivating = true;
            _deactivatingAt = now;

            SimHub.Logging.Current.Info("FanatecItmDriver: connecting on page " + _page + " (deviceId=" + _deviceId + ")");
        }

        /// <summary>
        /// Completes the connect sequence after the deactivate delay: sends
        /// activate-on, PageSet, ParamDefs, then starts the kick sequence.
        /// </summary>
        private void CompleteConnect(GameData data, DateTime now)
        {
            _itm.SendItmActivate();
            _activated = true;
            _itm.SendPageSet(_deviceId, _page);
            SendParamDefsForPage(data, now);
            ResetValueTrackers();
            StartKick(now);

            SimHub.Logging.Current.Info("FanatecItmDriver: activated page " + _page);
        }

        /// <summary>
        /// Switches to a new page using the same activate-off → delay →
        /// activate-on cycle as initial connect, to clear the handle table.
        /// </summary>
        private void SwitchActivePage(byte newPage, GameData data)
        {
            if (newPage == _page && !_deactivating && !_settling && _kicksRemaining == 0) return;

            _page = newPage;
            _itm.SendItmActivate(false);
            _activated = false;
            _deactivating = true;
            _deactivatingAt = DateTime.UtcNow;
            _settling = false;
            _kicksRemaining = 0;

            SimHub.Logging.Current.Info("FanatecItmDriver: switching to page " + _page);
        }

        /// <summary>
        /// Sends the first keepalive and sets up the remainder of the kick
        /// sequence. Page 4 uses a single kick + 1000ms settle; all other
        /// pages use three kicks at 100ms intervals + ParamDefs resend + 500ms settle.
        /// </summary>
        private void StartKick(DateTime now)
        {
            _itm.SendKeepalive();

            if (_page == ItmPage4.Page)
            {
                _kicksRemaining = 0;
                _currentSettleDelay = KickSettleDelayPage4;
                _kickedAt = now;
                _settling = true;
            }
            else
            {
                _kicksRemaining = 2;
                _nextKickAt = now + KickInterval;
                _currentSettleDelay = KickSettleDelayNormal;
                _settling = false;
            }

            _lastAutoEvalAt = now;
        }

        private void SendParamDefsForPage(GameData data, DateTime now)
        {
            if (_page == ItmPage1.Page)
            {
                _itm.SendParamDefs(BuildPage1ParamDefs(data));
                _page1TotalsSent = data.NewData.TotalLaps > 0;
                _page1TotalsCheckUntil = now + TimeSpan.FromSeconds(10);
            }
            else if (_page == ItmPage2.Page)
            {
                int maxFuelInt = ComputeMaxFuelInt(data);
                _itm.SendParamDefs(BuildPage2ParamDefs(maxFuelInt));
                _page2LastSentMaxFuelInt = maxFuelInt;
                _page2CapacitySent = false;
                _page2CapacityCheckUntil = now + TimeSpan.FromSeconds(10);
            }
            else if (_page == ItmPage3.Page)
            {
                _itm.SendParamDefs(BuildPage3ParamDefs());
            }
            else if (_page == ItmPage4.Page)
            {
                _itm.SendParamDefs(BuildPage4ParamDefs());
            }
            else if (_page == ItmPage5.Page)
            {
                _itm.SendParamDefs(BuildPage5ParamDefs());
            }
        }

        private byte ComputeAutoPage(GameData data, DateTime now)
        {
            var s = _autoSettings;

            // Session type override — bypasses all rules if set to a specific page.
            string sessionType = data.NewData.SessionTypeName ?? "";
            int sessionOverride = GetSessionOverride(sessionType, s);
            if (sessionOverride > 0) return (byte)sessionOverride;

            // Track lap changes.
            int lap = data.NewData.CurrentLap;
            if (_autoLastLap == int.MinValue)
                _autoLastLap = lap;
            else if (lap != _autoLastLap)
            {
                _autoLastLap = lap;
                _autoLapChangedAt = now;
            }

            // Rule 1: Brake bias, TC, or ABS changed → page 3.
            if (s.Page3OnControlChange)
            {
                float brakeBias = (float)data.NewData.BrakeBias;
                int tcLevel = (int)data.NewData.TCLevel;
                int absLevel = (int)data.NewData.ABSLevel;
                if (_autoBrakeBias == float.MinValue)
                {
                    _autoBrakeBias = brakeBias;
                    _autoTcLevel = tcLevel;
                    _autoAbsLevel = absLevel;
                }
                else if (brakeBias != _autoBrakeBias || tcLevel != _autoTcLevel || absLevel != _autoAbsLevel)
                {
                    _autoBrakeBias = brakeBias;
                    _autoTcLevel = tcLevel;
                    _autoAbsLevel = absLevel;
                    _autoPage3ChangedAt = now;
                }
                if (_autoPage3ChangedAt != DateTime.MinValue &&
                    now - _autoPage3ChangedAt < TimeSpan.FromSeconds(s.Page3ChangedDurationSeconds))
                    return ItmPage3.Page;
            }

            // Rule 2: In pit box.
            if (s.PitRuleEnabled && data.NewData.IsInPit > 0)
            {
                if (_autoPitCycleStart == DateTime.MinValue)
                    _autoPitCycleStart = now;

                if (s.PitDisplayMode == PitDisplayMode.Tyres) return ItmPage5.Page;
                if (s.PitDisplayMode == PitDisplayMode.Fuel)  return ItmPage2.Page;

                double cycleSeconds = (now - _autoPitCycleStart).TotalSeconds;
                double cyclePeriod = s.PitCycleIntervalSeconds * 2;
                return cycleSeconds % cyclePeriod < s.PitCycleIntervalSeconds
                    ? ItmPage5.Page
                    : ItmPage2.Page;
            }
            _autoPitCycleStart = DateTime.MinValue;

            // Rule 3: Post-lap sequence.
            if (s.PostLapRuleEnabled && _autoLapChangedAt != DateTime.MinValue)
            {
                var sinceLastLap = now - _autoLapChangedAt;
                var durationA = TimeSpan.FromSeconds(s.PostLapPageADurationSeconds);
                var durationB = TimeSpan.FromSeconds(s.PostLapPageBDurationSeconds);
                if (sinceLastLap < durationA)
                    return (byte)s.PostLapPageA;
                if (s.PostLapPageB > 0 && sinceLastLap < durationA + durationB)
                    return (byte)s.PostLapPageB;
            }

            // Rule 4: Car near → page 4.
            if (s.CarProximityRuleEnabled)
            {
                double carNearThreshold = _page == ItmPage4.Page ? AutoCarNearExitSeconds : AutoCarNearEnterSeconds;
                double? aheadGap = data.NewData.OpponentsAheadOnTrack?.FirstOrDefault()?.GaptoPlayer;
                double? behindGap = data.NewData.OpponentsBehindOnTrack?.FirstOrDefault()?.GaptoPlayer;
                bool carNear = (aheadGap.HasValue && Math.Abs(aheadGap.Value) < carNearThreshold)
                    || (behindGap.HasValue && Math.Abs(behindGap.Value) < carNearThreshold);
                if (carNear) return ItmPage4.Page;
            }

            // Rule 5: Low fuel.
            if (s.LowFuelRuleEnabled)
            {
                double lowFuelExit  = s.LowFuelThresholdLitres + 1.0;
                double lowFuelThreshold = _page == (byte)s.LowFuelPage ? lowFuelExit : s.LowFuelThresholdLitres;
                if (data.NewData.Fuel < lowFuelThreshold) return (byte)s.LowFuelPage;
            }

            return (byte)s.DefaultPage;
        }

        private static int GetSessionOverride(string sessionType, ItmAutoPageSettings s)
        {
            // Session type strings are not yet verified across all SimHub-supported games.
            // These match the most common normalised values — adjust if needed.
            switch (sessionType.ToLowerInvariant())
            {
                case "race":     return s.SessionRace;
                case "qualify":
                case "qualifying": return s.SessionQualify;
                case "practice": return s.SessionPractice;
                case "hotlap":   return s.SessionHotlap;
                case "drift":    return s.SessionDrift;
                default:         return 0;
            }
        }

        public void Clear()
        {
            _activated = false;
            _connected = false;
            _deactivating = false;
            _settling = false;
            _kicksRemaining = 0;
            _kickedAt = DateTime.MinValue;
            _deactivatingAt = DateTime.MinValue;
            _page = ItmPage1.Page;
            ResetValueTrackers();
            _autoLastLap = int.MinValue;
            _autoLapChangedAt = DateTime.MinValue;
            _autoBrakeBias = float.MinValue;
            _autoTcLevel = int.MinValue;
            _autoAbsLevel = int.MinValue;
            _autoPage3ChangedAt = DateTime.MinValue;
            _autoPitCycleStart = DateTime.MinValue;
            _lastAutoEvalAt = DateTime.MinValue;
            _page1TotalsSent = false;
            _page1TotalsCheckUntil = DateTime.MinValue;
            _page2CapacitySent = false;
            _page2CapacityCheckUntil = DateTime.MinValue;
        }

        public void Deactivate()
        {
            if (!_itm.IsConnected) return;
            if (_activated || (_connected && !_deactivating))
            {
                SendZeroesForCurrentPage();
                System.Threading.Thread.Sleep(50);
                _itm.SendItmActivate(false);
            }

            _activated = false;
            _connected = false;
            _deactivating = false;
            _settling = false;
            _kicksRemaining = 0;
            _kickedAt = DateTime.MinValue;
        }

        /// <summary>
        /// Sends zero values for all fields on the current page so the firmware
        /// cache is cleared before deactivation. Prevents stale values from a
        /// previous session persisting on the display at the start of the next.
        /// </summary>
        private void SendZeroesForCurrentPage()
        {
            var entries = new List<ItmDisplayController.ValueUpdateEntry>
            {
                new ItmDisplayController.ValueUpdateEntry(
                    ItmPage1.HandleSpeed, ItmParameterId.Speed, BitConverter.GetBytes((short)0)),
                new ItmDisplayController.ValueUpdateEntry(
                    ItmPage1.HandleGear, ItmParameterId.Gear, new byte[] { 0 }),
            };

            if (_page == ItmPage1.Page)
            {
                entries.Add(new ItmDisplayController.ValueUpdateEntry(
                    ItmPage1.HandleLap, ItmParameterId.Lap, new byte[] { 0 }));
                entries.Add(new ItmDisplayController.ValueUpdateEntry(
                    ItmPage1.HandlePosition, ItmParameterId.Position, new byte[] { 0 }));
                entries.Add(new ItmDisplayController.ValueUpdateEntry(
                    ItmPage1.HandleLapTime, ItmParameterId.LapTime, BitConverter.GetBytes(0f)));
                entries.Add(new ItmDisplayController.ValueUpdateEntry(
                    ItmPage1.HandleLastLapTime, ItmParameterId.LastLapTime, BitConverter.GetBytes(0f)));
            }
            else if (_page == ItmPage2.Page)
            {
                entries.Add(new ItmDisplayController.ValueUpdateEntry(
                    ItmPage2.HandleFuel, ItmParameterId.Fuel, BitConverter.GetBytes(0f)));
                entries.Add(new ItmDisplayController.ValueUpdateEntry(
                    ItmPage2.HandleErsLevel, ItmParameterId.ErsLevel, BitConverter.GetBytes(0)));
                entries.Add(new ItmDisplayController.ValueUpdateEntry(
                    ItmPage2.HandleDrsZone, ItmParameterId.DrsZone, new byte[] { 0 }));
                entries.Add(new ItmDisplayController.ValueUpdateEntry(
                    ItmPage2.HandleDrsActive, ItmParameterId.DrsActive, new byte[] { 0 }));
            }
            else if (_page == ItmPage3.Page)
            {
                entries.Add(new ItmDisplayController.ValueUpdateEntry(
                    ItmPage3.HandleTc, ItmParameterId.TcSetting, new byte[] { 0 }));
                entries.Add(new ItmDisplayController.ValueUpdateEntry(
                    ItmPage3.HandleAbs, ItmParameterId.AbsSetting, new byte[] { 0 }));
                entries.Add(new ItmDisplayController.ValueUpdateEntry(
                    ItmPage3.HandleOilTemp, ItmParameterId.OilTemp, new byte[] { 0 }));
                entries.Add(new ItmDisplayController.ValueUpdateEntry(
                    ItmPage3.HandleBrakeBias, ItmParameterId.BrakeBias, BitConverter.GetBytes((short)0)));
            }
            else if (_page == ItmPage4.Page)
            {
                entries.Add(new ItmDisplayController.ValueUpdateEntry(
                    ItmPage4.HandleLastLapTime, ItmParameterId.LastLapTime, BitConverter.GetBytes(0f)));
                entries.Add(new ItmDisplayController.ValueUpdateEntry(
                    ItmPage4.HandleBestLapTime, ItmParameterId.BestLapTime, BitConverter.GetBytes(0f)));
                entries.Add(new ItmDisplayController.ValueUpdateEntry(
                    ItmPage4.HandleCarAhead, ItmParameterId.CarAhead, BitConverter.GetBytes(0f)));
                entries.Add(new ItmDisplayController.ValueUpdateEntry(
                    ItmPage4.HandleCarBehind, ItmParameterId.CarBehind, BitConverter.GetBytes(0f)));
            }
            else if (_page == ItmPage5.Page)
            {
                entries.Add(new ItmDisplayController.ValueUpdateEntry(
                    ItmPage5.HandleFl, ItmParameterId.TyreFlTemp, new byte[] { 0 }));
                entries.Add(new ItmDisplayController.ValueUpdateEntry(
                    ItmPage5.HandleRl, ItmParameterId.TyreRlTemp, new byte[] { 0 }));
                entries.Add(new ItmDisplayController.ValueUpdateEntry(
                    ItmPage5.HandleFr, ItmParameterId.TyreFrTemp, new byte[] { 0 }));
                entries.Add(new ItmDisplayController.ValueUpdateEntry(
                    ItmPage5.HandleRr, ItmParameterId.TyreRrTemp, new byte[] { 0 }));
            }

            _itm.SendValueUpdate(entries);
        }

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
            _lastDrsZone = int.MinValue;
            _lastDrsActive = int.MinValue;
            _lastTcLevel = int.MinValue;
            _lastAbsLevel = int.MinValue;
            _lastOilTemp = int.MinValue;
            _lastBrakeBias = int.MinValue;
            _lastTyreFl = int.MinValue;
            _lastTyreRl = int.MinValue;
            _lastTyreFr = int.MinValue;
            _lastTyreRr = int.MinValue;
        }

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

        private static IReadOnlyList<ItmDisplayController.ParamDefEntry> BuildPage2ParamDefs(int maxFuelInt)
        {
            return new[]
            {
                new ItmDisplayController.ParamDefEntry(ItmPage2.Slot, ItmPage2.PositionFuel, SuffixFor(maxFuelInt)),
                new ItmDisplayController.ParamDefEntry(ItmPage2.Slot, ItmPage2.PositionErsLevel),
                new ItmDisplayController.ParamDefEntry(ItmPage2.Slot, ItmPage2.PositionDrsZone),
                new ItmDisplayController.ParamDefEntry(ItmPage2.Slot, ItmPage2.PositionDrsActive),
            };
        }

        private static IReadOnlyList<ItmDisplayController.ParamDefEntry> BuildPage3ParamDefs()
        {
            return new[]
            {
                new ItmDisplayController.ParamDefEntry(ItmPage3.Slot, ItmPage3.PositionTc),
                new ItmDisplayController.ParamDefEntry(ItmPage3.Slot, ItmPage3.PositionAbs),
                // Placeholder at position 2 — required to push OilTemp to handle 5.
                new ItmDisplayController.ParamDefEntry(ItmPage3.Slot, 2),
                new ItmDisplayController.ParamDefEntry(ItmPage3.Slot, ItmPage3.PositionOilTemp),
                new ItmDisplayController.ParamDefEntry(ItmPage3.SlotBrakeBias, 0),
            };
        }

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

        private static IReadOnlyList<ItmDisplayController.ParamDefEntry> BuildPage5ParamDefs()
        {
            return new[]
            {
                new ItmDisplayController.ParamDefEntry(ItmPage5.SlotFl),
                new ItmDisplayController.ParamDefEntry(ItmPage5.SlotRl),
                new ItmDisplayController.ParamDefEntry(ItmPage5.SlotFr),
                new ItmDisplayController.ParamDefEntry(ItmPage5.SlotRr),
            };
        }

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

        private static int ComputeMaxFuelInt(GameData data)
        {
            double maxFuel = data.NewData.MaxFuel;
            if (maxFuel <= 0) maxFuel = data.NewData.CarSettings_MaxFUEL;
            if (maxFuel <= 0 && data.NewData.FuelPercent > 0)
                maxFuel = data.NewData.Fuel / (data.NewData.FuelPercent / 100.0);

            return Clamp((int)Math.Round(maxFuel), 0, byte.MaxValue);
        }

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

        private void SendFastValueUpdates(GameData data)
        {
            var entries = new List<ItmDisplayController.ValueUpdateEntry>();

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
                // Reverse (−1) must be sent as i16 −1; all other gears fit in a single byte.
                byte[] gearBytes = gear == -1
                    ? BitConverter.GetBytes((short)-1)
                    : new[] { (byte)Clamp(gear, 0, 9) };
                entries.Add(new ItmDisplayController.ValueUpdateEntry(
                    ItmPage1.HandleGear, ItmParameterId.Gear, gearBytes));
                _lastGear = gear;
            }

            if (_page == ItmPage1.Page)
            {
                float lapTime = (float)data.NewData.CurrentLapTime.TotalSeconds;
                if (Math.Abs(lapTime - _lastLapTime) > float.Epsilon)
                {
                    entries.Add(new ItmDisplayController.ValueUpdateEntry(
                        ItmPage1.HandleLapTime, ItmParameterId.LapTime, BitConverter.GetBytes(lapTime)));
                    _lastLapTime = lapTime;
                }
            }

            if (_page == ItmPage4.Page)
                AddPage4FastValueUpdates(data, entries);

            SendIfAny(entries);
        }

        private void SendSlowValueUpdates(GameData data)
        {
            var entries = new List<ItmDisplayController.ValueUpdateEntry>();

            if (_page == ItmPage1.Page)
                AddPage1ValueUpdates(data, entries);
            else if (_page == ItmPage2.Page)
                AddPage2ValueUpdates(data, entries);
            else if (_page == ItmPage3.Page)
                AddPage3ValueUpdates(data, entries);
            else if (_page == ItmPage4.Page)
                AddPage4SlowValueUpdates(data, entries);
            else if (_page == ItmPage5.Page)
                AddPage5ValueUpdates(data, entries);

            SendIfAny(entries);
        }

        private void SendIfAny(List<ItmDisplayController.ValueUpdateEntry> entries)
        {
            if (entries.Count == 0) return;
            bool ok = _itm.SendValueUpdate(entries);
            SimHub.Logging.Current.Info(
                "FanatecItmDriver: ValueUpdate (" + entries.Count + " entries) ok=" + ok);
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

            // DRSAvailable: car is in a DRS detection zone (can activate).
            // DRSEnabled: DRS is currently active.
            int drsZone = data.NewData.DRSAvailable > 0 ? 1 : 0;
            if (drsZone != _lastDrsZone)
            {
                entries.Add(new ItmDisplayController.ValueUpdateEntry(
                    ItmPage2.HandleDrsZone, ItmParameterId.DrsZone, new[] { (byte)drsZone }));
                _lastDrsZone = drsZone;
            }

            int drsActive = data.NewData.DRSEnabled > 0 ? 1 : 0;
            if (drsActive != _lastDrsActive)
            {
                entries.Add(new ItmDisplayController.ValueUpdateEntry(
                    ItmPage2.HandleDrsActive, ItmParameterId.DrsActive, new[] { (byte)drsActive }));
                _lastDrsActive = drsActive;
            }
        }

        private void AddPage3ValueUpdates(GameData data, List<ItmDisplayController.ValueUpdateEntry> entries)
        {
            int tc = Clamp((int)data.NewData.TCLevel, 0, byte.MaxValue);
            if (tc != _lastTcLevel)
            {
                entries.Add(new ItmDisplayController.ValueUpdateEntry(
                    ItmPage3.HandleTc, ItmParameterId.TcSetting, new[] { (byte)tc }));
                _lastTcLevel = tc;
            }

            int abs = Clamp((int)data.NewData.ABSLevel, 0, byte.MaxValue);
            if (abs != _lastAbsLevel)
            {
                entries.Add(new ItmDisplayController.ValueUpdateEntry(
                    ItmPage3.HandleAbs, ItmParameterId.AbsSetting, new[] { (byte)abs }));
                _lastAbsLevel = abs;
            }

            int oilTemp = Clamp((int)Math.Round(data.NewData.OilTemperature), 0, byte.MaxValue);
            if (oilTemp != _lastOilTemp)
            {
                entries.Add(new ItmDisplayController.ValueUpdateEntry(
                    ItmPage3.HandleOilTemp, ItmParameterId.OilTemp, new[] { (byte)oilTemp }));
                _lastOilTemp = oilTemp;
            }

            // BrakeBias is sent as i16 ×10: 54.3% → send 543. Cap at safe max.
            float brakeBiasRaw = (float)data.NewData.BrakeBias;
            float brakeBiasClamped = Math.Min(Math.Max(brakeBiasRaw, 0f), ItmPage3.BrakeBiasMaxSafe);
            int brakeBiasInt = (int)Math.Round(brakeBiasClamped * 10.0f);
            if (brakeBiasInt != _lastBrakeBias)
            {
                entries.Add(new ItmDisplayController.ValueUpdateEntry(
                    ItmPage3.HandleBrakeBias, ItmParameterId.BrakeBias,
                    BitConverter.GetBytes((short)brakeBiasInt)));
                _lastBrakeBias = brakeBiasInt;
            }
        }

        private void AddPage4FastValueUpdates(GameData data, List<ItmDisplayController.ValueUpdateEntry> entries)
        {
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

        private void AddPage4SlowValueUpdates(GameData data, List<ItmDisplayController.ValueUpdateEntry> entries)
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
        }

        private void AddPage5ValueUpdates(GameData data, List<ItmDisplayController.ValueUpdateEntry> entries)
        {
            int fl = Clamp((int)Math.Round(data.NewData.TyreTemperatureFrontLeft), 0, byte.MaxValue);
            if (fl != _lastTyreFl)
            {
                entries.Add(new ItmDisplayController.ValueUpdateEntry(
                    ItmPage5.HandleFl, ItmParameterId.TyreFlTemp, new[] { (byte)fl }));
                _lastTyreFl = fl;
            }

            int rl = Clamp((int)Math.Round(data.NewData.TyreTemperatureRearLeft), 0, byte.MaxValue);
            if (rl != _lastTyreRl)
            {
                entries.Add(new ItmDisplayController.ValueUpdateEntry(
                    ItmPage5.HandleRl, ItmParameterId.TyreRlTemp, new[] { (byte)rl }));
                _lastTyreRl = rl;
            }

            int fr = Clamp((int)Math.Round(data.NewData.TyreTemperatureFrontRight), 0, byte.MaxValue);
            if (fr != _lastTyreFr)
            {
                entries.Add(new ItmDisplayController.ValueUpdateEntry(
                    ItmPage5.HandleFr, ItmParameterId.TyreFrTemp, new[] { (byte)fr }));
                _lastTyreFr = fr;
            }

            int rr = Clamp((int)Math.Round(data.NewData.TyreTemperatureRearRight), 0, byte.MaxValue);
            if (rr != _lastTyreRr)
            {
                entries.Add(new ItmDisplayController.ValueUpdateEntry(
                    ItmPage5.HandleRr, ItmParameterId.TyreRrTemp, new[] { (byte)rr }));
                _lastTyreRr = rr;
            }
        }

        private static byte[] SuffixFor(int total)
        {
            return total > 0 ? Encoding.ASCII.GetBytes("/" + total) : Array.Empty<byte>();
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }
}
