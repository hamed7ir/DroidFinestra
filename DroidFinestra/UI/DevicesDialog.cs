using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Windows.Forms;
using DroidFinestra.Core;
using DroidFinestra.Helpers;
using DroidFinestra.UI.Controls;

namespace DroidFinestra.UI
{
    /// <summary>
    /// §4.1 — what adb sees: "adb devices -l", parsed, one row per device with its state. In pick mode a click on
    /// a device returns it (<see cref="Picked"/>). adb runs on a worker thread; its own text is shown whenever it
    /// has something to say, so a failure reads as adb's message, not a generic error.
    /// </summary>
    public sealed class DevicesDialog : ThemedDialog
    {
        public AdbDevice Picked { get; private set; }

        private readonly bool _pickMode;
        private readonly NoteRow _status;
        private readonly OutputRow _output;
        private readonly RoundedButton _refresh, _restart;
        private bool _busy;

        public DevicesDialog(bool pickMode) : base(pickMode ? "Pick a device" : "Devices", 600, 520)
        {
            _pickMode = pickMode;
            _status = new NoteRow("", 2);
            _output = new OutputRow("adb's output", 150);

            _refresh = AddFooterButton("Refresh", RoundedButtonKind.Primary, DialogResult.None);
            _refresh.Click += (s, e) => Query(false);
            _restart = AddFooterButton("Restart adb", RoundedButtonKind.Neutral, DialogResult.None);
            _restart.Width = 140;
            _restart.Click += (s, e) => Query(true);
            AddFooterButton(pickMode ? "Cancel" : "Close", RoundedButtonKind.Neutral, DialogResult.Cancel);

            ShowDevices(new List<AdbDevice>(), "Asking adb for devices…", null);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Query(false);
        }

        private void Query(bool restart)
        {
            if (_busy) return;
            _busy = true;
            _refresh.Enabled = _restart.Enabled = false;
            ShowDevices(new List<AdbDevice>(), restart ? "Restarting the adb server… (this takes a few seconds on a Surface RT)" : "Asking adb for devices…", null);
            var t = new Thread(() =>
            {
                string prefix = "";
                if (restart)
                {
                    var rr = Adb.RestartServer();
                    prefix = rr.Describe() + Environment.NewLine;
                }
                var r = Adb.Devices();
                var list = r.StartError == null ? Adb.ParseDevices(r.Output) : new List<AdbDevice>();
                string status;
                if (r.StartError != null) status = r.StartError;
                else if (r.TimedOut) status = "adb did not answer in time. Try Restart adb.";
                else if (list.Count == 0) status = "adb lists no devices. For USB: cable in, USB debugging on. For Wi-Fi: pair and connect first.";
                else status = list.Count + " device" + (list.Count == 1 ? "" : "s") + (_pickMode ? " — click one to use it." : ".");
                string raw = prefix + "> adb devices -l" + Environment.NewLine + r.Describe();
                try
                {
                    if (!IsDisposed && IsHandleCreated)
                        BeginInvoke((Action)(() =>
                        {
                            _busy = false;
                            if (IsDisposed) return;
                            _refresh.Enabled = _restart.Enabled = true;
                            ShowDevices(list, status, raw);
                        }));
                }
                catch { }
            }) { IsBackground = true, Name = "adb devices" };
            t.Start();
        }

        private void ShowDevices(List<AdbDevice> devices, string status, string raw)
        {
            var old = new List<Control>();
            foreach (Control c in Body.Host.Controls) old.Add(c);
            Body.Host.Controls.Clear();
            foreach (var c in old) if (c is DeviceListRow || c is SectionHeader) c.Dispose();

            _status.Text2 = status;
            var rows = new List<Control> { new SectionHeader("Devices"), _status };
            foreach (var d in devices)
            {
                var row = new DeviceListRow(d, _pickMode);
                if (_pickMode) row.Click += (s, e) => OnPick(((DeviceListRow)s).Device);
                rows.Add(row);
            }
            _output.Value = raw ?? "";
            rows.Add(_output);
            PopulateBody(rows.ToArray());
            if (IsHandleCreated) StretchAndRestack();
        }

        private void OnPick(AdbDevice d)
        {
            if (d.State != "device" &&
                !ConfirmDialog.Ask(this, "adb reports this device as \"" + d.State + "\", not \"device\". Use it anyway?", "Pick a device", "Use it", "Cancel"))
                return;
            Picked = d;
            DialogResult = DialogResult.OK;
        }

        /// <summary>One device: model + transport + serial on the left, adb's state on the right.</summary>
        private sealed class DeviceListRow : SettingRow
        {
            public readonly AdbDevice Device;
            public DeviceListRow(AdbDevice d, bool clickable) : base(d.Label)
            {
                Device = d;
                if (clickable) Cursor = Cursors.Hand;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics; g.Clear(Bg); g.SmoothingMode = SmoothingMode.AntiAlias;
                PaintLeftLabel(g, 120);
                bool ok = Device.State == "device";
                var vr = new Rectangle(Width - 120, 0, 110, Height);
                using (var f = FontHelper.Ui(10.5f, ok ? FontStyle.Bold : FontStyle.Regular))
                    TextRenderer.DrawText(g, Device.State, f, vr, ok ? Accent : Color.FromArgb(200, 70, 60),
                        TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            }
        }
    }
}
