using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading;

namespace SilksongPS5Haptics
{
    /// <summary>Decoded haptic clip: interleaved stereo float frames at 48 kHz.</summary>
    public sealed class HapticPcm
    {
        public readonly float[] Samples;   // interleaved L,R
        public readonly int FrameCount;
        public HapticPcm(float[] samples)
        {
            Samples = samples;
            FrameCount = samples.Length / 2;
        }
    }

    /// <summary>
    /// Owns the mix thread: sums active emissions into 10 ms stereo float
    /// blocks and ships them over a named pipe to the HapticsBridge app, which
    /// plays them on the DualSense audio endpoint.
    /// </summary>
    public static class HapticEngine
    {
        public const int SampleRate = 48000;
        private const int BlockFrames = 480;                    // 10 ms
        private const int BlockBytes = BlockFrames * 2 * 4;     // stereo float32

        // Main-thread state cached once per frame (audio thread must not touch Unity APIs).
        public static volatile float CachedTimeScale = 1f;
        public static volatile float CachedStrengthMultiplier = 1f;
        public static volatile float CachedMasterGain = 1f;
        public static volatile bool CachedMixerPaused;

        public static volatile bool Connected;

        private static readonly List<PS5HapticEmission> active = new List<PS5HapticEmission>();
        private static readonly object mixLock = new object();
        private static readonly Dictionary<string, HapticPcm> pcmCache = new Dictionary<string, HapticPcm>();
        private static readonly HashSet<string> missingWarned = new HashSet<string>();

        private static string clipsDir;
        private static int port;
        private static Thread mixThread;
        private static volatile bool shutdown;
        private static string lastConnectError;

        public static void Start(string clipsDirectory, int bridgePort)
        {
            clipsDir = clipsDirectory;
            port = bridgePort;
            mixThread = new Thread(MixLoop) { IsBackground = true, Name = "PS5HapticMix", Priority = ThreadPriority.AboveNormal };
            mixThread.Start();
        }

        public static void Shutdown()
        {
            shutdown = true;
        }

        public static void Register(PS5HapticEmission emission)
        {
            lock (mixLock)
            {
                if (!active.Contains(emission))
                    active.Add(emission);
            }
        }

        public static bool TryGetPcm(string clipName, out HapticPcm pcm)
        {
            lock (pcmCache)
            {
                if (pcmCache.TryGetValue(clipName, out pcm))
                    return pcm != null;

                string path = Path.Combine(clipsDir, clipName + ".wav");
                if (!File.Exists(path))
                {
                    // Don't cache the miss: the bridge's first-run extraction
                    // may still be writing clips while the game is running.
                    if (missingWarned.Add(clipName))
                        PS5HapticsPlugin.Log.LogWarning($"No haptic wav (yet) for clip '{clipName}' - rumble fallback");
                    return false;
                }
                try
                {
                    pcm = LoadWav(path);
                    pcmCache[clipName] = pcm;
                    return true;
                }
                catch (Exception e)
                {
                    PS5HapticsPlugin.Log.LogError($"Failed to load {path}: {e.Message}");
                    pcmCache[clipName] = null;
                    return false;
                }
            }
        }

        // Minimal RIFF reader for the extracted clips (PCM16, 48 kHz, 1-2 channels).
        private static HapticPcm LoadWav(string path)
        {
            using (var br = new BinaryReader(File.OpenRead(path)))
            {
                if (new string(br.ReadChars(4)) != "RIFF") throw new InvalidDataException("not RIFF");
                br.ReadInt32();
                if (new string(br.ReadChars(4)) != "WAVE") throw new InvalidDataException("not WAVE");

                short channels = 0, bits = 0;
                int rate = 0;
                byte[] data = null;
                while (br.BaseStream.Position + 8 <= br.BaseStream.Length)
                {
                    string id = new string(br.ReadChars(4));
                    int size = br.ReadInt32();
                    if (id == "fmt ")
                    {
                        short fmt = br.ReadInt16();
                        channels = br.ReadInt16();
                        rate = br.ReadInt32();
                        br.ReadInt32(); br.ReadInt16();
                        bits = br.ReadInt16();
                        br.BaseStream.Seek(size - 16, SeekOrigin.Current);
                        if (fmt != 1) throw new InvalidDataException($"unsupported wav format {fmt}");
                    }
                    else if (id == "data")
                    {
                        data = br.ReadBytes(size);
                    }
                    else
                    {
                        br.BaseStream.Seek(size + (size & 1), SeekOrigin.Current);
                    }
                }
                if (data == null || channels == 0) throw new InvalidDataException("missing chunks");
                if (bits != 16) throw new InvalidDataException($"expected PCM16, got {bits}-bit");
                if (rate != SampleRate) PS5HapticsPlugin.Log.LogWarning($"{Path.GetFileName(path)}: {rate} Hz (expected {SampleRate}) - playing as-is");

                int frames = data.Length / 2 / channels;
                var stereo = new float[frames * 2];
                for (int f = 0; f < frames; f++)
                {
                    float l = BitConverter.ToInt16(data, (f * channels) * 2) / 32768f;
                    float r = channels >= 2 ? BitConverter.ToInt16(data, (f * channels + 1) * 2) / 32768f : l;
                    stereo[f * 2] = l;
                    stereo[f * 2 + 1] = r;
                }
                return new HapticPcm(stereo);
            }
        }

        private static void MixLoop()
        {
            var block = new float[BlockFrames * 2];
            var bytes = new byte[BlockBytes];
            var toRemove = new List<PS5HapticEmission>();

            while (!shutdown)
            {
                TcpClient client = null;
                try
                {
                    client = new TcpClient();
                    client.Connect("127.0.0.1", port);
                    client.NoDelay = true;
                    var stream = client.GetStream();
                    Connected = true;
                    lastConnectError = null;
                    PS5HapticsPlugin.Log.LogInfo($"Connected to HapticsBridge on port {port}");

                    var clock = Stopwatch.StartNew();
                    long blocksSent = 0;

                    while (!shutdown)
                    {
                        MixBlock(block, toRemove);
                        Buffer.BlockCopy(block, 0, bytes, 0, BlockBytes);
                        stream.Write(bytes, 0, BlockBytes);
                        blocksSent++;

                        // Pace to real time: each block is 10 ms.
                        long dueMs = blocksSent * 10;
                        long aheadMs = dueMs - clock.ElapsedMilliseconds;
                        if (aheadMs > 2)
                            Thread.Sleep((int)aheadMs - 1);
                    }
                }
                catch (Exception e)
                {
                    if (Connected)
                    {
                        PS5HapticsPlugin.Log.LogWarning($"Bridge disconnected ({e.GetType().Name}: {e.Message}); falling back to rumble");
                    }
                    else
                    {
                        // Log each distinct failure reason once, so a dead bridge is visible.
                        string msg = $"{e.GetType().Name}: {e.Message}";
                        if (msg != lastConnectError)
                        {
                            lastConnectError = msg;
                            PS5HapticsPlugin.Log.LogWarning($"Cannot reach HapticsBridge on 127.0.0.1:{port} ({msg}) - using rumble fallback, retrying every 3s");
                        }
                    }
                }
                finally
                {
                    Connected = false;
                    client?.Close();
                }
                if (!shutdown)
                    Thread.Sleep(3000);   // retry connect
            }
        }

        private static void MixBlock(float[] block, List<PS5HapticEmission> toRemove)
        {
            Array.Clear(block, 0, block.Length);
            float timeScale = CachedTimeScale;
            float gain = CachedStrengthMultiplier * CachedMasterGain;
            bool paused = CachedMixerPaused;

            lock (mixLock)
            {
                toRemove.Clear();
                foreach (var em in active)
                {
                    if (!em.IsPlaying) { toRemove.Add(em); continue; }
                    if (paused) continue;
                    if (timeScale <= float.Epsilon && !em.IsRealtime) continue;

                    var motors = em.Target.Motors;
                    if (motors == VibrationMotors.None) { em.Advance(BlockFrames * em.Speed); continue; }
                    float lGate = (motors & VibrationMotors.Left) != 0 ? 1f : 0f;
                    float rGate = (motors & VibrationMotors.Right) != 0 ? 1f : 0f;
                    float amp = em.Strength * gain;

                    double pos = em.PositionFrames;
                    double speed = em.Speed;
                    var pcm = em.Pcm;
                    for (int f = 0; f < BlockFrames; f++)
                    {
                        int src = (int)pos;
                        if (src >= pcm.FrameCount)
                        {
                            if (!em.IsLooping) break;
                            pos -= pcm.FrameCount;
                            src = (int)pos;
                        }
                        block[f * 2] += pcm.Samples[src * 2] * amp * lGate;
                        block[f * 2 + 1] += pcm.Samples[src * 2 + 1] * amp * rGate;
                        pos += speed;
                    }
                    em.Advance(BlockFrames * speed);
                }
                foreach (var em in toRemove)
                    active.Remove(em);
            }

            for (int i = 0; i < block.Length; i++)
            {
                if (block[i] > 1f) block[i] = 1f;
                else if (block[i] < -1f) block[i] = -1f;
            }
        }
    }
}
