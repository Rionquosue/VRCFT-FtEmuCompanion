using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.Extensions.Logging;
using VRCFaceTracking;
using VRCFaceTracking.Core.Params.Expressions;
using VRCFaceTracking.Core.Types;

namespace Riry.RiryTools.FTRecorder
{
    /// <summary>
    /// The VRCFaceTracking module. Takes face data from one source (a recording, a video file, a webcam, or the Babble
    /// app), hands it to VRCFaceTracking, and while "record" is on writes what it hands over to a recording. Settings are
    /// in FTRecorderModuleConfig's file and are re-read when it changes. This type refers to nothing from ONNX Runtime,
    /// so VRCFaceTracking can load it before the module's own DLLs are reachable.
    /// </summary>
    public sealed class FTRecorderModule : ExtTrackingModule
    {
        private const string Name = "RiryTools FTRecorder";

        private readonly object gate = new object();
        private readonly float[] unified = new float[FTRecorderExpressionMap.UnifiedNames.Length];
        private readonly float[] eye = FTRecorderSource.NeutralEyes();
        private int[] targets;
        private FTRecorderModuleConfig config;
        private FTRecorderSource source;
        private FTRecorderRecordingWriter recorder;
        private FileSystemWatcher watcher;
        private Timer reload;
        private long seen;
        private string lastStatus;
        private bool eyesClaimed;

        public FTRecorderModule()
        {
            FTRecorderModuleDependencies.Install();
        }

        public override (bool SupportsEye, bool SupportsExpression) Supported => (true, true);

        public override (bool eyeSuccess, bool expressionSuccess) Initialize(bool eyeAvailable, bool expressionAvailable)
        {
            ModuleInformation = new ModuleMetadata { Name = Name, StaticImages = new List<Stream>() };
            targets = new int[FTRecorderExpressionMap.UnifiedNames.Length];
            for (int i = 0; i < targets.Length; i++)
            {
                targets[i] = Enum.TryParse(FTRecorderExpressionMap.UnifiedNames[i], out UnifiedExpressions expression) ? (int)expression : -1;
            }
            config = FTRecorderModuleConfig.Load(out string error);
            if (error != null) Logger.LogWarning("Settings could not be read, defaults used: {error}", error);
            Logger.LogInformation("Settings file: {path}", FTRecorderModuleConfig.FilePath);
            if (!expressionAvailable && !eyeAvailable) return (false, false);
            lock (gate)
            {
                StartSource();
                ApplyRecording(null);
            }
            eyesClaimed = eyeAvailable && config.Source != FTRecorderModuleConfig.SourceBabbleOsc;
            Watch();
            return (eyesClaimed, expressionAvailable);
        }

        private void StartSource()
        {
            source?.Dispose();
            seen = 0;
            switch (config.Source)
            {
                case FTRecorderModuleConfig.SourcePlayback: source = new FTRecorderPlaybackSource(config.PlaybackFile, config.Loop, config.FfmpegPath); break;
                case FTRecorderModuleConfig.SourceVideo: source = new FTRecorderCameraSource(config, false); break;
                case FTRecorderModuleConfig.SourceBabbleOsc: source = new FTRecorderBabbleOscSource(config.BabbleOscPort); break;
                case FTRecorderModuleConfig.SourceWebcam: source = new FTRecorderCameraSource(config, true); break;
                default:
                    source = null;
                    Logger.LogWarning("Unknown source \"{source}\": use playback, video, webcam or babbleOsc.", config.Source);
                    return;
            }
            source.Start();
            Logger.LogInformation("Source: {source}", config.Source);
        }

        // Starts or stops the recording to match the settings. previous is the settings before a change (null at start).
        private void ApplyRecording(FTRecorderModuleConfig previous)
        {
            bool folderChanged = previous != null && previous.RecordingsFolder != config.RecordingsFolder;
            if (recorder != null && (!config.Record || folderChanged))
            {
                Logger.LogInformation("Recording saved: {path}", recorder.Path);
                recorder.Dispose();
                recorder = null;
            }
            if (config.Record && recorder == null)
            {
                try
                {
                    Directory.CreateDirectory(config.RecordingsFolder);
                    recorder = new FTRecorderRecordingWriter(FTRecorderRecordingWriter.NewPath(config.RecordingsFolder, "module"), "module:" + config.Source);
                    Logger.LogInformation("Recording to {path}", recorder.Path);
                }
                catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is ArgumentException)
                {
                    Logger.LogError("Could not start recording in {folder}: {error}", config.RecordingsFolder, exception.Message);
                }
            }
        }

        private void Watch()
        {
            string folder = Path.GetDirectoryName(FTRecorderModuleConfig.FilePath);
            try
            {
                watcher = new FileSystemWatcher(folder, Path.GetFileName(FTRecorderModuleConfig.FilePath))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                };
                // Editors write a file in several steps: settle for half a second before reading.
                reload = new Timer(_ => Reload(), null, Timeout.Infinite, Timeout.Infinite);
                FileSystemEventHandler changed = (_, _) => reload.Change(500, Timeout.Infinite);
                watcher.Changed += changed;
                watcher.Created += changed;
                watcher.Renamed += (_, _) => reload.Change(500, Timeout.Infinite);
                watcher.EnableRaisingEvents = true;
            }
            catch (Exception exception) when (exception is IOException || exception is ArgumentException || exception is PlatformNotSupportedException)
            {
                Logger.LogWarning("Settings changes will not be noticed until VRCFaceTracking restarts: {error}", exception.Message);
            }
        }

        private void Reload()
        {
            FTRecorderModuleConfig next = FTRecorderModuleConfig.Load(out string error);
            if (error != null)
            {
                Logger.LogWarning("Settings not applied: {error}", error);
                return;
            }
            lock (gate)
            {
                if (config == null) return;
                FTRecorderModuleConfig previous = config;
                config = next;
                if (next.SourceDiffers(previous)) StartSource();
                ApplyRecording(previous);
            }
        }

        public override void Update()
        {
            lock (gate)
            {
                if (source != null)
                {
                    string status = source.Status;
                    if (status != lastStatus)
                    {
                        lastStatus = status;
                        Logger.LogInformation("{status}", status);
                    }
                    if (source.TryRead(ref seen, unified, eye))
                    {
                        Send();
                        recorder?.WriteExpressions(recorder.Now, unified, eye);
                    }
                }
            }
            Thread.Sleep(10);
        }

        private void Send()
        {
            var shapes = UnifiedTracking.Data.Shapes;
            for (int i = 0; i < unified.Length; i++)
            {
                int target = targets[i];
                if (target >= 0 && target < shapes.Length) shapes[target].Weight = unified[i];
            }
            if (!eyesClaimed) return;
            var eyes = UnifiedTracking.Data.Eye;
            eyes.Left.Gaze = new Vector2(eye[FTRecorderExpressionMap.LeftGazeX], eye[FTRecorderExpressionMap.LeftGazeY]);
            eyes.Right.Gaze = new Vector2(eye[FTRecorderExpressionMap.RightGazeX], eye[FTRecorderExpressionMap.RightGazeY]);
            eyes.Left.Openness = eye[FTRecorderExpressionMap.LeftOpenness];
            eyes.Right.Openness = eye[FTRecorderExpressionMap.RightOpenness];
        }

        public override void Teardown()
        {
            watcher?.Dispose();
            reload?.Dispose();
            lock (gate)
            {
                source?.Dispose();
                source = null;
                if (recorder != null)
                {
                    Logger.LogInformation("Recording saved: {path}", recorder.Path);
                    recorder.Dispose();
                    recorder = null;
                }
                config = null;
            }
        }
    }
}

// ============================================================================
// GLOSSARY: FTRecorder module
// ============================================================================
//
// FTRecorderModule (this file)  The VRCFaceTracking module ("RiryTools FTRecorder"). Supports eyes and expressions;
//                            claims eyes unless the source is babbleOsc.
//   Initialize               Maps Unified Expression names to VRCFaceTracking's enum, reads the settings, starts the
//                            source and (when record is on) the recording, watches the settings file.
//   Update()                 Every 10 ms: the source's newest frame into UnifiedTracking.Data (shapes; gaze and
//                            openness when eyes are claimed), and into the recording; source status changes logged.
//   Reload()                 500 ms after the settings file changes: restarts the source when its settings changed,
//                            starts or stops recording. A file that does not parse is ignored (logged).
//   Teardown()               Stops the watcher, the source and the recording (its path logged).
