using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;

namespace Riry.RiryTools.FTRecorder
{
    /// <summary>The companion app's command line: process and replay (recording is the Recorder app).</summary>
    internal static class FTRecorderCompanion
    {
        private const string DefaultCable = "CABLE Input";

        private const string Usage = @"RiryTools FTRecorder companion

  process --video FILE --out FILE.mkv [--ffmpeg PATH] [--babble-model PATH] [--babble-labels PATH]
          [--babble-crop SCALE] [--visemes ovrlipsync|classifier] [--ovrlipsync PATH] [--cable NAME] [--fps N]
          [--size N] [--threads N]
      Runs a recorded video through the face models (MediaPipe; Babble too when --babble-model is given) into
      one Matroska file: the video itself, its sound as audio, the visemes as a subtitle track, and the whole recording
      (face data and visemes) attached. Until it is written the data is kept in FILE.mkv.recording.ftrec.jsonl.
      Visemes: while VRChat is running, the video's sound is played into VRChat through a virtual audio cable
      (--cable, default ""CABLE Input""; set VRChat's microphone to the cable's output) and the Viseme and Voice
      VRChat sends over OSC are recorded, at the video's own speed. When VRChat is not running, the sound goes
      through OVRLipSync: --ovrlipsync, else your VRChat install (found through Steam); when it cannot be found or
      loaded, you are asked for its path. --visemes ovrlipsync or classifier picks an engine whether VRChat runs
      or not; classifier is FTRecorder's own.

  replay --in FILE [--port PORT] [--loop] [--ffmpeg PATH]
      Sends recorded OSC, VRChat parameters and visemes to 127.0.0.1:PORT (9000) at their times. Expression
      frames are played by the VRCFaceTracking module's playback source instead.

  Every command that uses ffmpeg checks for it first and says how to install it when it is missing.
  Recording is done by the Recorder app (RiryTools.FTRecorder.Recorder).
";

        internal static int Main(string[] args)
        {
            if (args.Length == 0 || args[0] == "--help" || args[0] == "-h")
            {
                Console.WriteLine(Usage);
                return args.Length == 0 ? 1 : 0;
            }
            Dictionary<string, string> options;
            try
            {
                options = Options(args);
                switch (args[0])
                {
                    case "process": return Process(options);
                    case "replay": return Replay(options);
                    default: throw new ArgumentException("Unknown command \"" + args[0] + "\".");
                }
            }
            catch (Exception exception) when (exception is ArgumentException || exception is IOException || exception is InvalidOperationException || exception is InvalidDataException || exception is UnauthorizedAccessException || exception is PlatformNotSupportedException)
            {
                Console.Error.WriteLine(exception.Message);
                Console.Error.WriteLine("Run with --help for the commands.");
                return 1;
            }
        }

        // --name value pairs; flags (--vrchat, --loop) map to "true".
        private static Dictionary<string, string> Options(string[] args)
        {
            var flags = new HashSet<string> { "--loop" };
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 1; i < args.Length; i++)
            {
                string name = args[i];
                if (!name.StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Unexpected \"" + name + "\".");
                if (flags.Contains(name)) { options[name] = "true"; continue; }
                if (i + 1 >= args.Length) throw new ArgumentException(name + " needs a value.");
                options[name] = args[++i];
            }
            return options;
        }

        private static string Required(Dictionary<string, string> options, string name)
        {
            if (!options.TryGetValue(name, out string value) || string.IsNullOrWhiteSpace(value)) throw new ArgumentException(name + " is required.");
            return value;
        }

        private static string Optional(Dictionary<string, string> options, string name) => options.TryGetValue(name, out string value) ? value : null;

        private static double Number(Dictionary<string, string> options, string name, double fallback, double min, double max)
        {
            if (!options.TryGetValue(name, out string text)) return fallback;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || value < min || value > max)
                throw new ArgumentException(name + " must be a number from " + min + " to " + max + ".");
            return value;
        }

        private static CancellationTokenSource CtrlC()
        {
            var cancel = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cancel.Cancel();
            };
            return cancel;
        }

        private static int Process(Dictionary<string, string> options)
        {
            string output = Required(options, "--out");
            // The face and viseme data go to a working file next to the output until the .mkv is written.
            string data = output + ".recording.ftrec.jsonl";
            var settings = new FTRecorderVideoOptions
            {
                Video = Required(options, "--video"),
                Output = data,
                Ffmpeg = Optional(options, "--ffmpeg"),
                BabbleModel = Optional(options, "--babble-model"),
                BabbleLabels = Optional(options, "--babble-labels"),
                BabbleCropScale = (float)Number(options, "--babble-crop", 1, 0.1, 10),
                OvrLipSync = Optional(options, "--ovrlipsync"),
                Classifier = Optional(options, "--visemes") == "classifier",
                // Without a console to type in, a missing OVRLipSync is an error straight away.
                AskForOvrLipSync = Console.IsInputRedirected ? null : AskPath,
                Fps = Number(options, "--fps", 30, 1, 240),
                Size = (int)Number(options, "--size", 640, 128, 1920),
                Threads = (int)Number(options, "--threads", Math.Max(1, Environment.ProcessorCount / 2), 1, 64),
            };
            // An engine named with --visemes is used whether VRChat runs or not; otherwise VRChat decides.
            string engine = Visemes(options);
            if (!File.Exists(settings.Video)) throw new FileNotFoundException("The video was not found.", settings.Video);
            foreach (string path in new[] { output, data })
            {
                if (File.Exists(path)) throw new IOException("Already exists: " + path);
            }
            FTRecorderFfmpeg.Check(settings.Ffmpeg);
            using CancellationTokenSource cancel = CtrlC();
            try
            {
                if (engine == null && FTRecorderVrchatInstall.IsRunning())
                {
                    Console.WriteLine("VRChat is running: its visemes are recorded while the video's sound plays into it.");
                    settings.VrchatVisemes = FTRecorderVrchatVisemes.Capture(settings.Ffmpeg, settings.Video, Optional(options, "--cable") ?? DefaultCable, Console.WriteLine, cancel.Token);
                }
                Console.WriteLine(FTRecorderVideoProcessor.Process(settings, Console.WriteLine, cancel.Token));
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine(File.Exists(data) ? "Stopped; the recording so far is in " + data : "Stopped.");
                return 1;
            }
            Console.WriteLine("Writing " + output);
            string captions = FTRecorderCaptions.Build(FTRecorderRecording.Load(data));
            try
            {
                FTRecorderMatroska.WriteWithVideo(settings.Ffmpeg, data, settings.Video, captions, output);
            }
            catch (Exception exception) when (exception is InvalidOperationException || exception is IOException)
            {
                throw new InvalidOperationException(exception.Message + " The recording is kept in " + data + ".", exception);
            }
            File.Delete(data);
            Console.WriteLine("Saved " + output);
            return 0;
        }

        // --visemes: ovrlipsync or classifier; null when not given (VRChat's own visemes while it runs).
        private static string Visemes(Dictionary<string, string> options)
        {
            string engine = Optional(options, "--visemes");
            if (engine == null || engine == "classifier" || engine == "ovrlipsync") return engine;
            throw new ArgumentException("--visemes takes ovrlipsync or classifier.");
        }

        // The error, then a typed path; an empty line (or no console) gives up.
        private static string AskPath(string reason)
        {
            Console.Error.WriteLine("Error: " + reason);
            Console.Write("Path to OVRLipSync.dll (empty to stop): ");
            return Console.ReadLine();
        }

        private static int Replay(Dictionary<string, string> options)
        {
            string input = Required(options, "--in");
            if (File.Exists(input) && FTRecorderMatroska.IsMatroska(input)) FTRecorderFfmpeg.Check(Optional(options, "--ffmpeg"));
            FTRecorderRecording recording = FTRecorderRecording.Load(input, Optional(options, "--ffmpeg"));
            int port = (int)Number(options, "--port", 9000, 1, 65535);
            using CancellationTokenSource cancel = CtrlC();
            Console.WriteLine("Replaying " + recording.Entries.Count + " entries (" + recording.Duration.ToString("0.0") + " s) to 127.0.0.1:" + port + ". Ctrl+C stops.");
            FTRecorderReplay.Run(recording, port, options.ContainsKey("--loop"), cancel.Token);
            return 0;
        }
    }
}

// ============================================================================
// GLOSSARY: FTRecorder companion
// ============================================================================
//
// FTRecorderCompanion (this file)  Command line entry point.
//   process --video FILE --out FILE.mkv [...]  FTRecorderVideoProcessor into FILE.mkv.recording.ftrec.jsonl, then
//                            FTRecorderMatroska.WriteWithVideo (the video and its audio) with FTRecorderCaptions; the working
//                            file is deleted once the .mkv is written, kept if not. ffmpeg checked first (Check).
//                            Processing: (--ffmpeg, --babble-model, --babble-labels,
//                            --babble-crop 1, --visemes ovrlipsync|classifier (forces an engine), --ovrlipsync (else
//                            VRChat's copy), --cable (DefaultCable "CABLE Input": with VRChat running and no --visemes,
//                            FTRecorderVrchatVisemes first),
//                            --fps 30, --size 640, --threads half the cores).
//   AskPath(reason)          Prints the error, then reads a path from the console; empty gives up. Not used without a
//                            console (input redirected): the error then ends the run.
//   replay --in FILE [--port 9000] [--loop] [--ffmpeg PATH]  FTRecorderReplay (.mkv read through ffmpeg).
//   Exit codes               0 done, 1 bad arguments, a fault, or stopped.
