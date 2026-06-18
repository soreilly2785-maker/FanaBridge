using System;
using System.Windows;
using System.Windows.Controls;
using FanaBridge.Adapters;

namespace FanaBridge.UI
{
    public partial class ItmAutoSettingsPanel : UserControl
    {
        private ItmAutoPageSettings _settings;
        private bool _suppressEvents;

        public event Action SettingsChanged;

        private static readonly (string Content, int Tag)[] PageOptions = new[]
        {
            ("1 - Lap Info",    1),
            ("2 - Fuel / ERS",  2),
            ("3 - Car Settings",3),
            ("4 - Lap Times",   4),
            ("5 - Tyre Temps",  5),
        };

        private static readonly (string Content, int Tag)[] PageOptionsWithNone = new[]
        {
            ("None",            0),
            ("1 - Lap Info",    1),
            ("2 - Fuel / ERS",  2),
            ("3 - Car Settings",3),
            ("4 - Lap Times",   4),
            ("5 - Tyre Temps",  5),
        };

        private static readonly (string Content, int Tag)[] SessionPageOptions = new[]
        {
            ("Auto",            0),
            ("1 - Lap Info",    1),
            ("2 - Fuel / ERS",  2),
            ("3 - Car Settings",3),
            ("4 - Lap Times",   4),
            ("5 - Tyre Temps",  5),
        };

        public ItmAutoSettingsPanel()
        {
            InitializeComponent();
            PopulatePageCombo(cmbSessionRace,    SessionPageOptions, 0);
            PopulatePageCombo(cmbSessionQualify, SessionPageOptions, 0);
            PopulatePageCombo(cmbSessionPractice,SessionPageOptions, 0);
            PopulatePageCombo(cmbPostLapPageA,   PageOptions, 1);
            PopulatePageCombo(cmbPostLapPageB,   PageOptionsWithNone, 2);
            PopulatePageCombo(cmbLowFuelPage,    PageOptions, 2);
            PopulatePageCombo(cmbDefaultPage,    PageOptions, 1);
        }

        public void Bind(ItmAutoPageSettings settings)
        {
            _settings = settings ?? new ItmAutoPageSettings();
            _suppressEvents = true;

            SelectComboByTag(cmbSessionRace,     _settings.SessionRace);
            SelectComboByTag(cmbSessionQualify,  _settings.SessionQualify);
            SelectComboByTag(cmbSessionPractice, _settings.SessionPractice);

            chkPage3OnControlChange.IsChecked = _settings.Page3OnControlChange;
            txtPage3Duration.Text = _settings.Page3ChangedDurationSeconds.ToString("0.#");

            chkPitRuleEnabled.IsChecked = _settings.PitRuleEnabled;
            SelectComboByStringTag(cmbPitDisplayMode, _settings.PitDisplayMode.ToString());
            txtPitCycleInterval.Text = _settings.PitCycleIntervalSeconds.ToString("0.#");
            panelPitCycleInterval.Visibility = _settings.PitDisplayMode == PitDisplayMode.Both
                ? Visibility.Visible : Visibility.Collapsed;

            chkPostLapRuleEnabled.IsChecked = _settings.PostLapRuleEnabled;
            SelectComboByTag(cmbPostLapPageA, _settings.PostLapPageA);
            txtPostLapPageADuration.Text = _settings.PostLapPageADurationSeconds.ToString("0.#");
            SelectComboByTag(cmbPostLapPageB, _settings.PostLapPageB);
            txtPostLapPageBDuration.Text = _settings.PostLapPageBDurationSeconds.ToString("0.#");

            chkCarProximityEnabled.IsChecked = _settings.CarProximityRuleEnabled;
            txtCarProximityEnter.Text = _settings.CarProximityEnterSeconds.ToString("0.#");
            txtCarProximityExit.Text = _settings.CarProximityExitSeconds.ToString("0.#");

            chkLowFuelEnabled.IsChecked = _settings.LowFuelRuleEnabled;
            txtLowFuelThreshold.Text = _settings.LowFuelThresholdLitres.ToString("0.#");
            SelectComboByTag(cmbLowFuelPage, _settings.LowFuelPage);

            SelectComboByTag(cmbDefaultPage, _settings.DefaultPage);

            _suppressEvents = false;
        }

        // ── Session overrides ────────────────────────────────────────────────

        private void SessionOverride_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _settings == null) return;
            var cb = (ComboBox)sender;
            int page = GetSelectedIntTag(cb);
            switch ((string)cb.Tag)
            {
                case "Race":     _settings.SessionRace     = page; break;
                case "Qualify":  _settings.SessionQualify  = page; break;
                case "Practice": _settings.SessionPractice = page; break;
            }
            SettingsChanged?.Invoke();
        }

        // ── Rule 1 ───────────────────────────────────────────────────────────

        private void ChkPage3OnControlChange_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _settings == null) return;
            _settings.Page3OnControlChange = chkPage3OnControlChange.IsChecked == true;
            SettingsChanged?.Invoke();
        }

        private void TxtPage3Duration_Changed(object sender, TextChangedEventArgs e)
        {
            if (_suppressEvents || _settings == null) return;
            if (double.TryParse(txtPage3Duration.Text, out double v) && v > 0)
            {
                _settings.Page3ChangedDurationSeconds = v;
                SettingsChanged?.Invoke();
            }
        }

        // ── Rule 2 ───────────────────────────────────────────────────────────

        private void ChkPitRule_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _settings == null) return;
            _settings.PitRuleEnabled = chkPitRuleEnabled.IsChecked == true;
            SettingsChanged?.Invoke();
        }

        private void CmbPitDisplayMode_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _settings == null) return;
            var tag = GetSelectedStringTag(cmbPitDisplayMode);
            if (Enum.TryParse(tag, out PitDisplayMode mode))
            {
                _settings.PitDisplayMode = mode;
                panelPitCycleInterval.Visibility = mode == PitDisplayMode.Both
                    ? Visibility.Visible : Visibility.Collapsed;
                SettingsChanged?.Invoke();
            }
        }

        private void TxtPitCycleInterval_Changed(object sender, TextChangedEventArgs e)
        {
            if (_suppressEvents || _settings == null) return;
            if (double.TryParse(txtPitCycleInterval.Text, out double v) && v > 0)
            {
                _settings.PitCycleIntervalSeconds = v;
                SettingsChanged?.Invoke();
            }
        }

        // ── Rule 3 ───────────────────────────────────────────────────────────

        private void ChkPostLapRule_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _settings == null) return;
            _settings.PostLapRuleEnabled = chkPostLapRuleEnabled.IsChecked == true;
            SettingsChanged?.Invoke();
        }

        private void PostLap_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _settings == null) return;
            _settings.PostLapPageA = GetSelectedIntTag(cmbPostLapPageA);
            _settings.PostLapPageB = GetSelectedIntTag(cmbPostLapPageB);
            SettingsChanged?.Invoke();
        }

        private void TxtPostLapDuration_Changed(object sender, TextChangedEventArgs e)
        {
            if (_suppressEvents || _settings == null) return;
            if (double.TryParse(txtPostLapPageADuration.Text, out double a) && a > 0)
                _settings.PostLapPageADurationSeconds = a;
            if (double.TryParse(txtPostLapPageBDuration.Text, out double b) && b > 0)
                _settings.PostLapPageBDurationSeconds = b;
            SettingsChanged?.Invoke();
        }

        // ── Rule 4 ───────────────────────────────────────────────────────────

        private void ChkCarProximity_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _settings == null) return;
            _settings.CarProximityRuleEnabled = chkCarProximityEnabled.IsChecked == true;
            SettingsChanged?.Invoke();
        }

        private void TxtCarProximity_Changed(object sender, TextChangedEventArgs e)
        {
            if (_suppressEvents || _settings == null) return;
            if (double.TryParse(txtCarProximityEnter.Text, out double enter) && enter > 0)
                _settings.CarProximityEnterSeconds = enter;
            if (double.TryParse(txtCarProximityExit.Text, out double exit) && exit > 0)
                _settings.CarProximityExitSeconds = exit;
            SettingsChanged?.Invoke();
        }

        // ── Rule 5 ───────────────────────────────────────────────────────────

        private void ChkLowFuel_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _settings == null) return;
            _settings.LowFuelRuleEnabled = chkLowFuelEnabled.IsChecked == true;
            SettingsChanged?.Invoke();
        }

        private void TxtLowFuelThreshold_Changed(object sender, TextChangedEventArgs e)
        {
            if (_suppressEvents || _settings == null) return;
            if (double.TryParse(txtLowFuelThreshold.Text, out double v) && v >= 0)
            {
                _settings.LowFuelThresholdLitres = v;
                SettingsChanged?.Invoke();
            }
        }

        private void CmbLowFuelPage_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _settings == null) return;
            _settings.LowFuelPage = GetSelectedIntTag(cmbLowFuelPage);
            SettingsChanged?.Invoke();
        }

        // ── Default ──────────────────────────────────────────────────────────

        private void CmbDefaultPage_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _settings == null) return;
            _settings.DefaultPage = GetSelectedIntTag(cmbDefaultPage);
            SettingsChanged?.Invoke();
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private static void PopulatePageCombo(ComboBox cb, (string Content, int Tag)[] options, int defaultTag)
        {
            cb.Items.Clear();
            foreach (var (content, tag) in options)
                cb.Items.Add(new ComboBoxItem { Content = content, Tag = tag });
            SelectComboByTag(cb, defaultTag);
        }

        private static void SelectComboByTag(ComboBox cb, int tag)
        {
            foreach (ComboBoxItem item in cb.Items)
            {
                if ((int)item.Tag == tag)
                {
                    cb.SelectedItem = item;
                    return;
                }
            }
            if (cb.Items.Count > 0) cb.SelectedIndex = 0;
        }

        private static void SelectComboByStringTag(ComboBox cb, string tag)
        {
            foreach (ComboBoxItem item in cb.Items)
            {
                if ((string)item.Tag == tag)
                {
                    cb.SelectedItem = item;
                    return;
                }
            }
            if (cb.Items.Count > 0) cb.SelectedIndex = 0;
        }

        private static int GetSelectedIntTag(ComboBox cb)
        {
            return cb.SelectedItem is ComboBoxItem item ? (int)item.Tag : 0;
        }

        private static string GetSelectedStringTag(ComboBox cb)
        {
            return cb.SelectedItem is ComboBoxItem item ? (string)item.Tag : "";
        }
    }
}
