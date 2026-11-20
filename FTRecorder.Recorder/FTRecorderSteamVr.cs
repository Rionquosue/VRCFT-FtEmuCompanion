using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Riry.RiryTools.FTRecorder
{
    /// <summary>
    /// "Start with SteamVR": registers the Recorder with SteamVR as an overlay application (a .vrmanifest next to the
    /// app) and turns its auto-launch on or off, through Valve's openvr_api.dll (bundled, BSD 3-Clause).
    /// </summary>
    internal static unsafe class FTRecorderSteamVr
    {
        internal const string AppKey = "riry.rirytools.ftrecorder.recorder";
        private const string ManifestName = "RiryTools.FTRecorder.Recorder.vrmanifest";
        private const string Interface = "FnTable:IVRApplications_008";
        private const int ApplicationBackground = 3;   // EVRApplicationType: does not start SteamVR
        private const int ApplicationUtility = 4;      // EVRApplicationType: settings access, no hardware
        private const int NoServerForBackgroundApp = 121;

        // IVRApplications_008's function table, in openvr.h's order.
        private const int AddApplicationManifest = 0;
        private const int RemoveApplicationManifest = 1;
        private const int GetApplicationsErrorNameFromEnum = 13;
        private const int SetApplicationAutoLaunch = 17;
        private const int GetApplicationAutoLaunch = 18;

        private static IntPtr library;

        internal static string ManifestPath => Path.Combine(AppContext.BaseDirectory, ManifestName);

        /// <summary>
        /// Whether SteamVR starts the Recorder; null when SteamVR is not running (it is not started to find out).
        /// </summary>
        internal static bool? IsAutoLaunch()
        {
            IntPtr* table = Open(ApplicationBackground, out bool notRunning);
            if (notRunning) return null;
            try
            {
                using var key = new Utf8(AppKey);
                return ((delegate* unmanaged<IntPtr, byte>)table[GetApplicationAutoLaunch])(key.Pointer) != 0;
            }
            finally
            {
                Shutdown();
            }
        }

        /// <summary>
        /// On: writes the manifest next to the app, registers it and turns auto-launch on. Off: turns auto-launch off and
        /// removes the manifest. Needs SteamVR's server, which OpenVR starts when it is not running.
        /// </summary>
        internal static void SetAutoLaunch(bool on)
        {
            if (on) WriteManifest();
            IntPtr* table = Open(ApplicationUtility, out _);
            try
            {
                using var path = new Utf8(ManifestPath);
                using var key = new Utf8(AppKey);
                if (on)
                {
                    Check(table, ((delegate* unmanaged<IntPtr, byte, int>)table[AddApplicationManifest])(path.Pointer, 0), "registering the Recorder");
                    Check(table, ((delegate* unmanaged<IntPtr, byte, int>)table[SetApplicationAutoLaunch])(key.Pointer, 1), "turning on its auto-launch");
                }
                else
                {
                    Check(table, ((delegate* unmanaged<IntPtr, byte, int>)table[SetApplicationAutoLaunch])(key.Pointer, 0), "turning off its auto-launch");
                    Check(table, ((delegate* unmanaged<IntPtr, int>)table[RemoveApplicationManifest])(path.Pointer), "unregistering the Recorder");
                }
            }
            finally
            {
                Shutdown();
            }
        }

        // SteamVR's application manifest: an overlay application (SteamVR only auto-launches those) started from this exe.
        private static void WriteManifest()
        {
            string exe = Path.GetFileName(Environment.ProcessPath ?? "RiryTools.FTRecorder.Recorder.exe");
            var manifest = new
            {
                applications = new[]
                {
                    new Application
                    {
                        AppKey = AppKey,
                        LaunchType = "binary",
                        BinaryPathWindows = exe,
                        IsDashboardOverlay = true,
                        Strings = new() { ["en_us"] = new Strings { Name = "RiryTools FTRecorder Recorder", Description = "Relays VRCFaceTracking's OSC output to VRChat and records it on demand." } },
                    },
                },
            };
            try
            {
                File.WriteAllText(ManifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (UnauthorizedAccessException exception)
            {
                throw new InvalidOperationException("The SteamVR manifest cannot be written next to the Recorder (" + ManifestPath + "). Move the Recorder to a folder you can write to.", exception);
            }
        }

        private sealed class Application
        {
            [System.Text.Json.Serialization.JsonPropertyName("app_key")] public string AppKey { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("launch_type")] public string LaunchType { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("binary_path_windows")] public string BinaryPathWindows { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("is_dashboard_overlay")] public bool IsDashboardOverlay { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("strings")] public System.Collections.Generic.Dictionary<string, Strings> Strings { get; set; }
        }

        private sealed class Strings
        {
            [System.Text.Json.Serialization.JsonPropertyName("name")] public string Name { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("description")] public string Description { get; set; }
        }

        // VR_InitInternal2 and the IVRApplications function table. notRunning: a Background start found no SteamVR.
        private static IntPtr* Open(int type, out bool notRunning)
        {
            notRunning = false;
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Starting with SteamVR is for Windows.");
            if (library == IntPtr.Zero)
            {
                string path = Path.Combine(AppContext.BaseDirectory, "openvr_api.dll");
                if (!NativeLibrary.TryLoad(path, out library)) throw new InvalidOperationException("openvr_api.dll could not be loaded from " + path + ".");
            }
            int error = 0;
            using (var startup = new Utf8(""))
            {
                ((delegate* unmanaged[Cdecl]<int*, int, IntPtr, uint>)Export("VR_InitInternal2"))(&error, type, startup.Pointer);
            }
            if (error == NoServerForBackgroundApp)
            {
                notRunning = true;
                return null;
            }
            if (error != 0) throw new InvalidOperationException("SteamVR could not be reached: " + InitError(error) + ".");
            IntPtr table;
            using (var name = new Utf8(Interface))
            {
                table = ((delegate* unmanaged[Cdecl]<IntPtr, int*, IntPtr>)Export("VR_GetGenericInterface"))(name.Pointer, &error);
            }
            if (error != 0 || table == IntPtr.Zero)
            {
                Shutdown();
                throw new InvalidOperationException("SteamVR's application settings are unavailable: " + InitError(error) + ".");
            }
            return (IntPtr*)table;
        }

        private static void Shutdown() => ((delegate* unmanaged[Cdecl]<void>)Export("VR_ShutdownInternal"))();

        private static IntPtr Export(string name) => NativeLibrary.GetExport(library, name);

        private static string InitError(int error)
        {
            IntPtr text = ((delegate* unmanaged[Cdecl]<int, IntPtr>)Export("VR_GetVRInitErrorAsEnglishDescription"))(error);
            return (text == IntPtr.Zero ? "error" : Marshal.PtrToStringUTF8(text)) + " (" + error + ")";
        }

        private static void Check(IntPtr* table, int error, string what)
        {
            if (error == 0) return;
            IntPtr text = ((delegate* unmanaged<int, IntPtr>)table[GetApplicationsErrorNameFromEnum])(error);
            throw new InvalidOperationException("SteamVR refused " + what + ": " + (text == IntPtr.Zero ? "error" : Marshal.PtrToStringUTF8(text)) + " (" + error + ").");
        }

        // A NUL-terminated UTF-8 copy of a string, freed on Dispose.
        private readonly struct Utf8 : IDisposable
        {
            internal readonly IntPtr Pointer;

            internal Utf8(string text)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(text + "\0");
                Pointer = Marshal.AllocHGlobal(bytes.Length);
                Marshal.Copy(bytes, 0, Pointer, bytes.Length);
            }

            public void Dispose() => Marshal.FreeHGlobal(Pointer);
        }
    }
}

// ============================================================================
// GLOSSARY: FTRecorder SteamVR
// ============================================================================
//
// FTRecorderSteamVr (this file)  The Recorder's "Start with SteamVR", through openvr_api.dll next to the app.
//   AppKey                   riry.rirytools.ftrecorder.recorder, the Recorder's SteamVR application key.
//   ManifestPath             RiryTools.FTRecorder.Recorder.vrmanifest next to the app: an overlay application
//                            (is_dashboard_overlay; SteamVR only auto-launches those) started from this exe.
//   IsAutoLaunch()           GetApplicationAutoLaunch, connecting as a Background application: null when SteamVR is not
//                            running (it is not started to find out).
//   SetAutoLaunch(on)        On: writes the manifest, AddApplicationManifest (permanent), SetApplicationAutoLaunch(true).
//                            Off: SetApplicationAutoLaunch(false), RemoveApplicationManifest. Connects as a Utility
//                            application, which starts SteamVR's server when needed. Throws with SteamVR's error.
//   Function table           IVRApplications_008 (OpenVR SDK 2.15.6): Add 0, Remove 1, GetApplicationsErrorNameFromEnum 13,
//                            SetApplicationAutoLaunch 17, GetApplicationAutoLaunch 18.
