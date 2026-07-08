using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Windows.Forms;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace HapticsBridge
{
    /// <summary>
    /// System-tray bridge: receives 48 kHz stereo float haptic PCM from the
    /// Silksong BepInEx plugin over localhost TCP and plays it into the
    /// DualSense audio endpoint, mapped onto the controller's actuator channels.
    /// On first run it extracts the PS5 haptic clips from the user's own game
    /// files. Tray icon: gray = no DualSense audio device, blue = extracting,
    /// orange = ready/listening, green = game connected.
    /// </summary>
    internal static class Program
    {
        public const int SampleRate = 48000;

        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Utility modes run regardless of a tray instance.
            if (args.Contains("--list"))
            {
                using (var enumerator = new MMDeviceEnumerator())
                {
                    var lines = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                        .Select(d => string.Format("[{0}ch @ {1}Hz] {2}", d.AudioClient.MixFormat.Channels, d.AudioClient.MixFormat.SampleRate, d.FriendlyName));
                    MessageBox.Show(string.Join("\n", lines), "Active audio render devices");
                }
                return 0;
            }

            if (args.Contains("--extract-only"))
            {
                string outDir = GetArg(args, "--extract-only");
                string gameRoot = GameLocator.Locate(interactive: true);
                if (gameRoot == null) return 2;
                int n = ClipExtractor.ExtractAll(gameRoot, outDir, (done, total) => { });
                Engine.Log(string.Format("Extract-only: {0} clips -> {1}", n, outDir));
                return n > 0 ? 0 : 1;
            }

            bool isFirst;
            using (var mutex = new Mutex(true, "SilksongHapticsBridge", out isFirst))
            {
                if (!isFirst)
                    return 0;   // tray instance already running

                Application.Run(new TrayContext(args));
                return 0;
            }
        }

        public static string GetArg(string[] args, string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
    }

    internal sealed class TrayContext : ApplicationContext
    {
        private readonly NotifyIcon trayIcon;
        private readonly Engine engine;
        private readonly System.Windows.Forms.Timer poll;
        private readonly Icon iconGray = MakeIcon(Color.FromArgb(140, 140, 140));
        private readonly Icon iconBlue = MakeIcon(Color.FromArgb(80, 140, 235));
        private readonly Icon iconOrange = MakeIcon(Color.FromArgb(235, 160, 40));
        private readonly Icon iconGreen = MakeIcon(Color.FromArgb(70, 190, 90));
        private Engine.State lastState = (Engine.State)(-1);
        private string lastDetail;
        private readonly List<ToolStripMenuItem> presetItems = new List<ToolStripMenuItem>();

        public TrayContext(string[] args)
        {
            engine = new Engine(args);

            var menu = new ContextMenuStrip();
            menu.Items.Add("Play test chime", null, delegate { engine.RequestChime(); });
            menu.Items.Add(BuildLatencyMenu());
            menu.Items.Add("Open log", null, delegate
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Engine.LogPath) { UseShellExecute = true });
            });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate { ExitThread(); });

            trayIcon = new NotifyIcon
            {
                Icon = iconGray,
                Text = "Silksong Haptics: starting...",
                Visible = true,
                ContextMenuStrip = menu,
            };

            poll = new System.Windows.Forms.Timer { Interval = 400 };
            poll.Tick += delegate { UpdateTray(); };
            poll.Start();

            engine.Start();
        }

        /// <summary>
        /// "Latency" submenu: grayed help text explaining the trade-off, then
        /// one checkable item per preset. Default (Reliable) needs no touching;
        /// the guidance tells the user when they'd want to move off it.
        /// </summary>
        private ToolStripMenuItem BuildLatencyMenu()
        {
            var menu = new ToolStripMenuItem("Latency");

            string[] help =
            {
                "Leave on Reliable unless haptics lag or glitch.",
                "Higher = steadier (Bluetooth, busy PCs).",
                "Lower = snappier, but can crackle or cut out —",
                "especially on Bluetooth. Minimal is wired-only.",
            };
            foreach (var line in help)
                menu.DropDownItems.Add(new ToolStripMenuItem(line) { Enabled = false });
            menu.DropDownItems.Add(new ToolStripSeparator());

            foreach (var p in Engine.Presets)
            {
                string key = p.Key;
                var item = new ToolStripMenuItem(p.Label + " — " + p.Blurb);
                item.Tag = key;
                item.Click += delegate { engine.ApplyPreset(key); RefreshPresetChecks(); };
                presetItems.Add(item);
                menu.DropDownItems.Add(item);
            }
            menu.DropDownOpening += delegate { RefreshPresetChecks(); };
            RefreshPresetChecks();
            return menu;
        }

        private void RefreshPresetChecks()
        {
            string cur = engine.CurrentPresetKey;
            foreach (var it in presetItems)
                it.Checked = ((string)it.Tag == cur);
        }

        private void UpdateTray()
        {
            var state = engine.CurrentState;
            string detail = engine.StateDetail;
            if (state == lastState && detail == lastDetail) return;
            lastState = state;
            lastDetail = detail;
            Icon icon;
            string text;
            switch (state)
            {
                case Engine.State.GameConnected:
                    icon = iconGreen; text = "Silksong Haptics: game connected"; break;
                case Engine.State.Listening:
                    icon = iconOrange; text = "Silksong Haptics: ready, waiting for game"; break;
                case Engine.State.Extracting:
                    icon = iconBlue; text = "Silksong Haptics: extracting clips " + detail; break;
                default:
                    icon = iconGray; text = "Silksong Haptics: no DualSense audio device"; break;
            }
            trayIcon.Icon = icon;
            trayIcon.Text = text.Length > 63 ? text.Substring(0, 63) : text;
        }

        protected override void ExitThreadCore()
        {
            poll.Stop();
            engine.Stop();
            trayIcon.Visible = false;
            trayIcon.Dispose();
            base.ExitThreadCore();
        }

        private static Icon MakeIcon(Color color)
        {
            using (var bmp = new Bitmap(16, 16))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (var brush = new SolidBrush(color))
                        g.FillEllipse(brush, 2, 2, 12, 12);
                    using (var pen = new Pen(Color.FromArgb(60, 0, 0, 0)))
                        g.DrawEllipse(pen, 2, 2, 12, 12);
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }
    }

    internal sealed class Engine
    {
        public enum State { NoDevice, Extracting, Listening, GameConnected }

        public static readonly string LogPath =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "HapticsBridge.log");

        public State CurrentState { get { return state; } }
        public string StateDetail { get { return stateDetail; } }
        private volatile State state = State.NoDevice;
        private volatile string stateDetail = "";

        private readonly string deviceMatch;
        private readonly int port;
        private volatile int bufferMs;
        private volatile int latencyMs;
        private volatile bool eventSync;
        private readonly bool hidAssert;
        private readonly string map;
        private readonly bool chimeOnStart;
        private volatile bool stop;
        private volatile bool sessionDead;
        private volatile bool fastReopen;   // set when a preset change forces a reopen

        /// <summary>A named latency/reliability trade-off point (see the tray menu).</summary>
        public struct LatencyPreset
        {
            public readonly string Key, Label, Blurb;
            public readonly int LatencyMs, BufferMs;
            public readonly bool EventSync;
            public LatencyPreset(string key, string label, string blurb, int latencyMs, int bufferMs, bool eventSync)
            { Key = key; Label = label; Blurb = blurb; LatencyMs = latencyMs; BufferMs = bufferMs; EventSync = eventSync; }
        }

        // Ordered safest -> snappiest. "Reliable" matches the built-in defaults.
        public static readonly LatencyPreset[] Presets =
        {
            new LatencyPreset("reliable", "Reliable", "survives Bluetooth & busy PCs (default)", 100, 60, false),
            new LatencyPreset("snappy",   "Snappy",   "lower latency, best on wired USB",         40, 30, false),
            new LatencyPreset("minimal",  "Minimal",  "lowest latency, wired only (may glitch)",  20, 20, true),
        };

        private static readonly string SettingsPath =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "latency.cfg");
        private Thread thread;
        private TcpListener listener;
        private volatile BufferedWaveProvider chimeBuffer;
        private string openedDeviceId;

        // One lane per connected game instance (e.g. coop host + client on
        // one machine); their streams are mixed into the same controller.
        private sealed class Lane
        {
            public TcpClient Client;
            public BufferedWaveProvider Buffer;
            public ISampleProvider Input;
        }
        private readonly List<Lane> lanes = new List<Lane>();
        private int clientCount;

        public Engine(string[] args)
        {
            deviceMatch = Program.GetArg(args, "--device") ?? "DualSense";
            int p, b, l;
            port = int.TryParse(Program.GetArg(args, "--port"), out p) ? p : 48111;
            bufferMs = int.TryParse(Program.GetArg(args, "--buffer-ms"), out b) ? b : 60;
            // Timer/push-driven WASAPI at 100 ms by default. Event-sync at low
            // latency (the old 20 ms default) silently wedges on Bluetooth
            // DualSense endpoints: the endpoint stops delivering buffer-ready
            // events, the render client stops pulling, and haptics freeze.
            // Timer-driven doesn't depend on that callback, so it survives BT
            // jitter; on wired endpoints it's indistinguishable. Opt back into
            // event-sync with --event-sync and tune latency with --latency-ms.
            latencyMs = int.TryParse(Program.GetArg(args, "--latency-ms"), out l) ? l : 100;
            eventSync = args.Contains("--event-sync");
            // USB pads leave their audio path unpowered until told otherwise,
            // so ch3/4 audio drains but the actuators stay silent; the bridge
            // enables audio haptics over HID itself (see DualSenseHid) instead
            // of depending on DSX. Disable if another app should own the pad.
            hidAssert = !args.Contains("--no-hid");
            map = Program.GetArg(args, "--map") ?? "auto";
            chimeOnStart = !args.Contains("--no-chime");
            try { File.WriteAllText(LogPath, ""); } catch { }
            // A saved tray preset overrides the arg/default latency knobs.
            LoadPreset();
        }

        private void LoadPreset()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return;
                string key = File.ReadAllText(SettingsPath).Trim();
                foreach (var p in Presets)
                    if (p.Key == key)
                    {
                        latencyMs = p.LatencyMs; bufferMs = p.BufferMs; eventSync = p.EventSync;
                        return;
                    }
            }
            catch { }
        }

        /// <summary>The preset whose values match the live settings, or null if a CLI override put us off-grid.</summary>
        public string CurrentPresetKey
        {
            get
            {
                foreach (var p in Presets)
                    if (p.LatencyMs == latencyMs && p.BufferMs == bufferMs && p.EventSync == eventSync) return p.Key;
                return null;
            }
        }

        /// <summary>Switch preset, persist it, and reopen the session so it takes effect now.</summary>
        public void ApplyPreset(string key)
        {
            foreach (var p in Presets)
                if (p.Key == key)
                {
                    latencyMs = p.LatencyMs; bufferMs = p.BufferMs; eventSync = p.EventSync;
                    try { File.WriteAllText(SettingsPath, key); } catch { }
                    Log(string.Format("Latency preset: {0} ({1}-driven, {2} ms latency, {3} ms buffer)",
                        p.Label, eventSync ? "event" : "timer", latencyMs, bufferMs));
                    fastReopen = true;
                    KillSession("latency preset changed");
                    return;
                }
        }

        public void Start()
        {
            thread = new Thread(Run) { IsBackground = true, Name = "HapticsEngine" };
            thread.SetApartmentState(ApartmentState.STA);   // GameLocator may show a dialog
            thread.Start();
        }

        public void Stop()
        {
            stop = true;
            try { if (listener != null) listener.Stop(); } catch { }
        }

        public void RequestChime()
        {
            var cb = chimeBuffer;
            if (cb == null) return;
            new Thread(delegate () { try { Chime.Play(cb); } catch { } }) { IsBackground = true }.Start();
        }

        private void Run()
        {
            EnsureClips();

            bool firstChimePlayed = false;
            int reopenDelayMs = 2000;
            int sessionStartTick = Environment.TickCount;
            while (!stop)
            {
                var device = FindDevice();
                if (device == null)
                {
                    state = State.NoDevice;
                    SleepInterruptible(4000);
                    continue;
                }

                try
                {
                    var mix = device.AudioClient.MixFormat;
                    Log(string.Format("Device: {0} ({1} Hz, {2} ch, {3}-bit)", device.FriendlyName, mix.SampleRate, mix.Channels, mix.BitsPerSample));
                    if (mix.BitsPerSample != 32)
                    {
                        Log("Unsupported mix format (not 32-bit float); retrying in 10s");
                        state = State.NoDevice;
                        SleepInterruptible(10000);
                        continue;
                    }

                    int leftCh, rightCh;
                    if (map == "12" || (map == "auto" && mix.Channels < 4)) { leftCh = 0; rightCh = Math.Min(1, mix.Channels - 1); }
                    else { leftCh = 2; rightCh = 3; }
                    Log(string.Format("Routing: haptic L -> ch{0}, haptic R -> ch{1}", leftCh + 1, rightCh + 1));

                    var stereoFloat = WaveFormat.CreateIeeeFloatWaveFormat(Program.SampleRate, 2);
                    // Separate lane for the chime so it can play any time,
                    // including while games are streaming.
                    var chime = new BufferedWaveProvider(stereoFloat)
                    {
                        BufferDuration = TimeSpan.FromMilliseconds(500),
                        DiscardOnBufferOverflow = true,
                    };
                    var mixer = new MixingSampleProvider(new[] { chime.ToSampleProvider() })
                    {
                        ReadFully = true,
                    };
                    ISampleProvider source = mixer;
                    if (mix.SampleRate != Program.SampleRate)
                        source = new WdlResamplingSampleProvider(source, mix.SampleRate);
                    var provider = new ChannelMapWaveProvider(source, mix, leftCh, rightCh);

                    sessionDead = false;
                    sessionStartTick = Environment.TickCount;
                    openedDeviceId = device.ID;
                    chimeBuffer = chime;

                    Log(string.Format("Output: {0}-driven, {1} ms latency", eventSync ? "event" : "timer", latencyMs));
                    using (var output = new WasapiOut(device, AudioClientShareMode.Shared, eventSync, latencyMs))
                    {
                        output.PlaybackStopped += delegate (object s, StoppedEventArgs e)
                        {
                            if (e.Exception != null) Log("Playback stopped: " + e.Exception.Message);
                            KillSession("playback stopped");
                        };
                        output.Init(provider);
                        output.Play();

                        if (hidAssert) DualSenseHid.AssertAudioHaptics();

                        var watchdog = new Thread(delegate () { WatchdogLoop(output); })
                        { IsBackground = true, Name = "HapticsWatchdog" };
                        watchdog.Start();

                        if (chimeOnStart && !firstChimePlayed)
                        {
                            firstChimePlayed = true;
                            Chime.Play(chime);
                            Log("Startup chime played");
                        }

                        listener = new TcpListener(IPAddress.Loopback, port);
                        listener.Start();
                        Log("Listening on 127.0.0.1:" + port);
                        state = State.Listening;

                        while (!stop && !sessionDead)
                        {
                            while (!stop && !sessionDead && !listener.Pending())
                                Thread.Sleep(100);
                            if (stop || sessionDead) break;

                            var client = listener.AcceptTcpClient();
                            client.NoDelay = true;
                            var lane = new Lane
                            {
                                Client = client,
                                Buffer = new BufferedWaveProvider(stereoFloat)
                                {
                                    BufferDuration = TimeSpan.FromMilliseconds(Math.Max(bufferMs * 4, 250)),
                                    DiscardOnBufferOverflow = true,
                                },
                            };
                            lane.Input = lane.Buffer.ToSampleProvider();
                            lock (lanes) { lanes.Add(lane); }
                            mixer.AddMixerInput(lane.Input);
                            int n = Interlocked.Increment(ref clientCount);
                            Log("Game connected (" + n + " instance" + (n > 1 ? "s" : "") + ")");
                            state = State.GameConnected;

                            var clientThread = new Thread(delegate () { ClientLoop(lane, mixer); })
                            { IsBackground = true, Name = "HapticsClient" };
                            clientThread.Start();
                        }
                    }
                }
                catch (SocketException) { /* listener stopped for shutdown/device loss */ }
                catch (Exception e)
                {
                    Log(string.Format("Engine error: {0}: {1}", e.GetType().Name, e.Message));
                }
                finally
                {
                    sessionDead = true;   // stops the watchdog and client loops
                    chimeBuffer = null;
                    lock (lanes)
                    {
                        foreach (var lane in lanes)
                            try { lane.Client.Close(); } catch { }
                        lanes.Clear();
                    }
                    try { if (listener != null) listener.Stop(); } catch { }
                    listener = null;
                }
                if (!stop)
                {
                    state = State.NoDevice;
                    if (fastReopen)
                    {
                        // Intentional reopen (preset change) — don't treat it as
                        // a stall; come straight back with the new settings.
                        fastReopen = false;
                        reopenDelayMs = 2000;
                        SleepInterruptible(250);
                    }
                    else
                    {
                        // Back off if sessions are dying young (a wedged endpoint
                        // that re-stalls on every reopen); reset once one survives.
                        if (Environment.TickCount - sessionStartTick >= 60000)
                            reopenDelayMs = 2000;
                        else
                            reopenDelayMs = Math.Min(reopenDelayMs * 2, 30000);
                        SleepInterruptible(reopenDelayMs);
                    }
                }
            }
        }

        /// <summary>Reads one game instance's stream into its mixer lane until it disconnects.</summary>
        private void ClientLoop(Lane lane, MixingSampleProvider mixer)
        {
            var block = new byte[480 * 2 * 4];
            try
            {
                using (var stream = lane.Client.GetStream())
                {
                    int read;
                    while (!stop && !sessionDead && (read = ReadBlock(stream, block)) > 0)
                    {
                        if (lane.Buffer.BufferedDuration.TotalMilliseconds > bufferMs * 2)
                            lane.Buffer.ClearBuffer();
                        lane.Buffer.AddSamples(block, 0, read);
                    }
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            finally
            {
                try { mixer.RemoveMixerInput(lane.Input); } catch { }
                lock (lanes) { lanes.Remove(lane); }
                try { lane.Client.Close(); } catch { }
                int n = Interlocked.Decrement(ref clientCount);
                Log("Game disconnected (" + n + " remaining)");
                if (!sessionDead)
                    state = n > 0 ? State.GameConnected : State.Listening;
            }
        }

        /// <summary>
        /// Tear down the current audio session so the outer loop reopens the
        /// device. Unblocks the accept/read loops by closing their sockets.
        /// </summary>
        private void KillSession(string why)
        {
            if (sessionDead) return;
            sessionDead = true;
            Log("Reopening audio session: " + why);
            lock (lanes)
            {
                foreach (var lane in lanes)
                    try { lane.Client.Close(); } catch { }
            }
            try { if (listener != null) listener.Stop(); } catch { }
        }

        /// <summary>
        /// Detects endpoint replacement (DSX recreating its virtual device, a
        /// USB controller replug) and a frozen render clock. While the session
        /// is playing, the device's hardware position always advances — even
        /// through silence — so a position frozen across two ticks (~3s) means
        /// the endpoint genuinely stopped consuming audio (the Bluetooth stall)
        /// and the session must be reopened. Unlike the old buffered-bytes
        /// check, the device clock cannot misfire on a quiet producer.
        /// </summary>
        private void WatchdogLoop(WasapiOut output)
        {
            long lastPos = -1;
            int frozenTicks = 0;
            while (!stop && !sessionDead)
            {
                Thread.Sleep(1500);
                if (stop || sessionDead) return;
                try
                {
                    // Re-select audio-haptics mode every tick: the game/Steam
                    // can flip the pad back to rumble emulation at any time.
                    if (hidAssert) DualSenseHid.AssertAudioHaptics();

                    using (var enumerator = new MMDeviceEnumerator())
                    {
                        var live = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                            .FirstOrDefault(d => d.FriendlyName.IndexOf(deviceMatch, StringComparison.OrdinalIgnoreCase) >= 0);
                        if (live == null) { KillSession("device removed"); return; }
                        if (live.ID != openedDeviceId) { KillSession("device replaced (new endpoint instance)"); return; }
                    }

                    long pos = output.GetPosition();
                    if (pos == lastPos)
                    {
                        if (++frozenTicks >= 2)
                        {
                            KillSession("render clock frozen (endpoint stopped consuming audio)");
                            return;
                        }
                    }
                    else
                        frozenTicks = 0;
                    lastPos = pos;
                }
                catch (Exception e)
                {
                    KillSession("watchdog error: " + e.Message);
                    return;
                }
            }
        }

        /// <summary>
        /// First-run: extract the PS5 haptic clips from the user's own game
        /// files into the plugin's clips folder.
        /// </summary>
        private void EnsureClips()
        {
            try
            {
                string gameRoot = GameLocator.Locate(interactive: false);
                string clipsDir = null;
                if (gameRoot != null)
                    clipsDir = Path.Combine(gameRoot, "BepInEx", "plugins", "SilksongPS5Haptics", "clips");

                if (clipsDir != null && Directory.Exists(clipsDir) && Directory.GetFiles(clipsDir, "*.wav").Length >= 300)
                    return;   // already extracted

                if (gameRoot == null)
                {
                    gameRoot = GameLocator.Locate(interactive: true);
                    if (gameRoot == null)
                    {
                        Log("Game location not provided; clips unavailable (game will use normal rumble)");
                        return;
                    }
                    clipsDir = Path.Combine(gameRoot, "BepInEx", "plugins", "SilksongPS5Haptics", "clips");
                }

                state = State.Extracting;
                Log("Extracting PS5 haptic clips from game files (one-time)...");
                int n = ClipExtractor.ExtractAll(gameRoot, clipsDir, delegate (int done, int total)
                {
                    stateDetail = string.Format("{0}/{1}", done, total);
                });
                Log(string.Format("Extracted {0} haptic clips -> {1}", n, clipsDir));
            }
            catch (Exception e)
            {
                Log("Extraction failed: " + e);
            }
        }

        private MMDevice FindDevice()
        {
            try
            {
                using (var enumerator = new MMDeviceEnumerator())
                {
                    return enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                        .FirstOrDefault(d => d.FriendlyName.IndexOf(deviceMatch, StringComparison.OrdinalIgnoreCase) >= 0);
                }
            }
            catch (Exception e)
            {
                Log("Device enumeration failed: " + e.Message);
                return null;
            }
        }

        private void SleepInterruptible(int ms)
        {
            for (int i = 0; i < ms / 100 && !stop; i++)
                Thread.Sleep(100);
        }

        private static int ReadBlock(Stream s, byte[] block)
        {
            int total = 0;
            while (total < block.Length)
            {
                int n = s.Read(block, total, block.Length - total);
                if (n == 0) return total;
                total += n;
            }
            return total;
        }

        private static readonly object logLock = new object();
        public static void Log(string message)
        {
            try
            {
                lock (logLock)
                    File.AppendAllText(LogPath, string.Format("[{0:HH:mm:ss}] {1}\r\n", DateTime.Now, message));
            }
            catch { }
        }
    }
}
