using System;
using System.Collections.Generic;
using System.Threading;
using NAudio.Wave;

namespace HapticsBridge
{
    /// <summary>
    /// ~1.6s haptic signature: three rising pips alternating palms, then a
    /// detuned shimmer sweep ping-ponging L/R. Deliberately un-rumble-like,
    /// so hearing it in your hands confirms true haptics are flowing.
    /// </summary>
    internal static class Chime
    {
        public static void Play(BufferedWaveProvider buffer)
        {
            var samples = new List<float>();
            Action<double> silence = delegate (double sec)
            {
                for (int i = 0; i < (int)(sec * Program.SampleRate); i++) { samples.Add(0f); samples.Add(0f); }
            };

            double[][] pips = { new double[] { 180, 1, 0 }, new double[] { 250, 0, 1 }, new double[] { 330, 1, 1 } };
            foreach (var pip in pips)
            {
                int n = (int)(0.11 * Program.SampleRate);
                for (int i = 0; i < n; i++)
                {
                    double t = (double)i / Program.SampleRate;
                    float env = (float)(Math.Min(1.0, t / 0.005) * Math.Exp(-t * 28));
                    float v = 0.85f * env * (float)Math.Sin(2 * Math.PI * pip[0] * t);
                    samples.Add(v * (float)pip[1]);
                    samples.Add(v * (float)pip[2]);
                }
                silence(0.05);
            }
            silence(0.06);

            int shimmerN = (int)(0.9 * Program.SampleRate);
            double phase1 = 0, phase2 = 0;
            for (int i = 0; i < shimmerN; i++)
            {
                double t = (double)i / Program.SampleRate;
                double prog = (double)i / shimmerN;
                double freq = 200 + 140 * prog;
                phase1 += 2 * Math.PI * freq / Program.SampleRate;
                phase2 += 2 * Math.PI * (freq * 1.06) / Program.SampleRate;
                float trem = 0.6f + 0.4f * (float)Math.Sin(2 * Math.PI * 9 * t);
                float fade = (float)(Math.Min(1.0, t / 0.05) * (1.0 - Math.Pow(prog, 2.2)));
                float v = 0.55f * trem * fade * ((float)Math.Sin(phase1) + 0.6f * (float)Math.Sin(phase2));
                float pan = 0.5f + 0.5f * (float)Math.Sin(2 * Math.PI * 1.7 * t);
                samples.Add(v * (1f - pan));
                samples.Add(v * pan);
            }
            silence(0.05);

            var bytes = new byte[samples.Count * 4];
            Buffer.BlockCopy(samples.ToArray(), 0, bytes, 0, bytes.Length);
            // Feed in chunks: the jitter buffer is much shorter than the chime.
            // Deadline guards both waits — if the endpoint stops draining
            // mid-chime (Bluetooth stall), bail out instead of spinning forever
            // (the startup chime runs on the engine thread).
            int deadline = Environment.TickCount + (int)(1000.0 * samples.Count / 2 / Program.SampleRate) + 5000;
            for (int off = 0; off < bytes.Length; off += 3840)
            {
                while (buffer.BufferedDuration.TotalMilliseconds > 120)
                {
                    if (Environment.TickCount > deadline) return;
                    Thread.Sleep(5);
                }
                buffer.AddSamples(bytes, off, Math.Min(3840, bytes.Length - off));
            }
            while (buffer.BufferedDuration.TotalMilliseconds > 0)
            {
                if (Environment.TickCount > deadline) return;
                Thread.Sleep(10);
            }
        }
    }

    /// <summary>
    /// IWaveProvider that reports the device's own mix format and writes the
    /// stereo haptic source into two chosen channels of each output frame.
    /// Always fills the requested count (silence on underrun) so playback never stalls.
    /// Optionally mixes an imperceptible low-level pilot tone into the haptic
    /// channels: some Bluetooth audio stacks idle the link on sustained digital
    /// silence and never resume rendering, which freezes haptics until the
    /// device is reconnected. The pilot keeps the stream "non-silent" so the
    /// link stays awake. 100 Hz at -56 dBFS is far below actuator threshold.
    /// </summary>
    internal sealed class ChannelMapWaveProvider : IWaveProvider
    {
        private const float PilotAmp = 0.0015f;
        private const double PilotHz = 100.0;

        private readonly ISampleProvider source;
        private readonly int leftCh, rightCh;
        private readonly WaveFormat format;
        private readonly bool keepalive;
        private double pilotPhase;
        private float[] srcBuf = new float[0];

        public ChannelMapWaveProvider(ISampleProvider stereoSource, WaveFormat deviceMixFormat, int leftCh, int rightCh, bool keepalive)
        {
            source = stereoSource;
            format = deviceMixFormat;
            this.leftCh = leftCh;
            this.rightCh = rightCh;
            this.keepalive = keepalive;
        }

        public WaveFormat WaveFormat { get { return format; } }

        public int Read(byte[] buffer, int offset, int count)
        {
            int frames = count / format.BlockAlign;
            int srcNeeded = frames * 2;
            if (srcBuf.Length < srcNeeded) srcBuf = new float[srcNeeded];
            int srcFrames = source.Read(srcBuf, 0, srcNeeded) / 2;

            Array.Clear(buffer, offset, frames * format.BlockAlign);
            double phaseStep = 2 * Math.PI * PilotHz / format.SampleRate;
            for (int f = 0; f < frames; f++)
            {
                float pilot = 0f;
                if (keepalive)
                {
                    pilot = PilotAmp * (float)Math.Sin(pilotPhase);
                    pilotPhase += phaseStep;
                    if (pilotPhase > 2 * Math.PI) pilotPhase -= 2 * Math.PI;
                }
                float l = pilot, r = pilot;
                if (f < srcFrames)
                {
                    l += srcBuf[f * 2];
                    r += srcBuf[f * 2 + 1];
                }
                int frameByte = offset + f * format.BlockAlign;
                WriteFloat(buffer, frameByte + leftCh * 4, l);
                WriteFloat(buffer, frameByte + rightCh * 4, r);
            }
            return frames * format.BlockAlign;
        }

        private static void WriteFloat(byte[] buffer, int index, float value)
        {
            var bytes = BitConverter.GetBytes(value);
            buffer[index] = bytes[0];
            buffer[index + 1] = bytes[1];
            buffer[index + 2] = bytes[2];
            buffer[index + 3] = bytes[3];
        }
    }
}
