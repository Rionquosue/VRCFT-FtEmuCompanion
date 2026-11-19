using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;

namespace Riry.RiryTools.FTRecorder
{
    /// <summary>The companion app's command line: record, process and replay.</summary>
    internal static class FTRecorderCompanion
    {
        private const string DefaultProxy = "9010:9000";

        private const string Usage = @"RiryTools FTRecorder companion

  record --out FILE [--proxy IN:OUT]
      Records both VRCFaceTracking's output and the Viseme and Voice VRChat makes from your microphone.
      --proxy IN:OUT   Default 9010:9000. Set VRCFaceTracking's OSC output port to IN; its packets are passed on
                       to VRChat on OUT (VRChat's input port) and recorded. VRChat finds the viseme listener
                       through OSCQuery (OSC must be on in VRChat).
      Stops with Ctrl+C.

  process --video FILE --out FILE [--ffmpeg PATH] [--babble-model PATH] [--babble-labels PATH]
          [--babble-crop SCALE] [--visemes ovrlipsync|classifier] [--ovrlipsync PATH] [--fps N] [--size N]
          [--threads N]
      Runs a recorded video through the face models (MediaPipe; Babble too when --babble-model is given) and its
      sound through OVRLipSync, VRChat's viseme engine. OVRLipSync.dll is taken from --ovrlipsync, else from your
      VRChat install (found through Steam); when it cannot be found or loaded, you are asked for its path.
      --visemes classifier uses FTRecorder's own classifier instead.

  replay --in FILE [--port PORT] [--loop]
      Sends recorded OSC, VRChat parameters and visemes to 127.0.0.1:PORT (9000) at their times. Expression
      frames are played by the VRCFaceTracking module's playback source instead.
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
                    case "record": return Record(options);
                    case "process": return Process(options);
                    case "replay": return Replay(options);
                    default: throw new ArgumentException("Unknown command \"" + args[0] + "\".");
                }
            }
            catch (Exception exception) when (exception is ArgumentException || exception is IOException || exception is InvalidOperationException || exception is InvalidDataException || exception is UnauthorizedAccessException)
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

        private static int Record(Dictionary<string, string> options)
        {
            string output = Required(options, "--out");
            string proxy = Optional(options, "--proxy") ?? DefaultProxy;
            string[] ports = proxy.Split(':');
            if (ports.Length != 2 || !int.TryParse(ports[0], out int inPort) || !int.TryParse(ports[1], out int outPort) ||
                inPort < 1 || inPort > 65535 || outPort < 1 || outPort > 65535)
                throw new ArgumentException("--proxy takes IN:OUT, two port numbers.");
            using CancellationTokenSource cancel = CtrlC();
            using var writer = new FTRecorderRecordingWriter(output, "vrcft-proxy:" + inPort + "->" + outPort + "; vrchat-visemes");
            FTRecorderProxy forwarder = null;
            FTRecorderVrchatListener listener = null;
            try
            {
                forwarder = new FTRecorderProxy(inPort, outPort, writer);
                Console.WriteLine("VRCFaceTracking: set its OSC output port to " + inPort + "; passing on to VRChat on " + outPort + ".");
                listener = new FTRecorderVrchatListener(writer);
                Console.WriteLine(listener.Discoverable ? "VRChat visemes: waiting for VRChat to send Viseme and Voice (OSC must be on in VRChat)." : "VRChat visemes: VRChat cannot find this listener, multicast DNS is unavailable.");
                Console.WriteLine("Recording to " + writer.Path + ". Ctrl+C stops.");
                while (!cancel.Token.WaitHandle.WaitOne(5000))
                {
                    Console.WriteLine(forwarder.Packets + " VRCFaceTracking packets, " + listener.Received + " viseme values");
                }
            }
            finally
            {
                forwarder?.Dispose();
                listener?.Dispose();
            }
            Console.WriteLine("Saved " + writer.Path);
            return 0;
        }

        private static int Process(Dictionary<string, string> options)
        {
            var settings = new FTRecorderVideoOptions
            {
                Video = Required(options, "--video"),
                Output = Required(options, "--out"),
                Ffmpeg = Optional(options, "--ffmpeg"),
                BabbleModel = Optional(options, "--babble-model"),
                BabbleLabels = Optional(options, "--babble-labels"),
                BabbleCropScale = (float)Number(options, "--babble-crop", 1, 0.1, 10),
                OvrLipSync = Optional(options, "--ovrlipsync"),
                Classifier = Visemes(options),
                // Without a console to type in, a missing OVRLipSync is an error straight away.
                AskForOvrLipSync = Console.IsInputRedirected ? null : AskPath,
                Fps = Number(options, "--fps", 30, 1, 240),
                Size = (int)Number(options, "--size", 640, 128, 1920),
                Threads = (int)Number(options, "--threads", Math.Max(1, Environment.ProcessorCount / 2), 1, 64),
            };
            using CancellationTokenSource cancel = CtrlC();
            try
            {
                Console.WriteLine(FTRecorderVideoProcessor.Process(settings, Console.WriteLine, cancel.Token));
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("Stopped; the recording so far is in " + settings.Output);
                return 1;
            }
            Console.WriteLine("Saved " + settings.Output);
            return 0;
        }

        // --visemes: ovrlipsync (default) or classifier.
        private static bool Visemes(Dictionary<string, string> options)
        {
            string engine = Optional(options, "--visemes") ?? "ovrlipsync";
            if (engine == "classifier") return true;
            if (engine == "ovrlipsync") return false;
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
            FTRecorderRecording recording = FTRecorderRecording.Load(Required(options, "--in"));
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
//   record --out FILE [--proxy IN:OUT]  FTRecorderProxy (DefaultProxy 9010:9000) and FTRecorderVrchatListener, both,
//                            into one recording until Ctrl+C; counts every 5 s.
//   process --video FILE --out FILE [...]  FTRecorderVideoProcessor (--ffmpeg, --babble-model, --babble-labels,
//                            --babble-crop 1, --visemes ovrlipsync|classifier, --ovrlipsync (else VRChat's copy),
//                            --fps 30, --size 640, --threads half the cores).
//   AskPath(reason)          Prints the error, then reads a path from the console; empty gives up. Not used without a
//                            console (input redirected): the error then ends the run.
//   replay --in FILE [--port 9000] [--loop]  FTRecorderReplay.
//   Exit codes               0 done, 1 bad arguments, a fault, or stopped.
