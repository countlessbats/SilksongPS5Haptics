using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using Fmod5Sharp;
using Fmod5Sharp.FmodTypes;
using NVorbis;

namespace HapticsBridge
{
    /// <summary>
    /// Extracts the PS5 haptic AudioClips from the user's own Silksong install
    /// (vibrationstatic_assets_vibrationdataps5.bundle) to 48 kHz PCM16 WAVs.
    /// Chain: Unity bundle -> AudioClip resource bytes -> FSB5 -> Vorbis -> PCM.
    /// </summary>
    internal static class ClipExtractor
    {
        private const string BundleRelPath =
            @"Hollow Knight Silksong_Data\StreamingAssets\aa\StandaloneWindows64\vibrationstatic_assets_vibrationdataps5.bundle";

        public static int ExtractAll(string gameRoot, string outDir, Action<int, int> progress)
        {
            string bundlePath = Path.Combine(gameRoot, BundleRelPath);
            if (!File.Exists(bundlePath))
                throw new FileNotFoundException("PS5 vibration bundle not found", bundlePath);
            Directory.CreateDirectory(outDir);

            var manager = new AssetsManager();
            try
            {
                var bundle = manager.LoadBundleFile(bundlePath);
                var dirInfos = bundle.file.BlockAndDirInfo.DirectoryInfos;

                var resCache = new Dictionary<string, byte[]>();
                Func<string, byte[]> getBundleFile = delegate (string name)
                {
                    byte[] cached;
                    if (resCache.TryGetValue(name, out cached)) return cached;
                    for (int i = 0; i < dirInfos.Count; i++)
                    {
                        if (dirInfos[i].Name == name)
                        {
                            var reader = bundle.file.DataReader;
                            reader.Position = dirInfos[i].Offset;
                            byte[] bytes = reader.ReadBytes((int)dirInfos[i].DecompressedSize);
                            resCache[name] = bytes;
                            return bytes;
                        }
                    }
                    return null;
                };

                var assetsFile = manager.LoadAssetsFileFromBundle(bundle, 0, false);
                var clips = assetsFile.file.GetAssetsOfType(AssetClassID.AudioClip);
                int done = 0, ok = 0;

                foreach (var info in clips)
                {
                    done++;
                    if (progress != null) progress(done, clips.Count);
                    try
                    {
                        var baseField = manager.GetBaseField(assetsFile, info);
                        string name = baseField["m_Name"].AsString;
                        var resource = baseField["m_Resource"];
                        string source = resource["m_Source"].AsString;
                        long offset = resource["m_Offset"].AsLong;
                        long size = resource["m_Size"].AsLong;

                        string resName = source.Substring(source.LastIndexOf('/') + 1);
                        byte[] resBytes = getBundleFile(resName);
                        if (resBytes == null) { Engine.Log(name + ": resource file missing"); continue; }

                        var fsbBytes = new byte[size];
                        Array.Copy(resBytes, offset, fsbBytes, 0, size);

                        FmodSoundBank bank = FsbLoader.LoadFsbFromByteArray(fsbBytes);
                        FmodSample sample = bank.Samples[0];
                        byte[] data;
                        string ext;
                        if (!sample.RebuildAsStandardFileFormat(out data, out ext))
                        { Engine.Log(name + ": FSB rebuild failed"); continue; }

                        string outPath = Path.Combine(outDir, name + ".wav");
                        if (ext == "ogg")
                        {
                            DecodeOggToWav(data, (int)sample.Metadata.SampleCount, outPath);
                        }
                        else if (ext == "wav")
                        {
                            File.WriteAllBytes(outPath, data);
                        }
                        else { Engine.Log(name + ": unexpected format " + ext); continue; }
                        ok++;
                    }
                    catch (Exception e)
                    {
                        Engine.Log("clip " + done + ": " + e.GetType().Name + ": " + e.Message);
                    }
                }
                return ok;
            }
            finally
            {
                manager.UnloadAll();
            }
        }

        private static void DecodeOggToWav(byte[] ogg, int declaredFrames, string outPath)
        {
            using (var vorbis = new VorbisReader(new MemoryStream(ogg)))
            {
                int channels = vorbis.Channels;
                int rate = vorbis.SampleRate;
                var all = new List<float>(declaredFrames > 0 ? declaredFrames * channels : 4096);
                var buf = new float[4800 * channels];
                int n;
                while ((n = vorbis.ReadSamples(buf, 0, buf.Length)) > 0)
                    for (int i = 0; i < n; i++) all.Add(buf[i]);

                // FSB declares the true frame count; the decoder may emit a
                // little padding beyond it. Trim to the authored length.
                int frames = all.Count / channels;
                if (declaredFrames > 0 && declaredFrames < frames)
                    frames = declaredFrames;

                WriteWavPcm16(outPath, all, frames, channels, rate);
            }
        }

        private static void WriteWavPcm16(string path, List<float> interleaved, int frames, int channels, int rate)
        {
            using (var bw = new BinaryWriter(File.Create(path)))
            {
                int dataLen = frames * channels * 2;
                bw.Write(Encoding.ASCII.GetBytes("RIFF")); bw.Write(36 + dataLen); bw.Write(Encoding.ASCII.GetBytes("WAVE"));
                bw.Write(Encoding.ASCII.GetBytes("fmt ")); bw.Write(16); bw.Write((short)1); bw.Write((short)channels);
                bw.Write(rate); bw.Write(rate * channels * 2); bw.Write((short)(channels * 2)); bw.Write((short)16);
                bw.Write(Encoding.ASCII.GetBytes("data")); bw.Write(dataLen);
                int count = frames * channels;
                for (int i = 0; i < count; i++)
                {
                    float f = interleaved[i];
                    float c = f < -1f ? -1f : (f > 1f ? 1f : f);
                    bw.Write((short)Math.Round(c * 32767f));
                }
            }
        }
    }
}
