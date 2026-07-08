using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HapticsBridge
{
    /// <summary>
    /// Powers on a USB DualSense's audio path so its haptic actuators render
    /// the audio we stream to channels 3/4. By default the controller leaves
    /// that path unpowered: audio to ch3/4 drains from Windows but moves
    /// nothing, so everything looks fine yet nothing buzzes. Companion apps
    /// like DSX keep the path enabled by continuously sending HID output
    /// reports — which is the only reason haptics seem to "need" DSX. We send
    /// the same enable ourselves so a plain USB pad works with nothing else
    /// running.
    ///
    /// USB output report 0x02, per the DualSense output-report spec
    /// (game-controller-collective / dualsensectl): valid_flag0 bit 7
    /// (AUDIO_CONTROL_ENABLE) + bit 5 (SPEAKER_VOLUME_ENABLE) + bit 4
    /// (HEADPHONE_VOLUME_ENABLE); valid_flag1 bit 7 (AUDIO_CONTROL2_ENABLE);
    /// speaker volume (byte 6) full, headphone volume (byte 5) full, audio
    /// flags (byte 8) = 0 so the actuator channels aren't rerouted to the
    /// built-in loudspeaker. Motors stay 0 (no classic rumble). Verified on
    /// hardware: silent input = silent pad, ch3/4 audio = haptics.
    ///
    /// Bluetooth pads use a different framed report (0x31 + CRC) and have no
    /// native audio endpoint on Windows anyway (that path always involves
    /// DSX, which already enables audio), so only USB pads are configured.
    /// </summary>
    internal static class DualSenseHid
    {
        private const ushort SonyVid = 0x054C;
        private const ushort DualSensePid = 0x0CE6;
        private const ushort DualSenseEdgePid = 0x0DF2;

        private static readonly object sync = new object();
        private static List<string> cachedPaths;
        private static bool loggedAsserted, loggedNone;

        /// <summary>
        /// Writes the audio-haptics mode select to every USB DualSense.
        /// Safe to call repeatedly (the watchdog re-asserts it, since the game
        /// can flip the mode back at any time). Never throws.
        /// </summary>
        public static void AssertAudioHaptics()
        {
            lock (sync)
            {
                int ok = TryWriteAll();
                if (ok == 0)
                {
                    // Stale cache (replug changes the device path) — rescan once.
                    cachedPaths = null;
                    ok = TryWriteAll();
                }

                if (ok > 0 && !loggedAsserted)
                {
                    loggedAsserted = true;
                    loggedNone = false;
                    Engine.Log(string.Format("HID: audio haptics enabled ({0} USB controller{1})", ok, ok > 1 ? "s" : ""));
                }
                else if (ok == 0 && !loggedNone)
                {
                    loggedNone = true;
                    loggedAsserted = false;
                    Engine.Log("HID: no USB DualSense found to enable (fine on Bluetooth or when DSX manages the pad)");
                }
            }
        }

        private static int TryWriteAll()
        {
            try
            {
                if (cachedPaths == null)
                    cachedPaths = FindDualSensePaths();
                int ok = 0;
                foreach (var path in cachedPaths)
                    if (TryWriteModeSelect(path)) ok++;
                return ok;
            }
            catch
            {
                return 0;
            }
        }

        private static bool TryWriteModeSelect(string path)
        {
            using (var handle = CreateFile(path, GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero))
            {
                if (handle.IsInvalid) return false;

                IntPtr preparsed;
                if (!HidD_GetPreparsedData(handle, out preparsed)) return false;
                HIDP_CAPS caps;
                try { if (HidP_GetCaps(preparsed, out caps) != HIDP_STATUS_SUCCESS) return false; }
                finally { HidD_FreePreparsedData(preparsed); }

                // USB output reports are 48 bytes; Bluetooth's framed report is
                // 78 and needs a CRC we deliberately don't send (see class doc).
                int len = caps.OutputReportByteLength;
                if (len < 48 || len > 64) return false;

                var report = new byte[len];
                report[0] = 0x02;   // USB output report id; common struct begins at [1]
                report[1] = 0xB0;   // valid_flag0: AUDIO_CONTROL | SPEAKER_VOL | HEADPHONE_VOL enable
                report[2] = 0x80;   // valid_flag1: AUDIO_CONTROL2 enable
                report[3] = 0x00;   // motor_right (no classic rumble)
                report[4] = 0x00;   // motor_left
                report[5] = 0x7F;   // headphone_audio_volume (full)
                report[6] = 0xFF;   // speaker_audio_volume (full — drives actuator gain)
                report[7] = 0x00;   // internal_microphone_volume
                report[8] = 0x00;   // audio_flags: keep actuator channels off the loudspeaker
                try
                {
                    using (var fs = new FileStream(handle, FileAccess.Write, len, false))
                    {
                        fs.Write(report, 0, len);
                        fs.Flush();
                    }
                    return true;
                }
                catch { return false; }
            }
        }

        private static List<string> FindDualSensePaths()
        {
            var paths = new List<string>();
            Guid hidGuid;
            HidD_GetHidGuid(out hidGuid);
            IntPtr devInfo = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (devInfo == INVALID_HANDLE_VALUE) return paths;
            try
            {
                var ifData = new SP_DEVICE_INTERFACE_DATA();
                ifData.cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));
                for (int i = 0; SetupDiEnumDeviceInterfaces(devInfo, IntPtr.Zero, ref hidGuid, i, ref ifData); i++)
                {
                    string path = GetInterfacePath(devInfo, ref ifData);
                    if (path == null) continue;
                    using (var h = CreateFile(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero))
                    {
                        if (h.IsInvalid) continue;
                        var attrs = new HIDD_ATTRIBUTES();
                        attrs.Size = Marshal.SizeOf(typeof(HIDD_ATTRIBUTES));
                        if (!HidD_GetAttributes(h, ref attrs)) continue;
                        if (attrs.VendorID == SonyVid && (attrs.ProductID == DualSensePid || attrs.ProductID == DualSenseEdgePid))
                            paths.Add(path);
                    }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(devInfo); }
            return paths;
        }

        private static string GetInterfacePath(IntPtr devInfo, ref SP_DEVICE_INTERFACE_DATA ifData)
        {
            int required = 0;
            SetupDiGetDeviceInterfaceDetail(devInfo, ref ifData, IntPtr.Zero, 0, ref required, IntPtr.Zero);
            if (required <= 0) return null;
            IntPtr detail = Marshal.AllocHGlobal(required);
            try
            {
                // cbSize is the fixed part only: 8 on x64, 4 + char size on x86.
                Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 4 + Marshal.SystemDefaultCharSize);
                if (!SetupDiGetDeviceInterfaceDetail(devInfo, ref ifData, detail, required, ref required, IntPtr.Zero))
                    return null;
                return Marshal.PtrToStringAuto(new IntPtr(detail.ToInt64() + 4));
            }
            finally { Marshal.FreeHGlobal(detail); }
        }

        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 0x1;
        private const uint FILE_SHARE_WRITE = 0x2;
        private const uint OPEN_EXISTING = 3;
        private const int DIGCF_PRESENT = 0x2;
        private const int DIGCF_DEVICEINTERFACE = 0x10;
        private const int HIDP_STATUS_SUCCESS = 0x00110000;
        private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HIDD_ATTRIBUTES
        {
            public int Size;
            public ushort VendorID;
            public ushort ProductID;
            public ushort VersionNumber;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HIDP_CAPS
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
            public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps;
            public ushort NumberInputValueCaps;
            public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps;
            public ushort NumberOutputValueCaps;
            public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps;
            public ushort NumberFeatureValueCaps;
            public ushort NumberFeatureDataIndices;
        }

        [DllImport("hid.dll")]
        private static extern void HidD_GetHidGuid(out Guid hidGuid);

        [DllImport("hid.dll")]
        private static extern bool HidD_GetAttributes(SafeFileHandle hidDeviceObject, ref HIDD_ATTRIBUTES attributes);

        [DllImport("hid.dll")]
        private static extern bool HidD_GetPreparsedData(SafeFileHandle hidDeviceObject, out IntPtr preparsedData);

        [DllImport("hid.dll")]
        private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

        [DllImport("hid.dll")]
        private static extern int HidP_GetCaps(IntPtr preparsedData, out HIDP_CAPS capabilities);

        [DllImport("setupapi.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, int flags);

        [DllImport("setupapi.dll")]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, int memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

        [DllImport("setupapi.dll", CharSet = CharSet.Auto)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr deviceInterfaceDetailData, int deviceInterfaceDetailDataSize, ref int requiredSize, IntPtr deviceInfoData);

        [DllImport("setupapi.dll")]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
    }
}
