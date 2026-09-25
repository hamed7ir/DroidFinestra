using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace DroidFinestra.Core
{
    public enum Transport { Usb = 0, Wireless = 1 }

    /// <summary>NewDisplay = scrcpy's --new-display (a virtual display at a chosen size, the shipping mode);
    /// Mirror = the phone's own screen, optionally capped with --max-size.</summary>
    public enum VideoMode { NewDisplay = 0, Mirror = 1 }

    /// <summary>
    /// One saved device configuration — Finestra's ConnectionProfile shell (Guid Id, Name, JSON-round-trip
    /// Clone) with the RDP host fields replaced by the scrcpy options of BATCH-GUI-1 §2. Enums serialize as
    /// ints (Finestra's store uses Newtonsoft defaults); member 0 of each enum is the shipping default, so a
    /// field missing from the JSON means the measured configuration.
    ///
    /// Deliberately small: scrcpy 4.1 has over a hundred options; this is the §2 list plus a free-text box.
    /// </summary>
    public sealed class DeviceProfile
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "";

        // ── transport ──
        public Transport Transport { get; set; } = Transport.Usb;
        /// <summary>USB: an adb serial picked from the device list. Empty = "the USB device" (--select-usb).</summary>
        public string Serial { get; set; } = "";
        /// <summary>Wireless: the CONNECT address (ip:port) from the phone's Wireless debugging screen — not the
        /// pairing address, which is a different port.</summary>
        public string Address { get; set; } = "";

        // ── video ──
        public VideoMode VideoMode { get; set; } = VideoMode.NewDisplay;
        /// <summary>--new-display=WxH (NewDisplay mode). Empty = the phone's own size and density.</summary>
        public string DisplaySize { get; set; } = "1366x768";
        /// <summary>--max-size=N (Mirror mode). 0 = no limit.</summary>
        public int MaxSize { get; set; } = 0;
        /// <summary>--max-fps=N. 0 = no limit.</summary>
        public int MaxFps { get; set; } = 30;
        /// <summary>--video-bit-rate. Empty = scrcpy's default (8M).</summary>
        public string VideoBitRate { get; set; } = "2M";
        /// <summary>--video-codec. Part of the measured shipping line; not in the editor (§2), so it can only be
        /// changed through Extra arguments, where a later --video-codec wins.</summary>
        public string VideoCodec { get; set; } = "h264";

        // ── audio ──
        public bool Audio { get; set; } = true;
        /// <summary>--audio-buffer in ms. 0 = scrcpy's default (50).</summary>
        public int AudioBuffer { get; set; } = 0;

        // ── window ──
        public bool Fullscreen { get; set; }
        public bool AlwaysOnTop { get; set; }
        public string WindowTitle { get; set; } = "";

        // ── device ──
        public bool StayAwake { get; set; }
        public bool TurnScreenOff { get; set; }

        // ── advanced ──
        /// <summary>--render-driver=direct3d. ON by default and not casually switchable (§3): SDL3's default,
        /// direct3d11, compiles its YUV shader at ps_5_0; Tegra 3 is feature level 9_1 and the window is black.</summary>
        public bool RenderDirect3D { get; set; } = true;

        /// <summary>Appended to the command line verbatim.</summary>
        public string ExtraArgs { get; set; } = "";

        [JsonIgnore]
        public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Target : Name;

        /// <summary>One line for the list row: transport + target + the video mode.</summary>
        [JsonIgnore]
        public string Summary
        {
            get
            {
                string video = VideoMode == VideoMode.NewDisplay
                    ? "virtual display " + (string.IsNullOrWhiteSpace(DisplaySize) ? "(phone size)" : DisplaySize.Trim())
                    : "mirror" + (MaxSize > 0 ? " max " + MaxSize : "");
                return Target + "  ·  " + video + (MaxFps > 0 ? " @ " + MaxFps + " fps" : "")
                     + (string.IsNullOrWhiteSpace(VideoBitRate) ? "" : "  ·  " + VideoBitRate.Trim())
                     + (Audio ? "" : "  ·  no audio");
            }
        }

        [JsonIgnore]
        public string Target => Transport == Transport.Wireless
            ? "Wi-Fi " + (string.IsNullOrWhiteSpace(Address) ? "(no address)" : Address.Trim())
            : "USB" + (string.IsNullOrWhiteSpace(Serial) ? "" : " " + Serial.Trim());

        /// <summary>Deep copy with a fresh Id (Finestra's Clone: JSON round-trip, never MemberwiseClone).</summary>
        public DeviceProfile Clone()
        {
            var c = Copy();
            c.Id = Guid.NewGuid().ToString("N");
            return c;
        }

        /// <summary>Same data, same Id — the editor works on this, so Cancel discards cleanly.</summary>
        public DeviceProfile Copy()
        {
            var c = JsonConvert.DeserializeObject<DeviceProfile>(JsonConvert.SerializeObject(this));
            c.Normalize();
            return c;
        }

        /// <summary>Repairs nulls an older or hand-edited JSON may carry (Json.NET writes explicit nulls over
        /// initializers — Finestra's B18).</summary>
        public void Normalize()
        {
            if (string.IsNullOrEmpty(Id)) Id = Guid.NewGuid().ToString("N");
            Name = Name ?? ""; Serial = Serial ?? ""; Address = Address ?? "";
            DisplaySize = DisplaySize ?? ""; VideoBitRate = VideoBitRate ?? ""; VideoCodec = VideoCodec ?? "";
            WindowTitle = WindowTitle ?? ""; ExtraArgs = ExtraArgs ?? "";
            if (!Enum.IsDefined(typeof(Transport), Transport)) Transport = Transport.Usb;
            if (!Enum.IsDefined(typeof(VideoMode), VideoMode)) VideoMode = VideoMode.NewDisplay;
        }

        // ── presets (§3) — they encode measurements the user cannot redo ──────────────────────────────

        public static readonly string[] PresetNames = { "Surface RT — balanced", "Surface RT — light", "Wireless" };

        /// <summary>A new profile. Every preset starts from the measured shipping configuration
        /// (--new-display=1366x768 --video-codec=h264 --max-fps=30 --video-bit-rate=2M, direct3d on).</summary>
        public static DeviceProfile FromPreset(int preset)
        {
            var p = new DeviceProfile();   // = the shipping line
            switch (preset)
            {
                case 1:   // light: --max-size=800 --max-fps=24 --video-bit-rate=2M
                    p.Name = PresetNames[1];
                    p.VideoMode = VideoMode.Mirror;
                    p.MaxSize = 800;
                    p.MaxFps = 24;
                    p.VideoBitRate = "2M";
                    break;
                case 2:   // wireless: shipping line, 1M, audio off
                    p.Name = PresetNames[2];
                    p.Transport = Transport.Wireless;
                    p.VideoBitRate = "1M";
                    p.Audio = false;
                    break;
                default:
                    p.Name = PresetNames[0];
                    break;
            }
            return p;
        }

        // ── validation + the command line ─────────────────────────────────────────────────────────────

        private static readonly Regex SizeRx = new Regex(@"^\d{2,5}x\d{2,5}(/\d{2,4})?$");
        // scrcpy parses this with strtol(base 0) + one K/M suffix (util/str.c): no decimals, and a leading 0 means octal.
        private static readonly Regex RateRx = new Regex(@"^[1-9]\d*[KkMm]?$");

        /// <summary>Normalizes a typed address: drops spaces, appends :5555 to a bare IP (as the Wi-Fi launcher
        /// does). Returns null if it is not host[:port] with a valid port.</summary>
        public static string NormalizeAddress(string raw)
        {
            string a = (raw ?? "").Replace(" ", "").Trim();
            if (a.Length == 0) return null;
            int colon = a.LastIndexOf(':');
            if (colon < 0) return a + ":5555";
            int port;
            if (colon == 0 || !int.TryParse(a.Substring(colon + 1), out port) || port < 1 || port > 65535) return null;
            return a;
        }

        /// <summary>Human-readable problems, empty when the profile can be launched.</summary>
        public List<string> Validate()
        {
            var errs = new List<string>();
            if (Transport == Transport.Wireless && NormalizeAddress(Address) == null)
                errs.Add("Wireless needs the connect address as ip:port, from the phone's Wireless debugging screen.");
            if (VideoMode == VideoMode.NewDisplay && !string.IsNullOrWhiteSpace(DisplaySize) && !SizeRx.IsMatch(DisplaySize.Trim()))
                errs.Add("Display size must look like 1366x768 (optionally /dpi, e.g. 1366x768/240), or be empty.");
            if (!string.IsNullOrWhiteSpace(VideoBitRate) && !RateRx.IsMatch(VideoBitRate.Trim()))
                errs.Add("Video bit rate must be a whole number with an optional K or M suffix, e.g. 2M or 800K (no decimals).");
            if (MaxSize < 0 || MaxFps < 0 || AudioBuffer < 0) errs.Add("Numbers cannot be negative.");
            return errs;
        }

        /// <summary>The scrcpy arguments for this profile, in the order of the shipping line. Extra arguments are
        /// appended verbatim, after everything else, so they can override an earlier option.</summary>
        public string BuildArguments()
        {
            var t = new List<string>();
            if (Transport == Transport.Wireless)
            {
                string addr = NormalizeAddress(Address);
                if (addr != null) t.Add("--tcpip=" + addr);   // scrcpy runs "adb connect" itself, then uses it
            }
            else if (!string.IsNullOrWhiteSpace(Serial)) t.Add("--serial=" + Serial.Trim());
            else t.Add("--select-usb");

            if (VideoMode == VideoMode.NewDisplay)
                t.Add(string.IsNullOrWhiteSpace(DisplaySize) ? "--new-display" : "--new-display=" + DisplaySize.Trim());
            else if (MaxSize > 0)
                t.Add("--max-size=" + MaxSize);

            if (RenderDirect3D) t.Add("--render-driver=direct3d");
            if (!string.IsNullOrWhiteSpace(VideoCodec)) t.Add("--video-codec=" + VideoCodec.Trim());
            if (MaxFps > 0) t.Add("--max-fps=" + MaxFps);
            if (!string.IsNullOrWhiteSpace(VideoBitRate)) t.Add("--video-bit-rate=" + VideoBitRate.Trim());

            if (!Audio) t.Add("--no-audio");
            else if (AudioBuffer > 0) t.Add("--audio-buffer=" + AudioBuffer);

            if (Fullscreen) t.Add("--fullscreen");
            if (AlwaysOnTop) t.Add("--always-on-top");
            if (!string.IsNullOrWhiteSpace(WindowTitle)) t.Add("--window-title=" + WindowTitle.Trim());

            if (StayAwake) t.Add("--stay-awake");
            if (TurnScreenOff) t.Add("--turn-screen-off");

            string args = CommandLine.Join(t);
            string extra = (ExtraArgs ?? "").Trim();
            return extra.Length == 0 ? args : args + " " + extra;
        }
    }

    /// <summary>Windows argument quoting (CommandLineToArgvW rules) — Finestra's RdpLauncher.JoinArgs/QuoteArg.</summary>
    public static class CommandLine
    {
        public static string Join(IList<string> tokens)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < tokens.Count; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(Quote(tokens[i]));
            }
            return sb.ToString();
        }

        public static string Quote(string arg)
        {
            if (!string.IsNullOrEmpty(arg) && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return arg;
            var sb = new StringBuilder();
            sb.Append('"');
            int backslashes = 0;
            foreach (char c in arg ?? "")
            {
                if (c == '\\') { backslashes++; continue; }
                if (c == '"') { sb.Append('\\', backslashes * 2 + 1); sb.Append('"'); backslashes = 0; continue; }
                sb.Append('\\', backslashes); backslashes = 0; sb.Append(c);
            }
            sb.Append('\\', backslashes * 2);
            sb.Append('"');
            return sb.ToString();
        }
    }
}
