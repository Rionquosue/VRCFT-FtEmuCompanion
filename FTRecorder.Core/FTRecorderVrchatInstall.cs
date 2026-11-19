using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Riry.RiryTools.FTRecorder
{
    /// <summary>
    /// Finds the OVRLipSync library inside an installed copy of VRChat, which ships it as a Unity plugin. VRChat is
    /// looked for through Steam: Steam's folder (from the registry on Windows, else the usual places), every library
    /// listed in its libraryfolders.vdf, and VRChat's app id 438100. Only files are read; nothing in VRChat's folder is
    /// changed.
    /// </summary>
    internal static class FTRecorderVrchatInstall
    {
        internal const string AppId = "438100";
        private static readonly Regex LibraryPath = new Regex("\"path\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.CultureInvariant);
        private static readonly Regex InstallDir = new Regex("\"installdir\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.CultureInvariant);

        /// <summary>The OVRLipSync library of the first VRChat install found, or null. searched lists the VRChat folders looked in.</summary>
        internal static string FindOvrLipSync(out List<string> searched)
        {
            searched = new List<string>();
            foreach (string vrchat in VrchatFolders())
            {
                searched.Add(vrchat);
                string plugins = Path.Combine(vrchat, "VRChat_Data", "Plugins");
                foreach (string candidate in new[] { Path.Combine(plugins, "x86_64", "OVRLipSync.dll"), Path.Combine(plugins, "OVRLipSync.dll") })
                {
                    if (File.Exists(candidate)) return candidate;
                }
                if (!Directory.Exists(plugins)) continue;
                try
                {
                    foreach (string file in Directory.EnumerateFiles(plugins, "*LipSync*.dll", SearchOption.AllDirectories)) return file;
                }
                catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
                {
                    // An unreadable folder is skipped.
                }
            }
            return null;
        }

        // VRChat's folder in every Steam library that has it.
        private static IEnumerable<string> VrchatFolders()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string steam in SteamFolders())
            {
                foreach (string library in Libraries(steam))
                {
                    string apps = Path.Combine(library, "steamapps");
                    string folder = "VRChat";
                    string manifest = Path.Combine(apps, "appmanifest_" + AppId + ".acf");
                    string text = Read(manifest);
                    if (text != null)
                    {
                        Match match = InstallDir.Match(text);
                        if (match.Success) folder = Unescape(match.Groups[1].Value);
                    }
                    string vrchat = Path.Combine(apps, "common", folder);
                    if (Directory.Exists(vrchat) && seen.Add(Path.GetFullPath(vrchat))) yield return vrchat;
                }
            }
        }

        private static IEnumerable<string> SteamFolders()
        {
            var folders = new List<string>();
            if (OperatingSystem.IsWindows())
            {
                foreach (string key in new[] { @"HKEY_CURRENT_USER\Software\Valve\Steam", @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", @"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam" })
                {
                    foreach (string name in new[] { "SteamPath", "InstallPath" })
                    {
                        try
                        {
                            if (Microsoft.Win32.Registry.GetValue(key, name, null) is string path && path.Length > 0) folders.Add(path.Replace('/', Path.DirectorySeparatorChar));
                        }
                        catch (Exception exception) when (exception is System.Security.SecurityException || exception is IOException || exception is UnauthorizedAccessException)
                        {
                            // Not readable: the usual places below are tried.
                        }
                    }
                }
                folders.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
                folders.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"));
            }
            else
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                folders.Add(Path.Combine(home, ".steam", "steam"));
                folders.Add(Path.Combine(home, ".local", "share", "Steam"));
                folders.Add(Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam"));
            }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string folder in folders)
            {
                if (Directory.Exists(folder) && seen.Add(Path.GetFullPath(folder))) yield return folder;
            }
        }

        // Steam's own folder and every library in steamapps/libraryfolders.vdf.
        private static IEnumerable<string> Libraries(string steam)
        {
            yield return steam;
            string text = Read(Path.Combine(steam, "steamapps", "libraryfolders.vdf"));
            if (text == null) yield break;
            foreach (Match match in LibraryPath.Matches(text)) yield return Unescape(match.Groups[1].Value);
        }

        private static string Read(string path)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static string Unescape(string value) => value.Replace("\\\\", "\\");
    }
}

// ============================================================================
// GLOSSARY: FTRecorder VRChat install
// ============================================================================
//
// FTRecorderVrchatInstall (this file)
//   FindOvrLipSync(out searched)  VRChat_Data/Plugins/x86_64/OVRLipSync.dll, Plugins/OVRLipSync.dll, else any
//                            *LipSync*.dll under Plugins, in the first VRChat install found; null when none. searched
//                            lists the VRChat folders looked in.
//   Steam folders            Windows: registry SteamPath / InstallPath (HKCU, HKLM WOW6432Node, HKLM), then Program
//                            Files (x86)/Steam and Program Files/Steam. Elsewhere: ~/.steam/steam, ~/.local/share/Steam,
//                            the Flatpak Steam folder.
//   Libraries                Steam's folder and every "path" in steamapps/libraryfolders.vdf.
//   VRChat folder            steamapps/common/<installdir from appmanifest_438100.acf, else VRChat>.
