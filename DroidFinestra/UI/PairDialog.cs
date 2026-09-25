using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using DroidFinestra.Core;
using DroidFinestra.Helpers;
using DroidFinestra.UI.Controls;

namespace DroidFinestra.UI
{
    /// <summary>
    /// §4.2 — wireless pairing without the confusion: step 1 takes the PAIRING address and the 6-digit code and
    /// runs "adb pair"; step 2 takes the CONNECT address and runs "adb connect", then checks the state adb reports.
    /// adb's own words are shown at every step. On success <see cref="ConnectAddress"/> is the address to store.
    /// </summary>
    public sealed class PairDialog : ThemedDialog
    {
        public string ConnectAddress { get; private set; }

        private readonly NoteRow _intro, _step2Note;
        private readonly TextRow _pairAddr, _code, _connAddr;
        private readonly OutputRow _output;
        private readonly RoundedButton _primary, _skip;
        private bool _step2, _busy;

        public PairDialog() : base("Pair a phone over Wi-Fi", 520, 560)
        {
            _intro = new NoteRow("On the phone: Wireless debugging → Pair device with pairing code. The pairing port and the connect port are different. Keep the pairing box open on the phone until this finishes.", 3);
            _pairAddr = new TextRow("Pairing address — ip:port", "");
            _code = new TextRow("6-digit pairing code", "", numeric: true);
            _step2Note = new NoteRow("Now the connect address: the ip:port shown under \"IP address & port\" on the Wireless debugging screen.", 2);
            _connAddr = new TextRow("Connect address — ip:port", "");
            _output = new OutputRow("adb's output", 140);

            _primary = AddFooterButton("Pair", RoundedButtonKind.Primary, DialogResult.None);
            _primary.Click += (s, e) => { if (_step2) DoConnect(); else DoPair(); };
            _skip = AddFooterButton("Already paired", RoundedButtonKind.Neutral, DialogResult.None);
            _skip.Width = 170;
            _skip.Click += (s, e) => GoStep2();
            AddFooterButton("Cancel", RoundedButtonKind.Neutral, DialogResult.Cancel);

            Rebuild();
        }

        private void Rebuild()
        {
            var old = new List<Control>();
            foreach (Control c in Body.Host.Controls) old.Add(c);
            Body.Host.Controls.Clear();
            foreach (var c in old) if (c is SectionHeader) c.Dispose();

            var rows = new List<Control>();
            rows.Add(new SectionHeader(_step2 ? "Step 2 — connect" : "Step 1 — pair"));
            if (_step2) rows.AddRange(new Control[] { _step2Note, _connAddr });
            else rows.AddRange(new Control[] { _intro, _pairAddr, _code });
            rows.Add(_output);
            PopulateBody(rows.ToArray());
            if (IsHandleCreated) StretchAndRestack();
            _primary.Text = _step2 ? "Connect" : "Pair";
            _skip.Visible = !_step2;
        }

        private void GoStep2()
        {
            if (!_step2)
            {
                // prefill the host part: the connect address shares the IP, only the port differs
                string pa = (_pairAddr.Value ?? "").Replace(" ", "").Trim();
                int c = pa.LastIndexOf(':');
                if ((_connAddr.Value ?? "").Trim().Length == 0 && c > 0) _connAddr.Value = pa.Substring(0, c + 1);
            }
            _step2 = true;
            Rebuild();
        }

        private void DoPair()
        {
            string addr = (_pairAddr.Value ?? "").Replace(" ", "").Trim();
            string code = (_code.Value ?? "").Trim();
            if (addr.IndexOf(':') <= 0 || DeviceProfile.NormalizeAddress(addr) == null)
            { ConfirmDialog.Info(this, "Type the pairing address as ip:port, exactly as the phone shows it.", "Pair"); return; }
            if (code.Length != 6)
            { ConfirmDialog.Info(this, "The pairing code is the 6 digits shown on the phone.", "Pair"); return; }

            RunAdb("> adb pair " + addr + " ******", () => Adb.Pair(addr, code), r =>
            {
                if (Adb.PairSucceeded(r)) GoStep2();
            });
        }

        private void DoConnect()
        {
            string addr = DeviceProfile.NormalizeAddress(_connAddr.Value);
            if (addr == null)
            { ConfirmDialog.Info(this, "Type the connect address as ip:port, from the Wireless debugging screen.", "Connect"); return; }
            _connAddr.Value = addr;

            string state = null;
            RunAdb("> adb connect " + addr, () =>
            {
                var r = Adb.Connect(addr);
                if (Adb.ConnectSucceeded(r))
                {
                    state = Adb.StateOf(addr);
                    r.Output += "> adb devices: " + addr + " is " + (state ?? "not listed") + Environment.NewLine;
                }
                return r;
            }, r =>
            {
                if (!Adb.ConnectSucceeded(r)) return;
                if (state == "device")
                {
                    ConnectAddress = addr;
                    DialogResult = DialogResult.OK;
                }
                else if (state == "offline")
                    AppendOutput("The phone is 'offline' at this address. That is what the pairing port gives: use the port under \"IP address & port\" instead.");
                else if (state == "unauthorized")
                    AppendOutput("The phone has not authorized this computer yet. Check the phone for a prompt, then press Connect again.");
            });
        }

        private void RunAdb(string header, Func<ToolResult> work, Action<ToolResult> done)
        {
            if (_busy) return;
            _busy = true;
            _primary.Enabled = _skip.Enabled = false;
            _output.Value = header + Environment.NewLine + "…";
            var t = new Thread(() =>
            {
                ToolResult r;
                try { r = work(); }
                catch (Exception ex) { r = new ToolResult { StartError = ex.Message }; }
                try
                {
                    if (!IsDisposed && IsHandleCreated)
                        BeginInvoke((Action)(() =>
                        {
                            _busy = false;
                            if (IsDisposed) return;
                            _primary.Enabled = _skip.Enabled = true;
                            _output.Value = header + Environment.NewLine + r.Describe();
                            done(r);
                        }));
                }
                catch { }
            }) { IsBackground = true, Name = "adb pair/connect" };
            t.Start();
        }

        private void AppendOutput(string line) => _output.Value = _output.Value.TrimEnd() + Environment.NewLine + Environment.NewLine + line;
    }
}
