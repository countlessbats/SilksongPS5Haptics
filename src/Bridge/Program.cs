using System;
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

        public TrayContext(string[] args)
        {
            engine = new Engine(args);

            var menu = new ContextMenuStrip();
            menu.Items.Add("Play test chime", null, delegate { engine.RequestChime(); });
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
        private readonly int bufferMs;
        private readonly string map;
        private readonly bool chimeOnStart;
        private volatile bool stop;
        private volatile bool chimeRequested;
        private Thread thread;
        private TcpListener listener;

        public Engine(string[] args)
        {
            deviceMatch = Program.GetArg(args, "--device") ?? "DualSense";
            int p, b;
            port = int.TryParse(Program.GetArg(args, "--port"), out p) ? p : 48111;
            bufferMs = int.TryParse(Program.GetArg(args, "--buffer-ms"), out b) ? b : 60;
            map = Program.GetArg(args, "--map") ?? "auto";
            chimeOnStart = !args.Contains("--no-chime");
            try { File.WriteAllText(LogPath, ""); } catch { }
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

        public void RequestChime() { chimeRequested = true; }

        private void Run()
        {
            EnsureClips();

            bool firstChimePlayed = false;
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

                    var buffer = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(Program.SampleRate, 2))
                    {
                        BufferDuration = TimeSpan.FromMilliseconds(Math.Max(bufferMs * 4, 250)),
                        DiscardOnBufferOverflow = true,
                    };
                    ISampleProvider source = buffer.ToSampleProvider();
                    if (mix.SampleRate != Program.SampleRate)
                        source = new WdlResamplingSampleProvider(source, mix.SampleRate);
                    var provider = new ChannelMapWaveProvider(source, mix, leftCh, rightCh);

                    bool deviceLost = false;
                    using (var output = new WasapiOut(device, AudioClientShareMode.Shared, true, 20))
                    {
                        output.PlaybackStopped += delegate (object s, StoppedEventArgs e)
                        {
                            deviceLost = true;
                            try { if (listener != null) listener.Stop(); } catch { }
                            if (e.Exception != null) Log("Playback stopped: " + e.Exception.Message);
                        };
                        output.Init(provider);
                        output.Play();

                        if (chimeOnStart && !firstChimePlayed)
                        {
                            firstChimePlayed = true;
                            Chime.Play(buffer);
                            Log("Startup chime played");
                        }

                        listener = new TcpListener(IPAddress.Loopback, port);
                        listener.Start();
                        Log("Listening on 127.0.0.1:" + port);
                        state = State.Listening;

                        var block = new byte[480 * 2 * 4];
                        while (!stop && !deviceLost)
                        {
                            while (!stop && !deviceLost && !listener.Pending())
                            {
                                if (chimeRequested)
                                {
                                    chimeRequested = false;
                                    Chime.Play(buffer);
                                }
                                Thread.Sleep(100);
                            }
                            if (stop || deviceLost) break;

                            using (var client = listener.AcceptTcpClient())
                            {
                                client.NoDelay = true;
                                Log("Game connected");
                                state = State.GameConnected;
                                try
                                {
                                    using (var stream = client.GetStream())
                                    {
                                        int read;
                                        while (!stop && (read = ReadBlock(stream, block)) > 0)
                                        {
                                            if (buffer.BufferedDuration.TotalMilliseconds > bufferMs * 2)
                                                buffer.ClearBuffer();
                                            buffer.AddSamples(block, 0, read);
                                        }
                                    }
                                }
                                catch (IOException) { }
                                buffer.ClearBuffer();
                                Log("Game disconnected");
                                state = State.Listening;
                            }
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
                    try { if (listener != null) listener.Stop(); } catch { }
                    listener = null;
                }
                if (!stop)
                {
                    state = State.NoDevice;
                    SleepInterruptible(2000);
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
