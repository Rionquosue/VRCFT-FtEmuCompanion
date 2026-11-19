using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Riry.RiryTools.FTRecorder
{
    /// <summary>
    /// The module's settings, a JSON file the user edits (VRCFaceTracking has no settings page for modules). Kept in
    /// the user's local application data, so reinstalling the module keeps them. A default file is written the first
    /// time. Changes are picked up while VRCFaceTracking runs.
    /// </summary>
    internal sealed class FTRecorderModuleConfig
    {
        internal const string SourcePlayback = "playback";
        internal const string SourceVideo = "video";
        internal const string SourceWebcam = "webcam";
        internal const string SourceBabbleOsc = "babbleOsc";

        /// <summary>playback, video, webcam or babbleOsc.</summary>
        [JsonPropertyName("source")] public string Source { get; set; } = SourceWebcam;
        /// <summary>While true, everything the module sends to VRCFaceTracking is written to a recording.</summary>
        [JsonPropertyName("record")] public bool Record { get; set; }
        [JsonPropertyName("recordingsFolder")] public string RecordingsFolder { get; set; } = DefaultRecordings();
        [JsonPropertyName("playbackFile")] public string PlaybackFile { get; set; } = "";
        [JsonPropertyName("loop")] public bool Loop { get; set; } = true;
        [JsonPropertyName("videoFile")] public string VideoFile { get; set; } = "";
        /// <summary>ffmpeg's input format for the webcam: dshow (Windows), v4l2 (Linux) or avfoundation (macOS).</summary>
        [JsonPropertyName("webcamFormat")] public string WebcamFormat { get; set; } = OperatingSystem.IsWindows() ? "dshow" : OperatingSystem.IsMacOS() ? "avfoundation" : "v4l2";
        /// <summary>The camera's name (dshow), device path (v4l2) or index (avfoundation).</summary>
        [JsonPropertyName("webcamDevice")] public string WebcamDevice { get; set; } = "";
        [JsonPropertyName("ffmpegPath")] public string FfmpegPath { get; set; } = "ffmpeg";
        [JsonPropertyName("frameSize")] public int FrameSize { get; set; } = 640;
        [JsonPropertyName("fps")] public double Fps { get; set; } = 30;
        [JsonPropertyName("babbleModel")] public string BabbleModel { get; set; } = "";
        [JsonPropertyName("babbleLabels")] public string BabbleLabels { get; set; } = "";
        [JsonPropertyName("babbleCropScale")] public float BabbleCropScale { get; set; } = 1f;
        [JsonPropertyName("babbleOscPort")] public int BabbleOscPort { get; set; } = 8888;
        [JsonPropertyName("threads")] public int Threads { get; set; } = 2;

        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions { WriteIndented = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

        internal static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RiryTools", "FTRecorder", "module-config.json");

        private static string DefaultRecordings()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RiryTools FTRecorder");
        }

        /// <summary>Reads the file, writing the defaults first when there is none. error says what was wrong; defaults are then used.</summary>
        internal static FTRecorderModuleConfig Load(out string error)
        {
            error = null;
            string path = FilePath;
            try
            {
                if (!File.Exists(path))
                {
                    var defaults = new FTRecorderModuleConfig();
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllText(path, JsonSerializer.Serialize(defaults, Options));
                    return defaults;
                }
                return JsonSerializer.Deserialize<FTRecorderModuleConfig>(File.ReadAllText(path), Options) ?? new FTRecorderModuleConfig();
            }
            catch (Exception exception) when (exception is IOException || exception is JsonException || exception is UnauthorizedAccessException)
            {
                error = path + ": " + exception.Message;
                return new FTRecorderModuleConfig();
            }
        }

        /// <summary>True when other differs in anything that needs the source restarted (everything but record and recordingsFolder).</summary>
        internal bool SourceDiffers(FTRecorderModuleConfig other)
        {
            return Source != other.Source || PlaybackFile != other.PlaybackFile || Loop != other.Loop || VideoFile != other.VideoFile ||
                   WebcamFormat != other.WebcamFormat || WebcamDevice != other.WebcamDevice || FfmpegPath != other.FfmpegPath ||
                   FrameSize != other.FrameSize || Fps != other.Fps || BabbleModel != other.BabbleModel || BabbleLabels != other.BabbleLabels ||
                   BabbleCropScale != other.BabbleCropScale || BabbleOscPort != other.BabbleOscPort || Threads != other.Threads;
        }
    }
}

// ============================================================================
// GLOSSARY: FTRecorder module config
// ============================================================================
//
// FTRecorderModuleConfig (this file)  %LocalAppData%/RiryTools/FTRecorder/module-config.json; defaults written once.
//   source                   playback (a recording's expressions), video (a file through the face models, at its own
//                            speed), webcam (through ffmpeg), babbleOsc (the Babble app's OSC). Default webcam.
//   record                   Writes what the module sends to a recording in recordingsFolder
//                            (Documents/RiryTools FTRecorder by default).
//   playbackFile, loop       For playback; loop also restarts a video file.
//   videoFile                For video.
//   webcamFormat, webcamDevice  ffmpeg -f and the device: dshow "video=<name>", v4l2 path, avfoundation index.
//   ffmpegPath               ffmpeg (default "ffmpeg" on PATH). frameSize (640), fps (30).
//   babbleModel, babbleLabels, babbleCropScale  A Babble ONNX model for the mouth (video and webcam).
//   babbleOscPort            8888: where the Babble app sends (babbleOsc).
//   threads                  ONNX Runtime threads per model (2).
//   Load(out error)          The file, or defaults with error set. SourceDiffers(other): needs a source restart.
