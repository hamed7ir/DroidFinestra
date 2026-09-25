using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using DroidFinestra.Core;
using DroidFinestra.Helpers;
using DroidFinestra.UI.Controls;

namespace DroidFinestra.UI
{
    /// <summary>
    /// Add/Edit a <see cref="DeviceProfile"/> — Finestra's ConnectionEditorForm pattern: private row fields,
    /// RebuildBody() re-stacking the rows when a choice changes the field set, editing on a COPY that becomes
    /// <see cref="Result"/> only on Save. The rows are the §2 list, grouped as scrcpy groups them.
    /// </summary>
    public sealed class DeviceEditorForm : ThemedDialog
    {
        private static readonly string[] TransportOptions = { "USB", "Wireless" };
        private static readonly string[] ModeOptions = { "New display (virtual)", "Mirror the phone screen" };

        public DeviceProfile Result { get; private set; }

        private readonly bool _isNew;
        private DeviceProfile _p;

        private readonly ChoiceRow _preset;
        private readonly TextRow _name;
        private readonly ChoiceRow _transport;
        private readonly TextRow _serial, _address;
        private readonly NoteRow _usbNote, _wifiNote;
        private readonly RoundedButton _pick, _pair;
        private readonly ChoiceRow _mode;
        private readonly TextRow _size, _maxSize, _maxFps, _bitRate;
        private readonly ToggleRow _audio;
        private readonly TextRow _audioBuffer;
        private readonly ToggleRow _fullscreen, _onTop;
        private readonly NoteRow _fullscreenNote;
        private readonly TextRow _title;
        private readonly ToggleRow _stayAwake, _screenOff;
        private readonly NoteRow _stayAwakeNote;
        private readonly TextRow _extra;
        private readonly ToggleRow _d3d;
        private readonly NoteRow _d3dNote;

        public DeviceEditorForm(DeviceProfile existing)
            : base(existing == null ? "New device profile" : "Edit device profile", 500, 640)
        {
            _isNew = existing == null;
            _p = existing != null ? existing.Copy() : DeviceProfile.FromPreset(0);

            _preset = new ChoiceRow("Start from", DeviceProfile.PresetNames, 0) { ValueWidth = 210 };
            _preset.Changed += () => { _p = DeviceProfile.FromPreset(_preset.SelectedIndex); LoadRows(); RebuildBody(); };

            _name = new TextRow("Name", "");

            _transport = new ChoiceRow("Connection", TransportOptions, 0) { ValueWidth = 140 };
            _transport.Changed += RebuildBody;
            _serial = new TextRow("Device serial (blank = the USB device)", "");
            _usbNote = new NoteRow("Leave the serial blank unless more than one phone is plugged in.");
            _address = new TextRow("Connect address — ip:port from the phone's Wireless debugging screen", "");
            _wifiNote = new NoteRow("There is no automatic discovery in this build: the address is always typed. A new phone must be paired once first.", 2);
            _pick = RowButton("Pick device…", RoundedButtonKind.Neutral);
            _pick.Click += (s, e) => PickDevice();
            _pair = RowButton("Pair a new phone…", RoundedButtonKind.Neutral);
            _pair.Click += (s, e) => PairPhone();

            _mode = new ChoiceRow("Video", ModeOptions, 0) { ValueWidth = 210 };
            _mode.Changed += RebuildBody;
            _size = new TextRow("Display size WxH (blank = the phone's own size)", "");
            _maxSize = new TextRow("Max size in pixels (0 = no limit)", "", numeric: true);
            _maxFps = new TextRow("Max frame rate (0 = no limit)", "", numeric: true);
            _bitRate = new TextRow("Video bit rate (e.g. 2M, 800K)", "");

            _audio = new ToggleRow("Audio", true);
            _audio.Changed += RebuildBody;
            _audioBuffer = new TextRow("Audio buffer in ms (0 = scrcpy's default, 50)", "", numeric: true);

            _fullscreen = new ToggleRow("Start fullscreen", false);
            _fullscreenNote = new NoteRow("In the scrcpy window, MOD+f toggles fullscreen (MOD = left Alt or left Windows key).");
            _onTop = new ToggleRow("Always on top", false);
            _title = new TextRow("Window title (blank = the phone's model)", "");

            _stayAwake = new ToggleRow("Stay awake (while plugged in)", false);
            _stayAwakeNote = new NoteRow("A virtual display goes black when the phone locks; staying awake prevents it.");
            _screenOff = new ToggleRow("Turn the phone's screen off", false);

            _extra = new TextRow("Extra arguments — appended verbatim", "");

            _d3d = new ToggleRow("Render with Direct3D 9 (--render-driver=direct3d)", true);
            _d3dNote = new NoteRow("Off = D3D11; on Tegra 3 the window is black.");
            _d3d.Changed += OnD3dChanged;

            LoadRows();
            RebuildBody();

            var save = AddFooterButton("Save", RoundedButtonKind.Primary, DialogResult.None);
            save.Click += OnSave;
            AddFooterButton("Cancel", RoundedButtonKind.Neutral, DialogResult.Cancel);
        }

        private static RoundedButton RowButton(string text, RoundedButtonKind kind)
            => new RoundedButton { Text = text, Kind = kind, Height = 40, Font = FontHelper.Ui(10f, FontStyle.Bold) };

        /// <summary>Profile → rows (initially, and when a preset is chosen).</summary>
        private void LoadRows()
        {
            _loading = true;
            try
            {
                _name.Value = _p.Name;
                _transport.SelectedIndex = (int)_p.Transport;
                _serial.Value = _p.Serial;
                _address.Value = _p.Address;
                _mode.SelectedIndex = (int)_p.VideoMode;
                _size.Value = _p.DisplaySize;
                _maxSize.Value = _p.MaxSize.ToString();
                _maxFps.Value = _p.MaxFps.ToString();
                _bitRate.Value = _p.VideoBitRate;
                _audio.On = _p.Audio;
                _audioBuffer.Value = _p.AudioBuffer.ToString();
                _fullscreen.On = _p.Fullscreen;
                _onTop.On = _p.AlwaysOnTop;
                _title.Value = _p.WindowTitle;
                _stayAwake.On = _p.StayAwake;
                _screenOff.On = _p.TurnScreenOff;
                _extra.Value = _p.ExtraArgs;
                _d3d.On = _p.RenderDirect3D;
            }
            finally { _loading = false; }
        }

        private bool _loading;

        /// <summary>§3: default ON and deliberately not a casual click. Turning it off asks first, with the consequence.</summary>
        private void OnD3dChanged()
        {
            if (_loading || _d3d.On) return;
            bool sure = ConfirmDialog.Ask(this,
                "Turn off Direct3D 9?\n\nOff = D3D11. On Tegra 3 (Surface RT) the scrcpy window is black. Only turn it off on hardware where D3D11 is known to work.",
                "Render driver", "Turn off", "Keep on");
            if (!sure) { _loading = true; try { _d3d.On = true; } finally { _loading = false; } }
        }

        private void RebuildBody()
        {
            var old = new List<Control>();
            foreach (Control c in Body.Host.Controls) old.Add(c);
            Body.Host.Controls.Clear();
            foreach (var c in old) if (c is SectionHeader) c.Dispose();   // headers are rebuilt; the field rows are reused
            var rows = new List<Control>();
            if (_isNew) rows.Add(_preset);
            rows.Add(_name);

            rows.Add(new SectionHeader("Connection"));
            rows.Add(_transport);
            if (_transport.SelectedIndex == (int)Transport.Wireless) rows.AddRange(new Control[] { _address, _wifiNote, _pair, _pick });
            else rows.AddRange(new Control[] { _serial, _usbNote, _pick });

            rows.Add(new SectionHeader("Video"));
            rows.Add(_mode);
            rows.Add(_mode.SelectedIndex == (int)VideoMode.NewDisplay ? _size : _maxSize);
            rows.AddRange(new Control[] { _maxFps, _bitRate });

            rows.Add(new SectionHeader("Audio"));
            rows.Add(_audio);
            if (_audio.On) rows.Add(_audioBuffer);

            rows.Add(new SectionHeader("Window"));
            rows.AddRange(new Control[] { _fullscreen, _fullscreenNote, _onTop, _title });

            rows.Add(new SectionHeader("Phone"));
            rows.AddRange(new Control[] { _stayAwake, _stayAwakeNote, _screenOff });

            rows.Add(new SectionHeader("Extra"));
            rows.Add(_extra);

            rows.Add(new SectionHeader("Advanced"));
            rows.AddRange(new Control[] { _d3d, _d3dNote });

            PopulateBody(rows.ToArray());
            if (IsHandleCreated) StretchAndRestack();
        }

        private void PickDevice()
        {
            using (var d = new DevicesDialog(pickMode: true))
            {
                if (d.ShowDialog(this) != DialogResult.OK || d.Picked == null) return;
                if (d.Picked.IsWireless)
                {
                    _transport.SelectedIndex = (int)Transport.Wireless;
                    _address.Value = d.Picked.Serial;
                }
                else
                {
                    _transport.SelectedIndex = (int)Transport.Usb;
                    _serial.Value = d.Picked.Serial;
                }
                if (string.IsNullOrWhiteSpace(_name.Value) && d.Picked.Model.Length > 0) _name.Value = d.Picked.Model.Replace('_', ' ');
                RebuildBody();
            }
        }

        private void PairPhone()
        {
            using (var d = new PairDialog())
            {
                if (d.ShowDialog(this) != DialogResult.OK || string.IsNullOrEmpty(d.ConnectAddress)) return;
                _transport.SelectedIndex = (int)Transport.Wireless;
                _address.Value = d.ConnectAddress;   // stored in the profile, so it is typed once
                RebuildBody();
            }
        }

        private static int ParseInt(string s) { int v; return int.TryParse((s ?? "").Trim(), out v) ? v : 0; }

        /// <summary>The digit-only KeyPress filter does not stop a paste; say so instead of saving 0.</summary>
        private static void CheckNumber(List<string> errs, TextRow row, string what)
        {
            string t = (row.Value ?? "").Trim();
            int v;
            if (t.Length > 0 && (!int.TryParse(t, out v) || v < 0)) errs.Add(what + " must be a whole number (it holds \"" + t + "\").");
        }

        private void OnSave(object sender, EventArgs e)
        {
            var p = _p.Copy();
            p.Name = (_name.Value ?? "").Trim();
            p.Transport = (Transport)_transport.SelectedIndex;
            p.Serial = (_serial.Value ?? "").Trim();
            p.Address = (_address.Value ?? "").Trim();
            if (p.Transport == Transport.Wireless)
            {
                string a = DeviceProfile.NormalizeAddress(p.Address);
                if (a != null) p.Address = a;
            }
            p.VideoMode = (VideoMode)_mode.SelectedIndex;
            p.DisplaySize = (_size.Value ?? "").Trim();
            p.MaxSize = ParseInt(_maxSize.Value);
            p.MaxFps = ParseInt(_maxFps.Value);
            p.VideoBitRate = (_bitRate.Value ?? "").Trim();
            p.Audio = _audio.On;
            p.AudioBuffer = ParseInt(_audioBuffer.Value);
            p.Fullscreen = _fullscreen.On;
            p.AlwaysOnTop = _onTop.On;
            p.WindowTitle = (_title.Value ?? "").Trim();
            p.StayAwake = _stayAwake.On;
            p.TurnScreenOff = _screenOff.On;
            p.ExtraArgs = (_extra.Value ?? "").Trim();
            p.RenderDirect3D = _d3d.On;

            var errs = p.Validate();
            if (p.Name.Length == 0) errs.Insert(0, "Give the profile a name.");
            if (p.VideoMode == VideoMode.Mirror) CheckNumber(errs, _maxSize, "Max size");
            CheckNumber(errs, _maxFps, "Max frame rate");
            if (p.Audio) CheckNumber(errs, _audioBuffer, "Audio buffer");
            if (errs.Count > 0)
            {
                ConfirmDialog.Info(this, string.Join("\n", errs.ToArray()), "Check the profile");
                return;
            }
            Result = p;
            DialogResult = DialogResult.OK;
        }
    }
}
