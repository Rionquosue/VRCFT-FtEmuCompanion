using System;
using System.IO;
using System.Threading;

namespace Riry.RiryTools.FTRecorder
{
    /// <summary>What one recording uses: the Recorder window's fields.</summary>
    internal sealed class FTRecorderRecorderSettings
    {
        internal string Output;
        internal int InPort = 9010;
        internal int OutPort = 9000;
        internal string Microphone;
        /// <summary>ffmpeg's input format for the microphone: dshow on Windows.</summary>
        internal string MicrophoneFormat = OperatingSystem.IsWindows() ? "dshow" : "pulse";
        internal string Ffmpeg;
        /// <summary>Empty for VRChat's copy.</summary>
        internal string OvrLipSync;
    }

    /// <summary>
    /// One recording: VRCFaceTracking's OSC output through the proxy, visemes (from VRChat's OSC while it runs, else
    /// OVRLipSync on the microphone) and the microphone, into working files next to the output, combined into one
    /// .mkv when stopped.
    /// </summary>
    internal sealed class FTRecorderRecorderSession
    {
        private readonly FTRecorderRecorderSettings settings;
        private readonly Action<string> log;
        private readonly string data;
        private readonly string audio;
        private readonly bool vrchat;
        private FTRecorderOvrLipSync lipSync;
        private FTRecorderRecordingWriter writer;
        private FTRecorderProxy forwarder;
        private FTRecorderVrchatListener listener;
        private FTRecorderMicrophone microphone;

        private FTRecorderRecorderSession(FTRecorderRecorderSettings settings, Action<string> log)
        {
            this.settings = settings;
            this.log = log;
            // The working files sit next to the output until it is written, so a crash keeps what was recorded.
            data = settings.Output + ".recording.ftrec.jsonl";
            audio = settings.Output + ".microphone.s16le";
            vrchat = FTRecorderVrchatInstall.IsRunning();
        }

        /// <summary>
        /// Starts recording. Blocks (ffmpeg and OVRLipSync are checked first), so it is called off the window's thread.
        /// ask is given the error when OVRLipSync cannot be found or loaded and returns a path, or null to stop.
        /// </summary>
        internal static FTRecorderRecorderSession Start(FTRecorderRecorderSettings settings, Func<string, string> ask, Action<string> log)
        {
            if (string.IsNullOrWhiteSpace(settings.Output)) throw new ArgumentException("Choose where the recording goes.");
            if (settings.InPort == settings.OutPort) throw new ArgumentException("The two ports must differ.");
            var session = new FTRecorderRecorderSession(settings, log);
            try
            {
                session.Open(ask);
            }
            catch
            {
                session.Close();
                throw;
            }
            return session;
        }

        private void Open(Func<string, string> ask)
        {
            // Checked first, so nothing is started for a recording that cannot be written.
            foreach (string path in new[] { settings.Output, data, audio })
            {
                if (File.Exists(path)) throw new IOException("Already exists: " + path);
            }
            string folder = Path.GetDirectoryName(Path.GetFullPath(settings.Output));
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
            FTRecorderFfmpeg.Check(settings.Ffmpeg);
            // The microphone is recorded either way; without VRChat it also feeds OVRLipSync. Both are found first.
            if (!vrchat)
            {
                lipSync = FTRecorderVisemeGenerator.Open(settings.OvrLipSync, ask, out string loaded);
                log("VRChat is not running: visemes from your microphone through OVRLipSync (" + loaded + ").");
            }
            if (string.IsNullOrWhiteSpace(settings.Microphone))
                throw new ArgumentException("No microphone found (ffmpeg -list_devices true -f dshow -i dummy lists them).");
            FTRecorderFfmpeg capture = FTRecorderFfmpeg.OpenMicrophone(settings.Ffmpeg, settings.MicrophoneFormat, settings.Microphone);
            try
            {
                log("Microphone: " + settings.Microphone);
                writer = new FTRecorderRecordingWriter(data, "vrcft-proxy:" + settings.InPort + "->" + settings.OutPort + "; " + (vrchat ? "vrchat-visemes" : "ovrlipsync-visemes") + "; microphone");
                forwarder = new FTRecorderProxy(settings.InPort, settings.OutPort, writer);
                log("VRCFaceTracking: set its OSC output port to " + settings.InPort + "; passing on to " + settings.OutPort + ".");
                if (vrchat)
                {
                    listener = new FTRecorderVrchatListener(() => writer.Now, writer.WriteParameter);
                    log(listener.Discoverable ? "VRChat is running: recording the Viseme and Voice it sends (OSC must be on in VRChat)." : "VRChat is running, but it cannot find the viseme listener: multicast DNS is unavailable.");
                }
                microphone = new FTRecorderMicrophone(capture, audio, lipSync, writer);
                capture = null;
                log("Recording to " + settings.Output);
            }
            finally
            {
                capture?.Dispose();
            }
        }

        /// <summary>The counts so far, for the window's status line.</summary>
        internal string Status()
        {
            // Read once: Stop clears them from another thread.
            FTRecorderProxy f = forwarder;
            FTRecorderVrchatListener l = listener;
            FTRecorderMicrophone m = microphone;
            if (f == null || m == null) return "";
            return Interlocked.Read(ref f.Packets) + " VRCFaceTracking packets, " + (l != null ? Interlocked.Read(ref l.Received) + " VRChat viseme values, " : "") +
                   (Interlocked.Read(ref m.Blocks) / 100.0).ToString("0") + " s of microphone";
        }

        /// <summary>
        /// Stops recording and writes the .mkv; the working files are deleted once it is written. Blocks (ffmpeg encodes),
        /// so it is called off the window's thread. Throws, keeping the working files, when the .mkv cannot be written.
        /// </summary>
        internal void Stop()
        {
            double audioStart = microphone?.AudioStart ?? -1;
            Close();
            log("Writing " + settings.Output);
            string captions = FTRecorderCaptions.Build(FTRecorderRecording.Load(data));
            try
            {
                FTRecorderMatroska.WriteWithRawAudio(settings.Ffmpeg, data, audio, FTRecorderMicrophone.SampleRate, audioStart, captions, settings.Output);
            }
            catch (Exception exception) when (exception is InvalidOperationException || exception is IOException)
            {
                throw new InvalidOperationException(exception.Message + " The recording is kept in " + data + " and " + audio + ".", exception);
            }
            File.Delete(data);
            File.Delete(audio);
            log("Saved " + settings.Output);
        }

        // Everything started is stopped; the writer is closed last, after the threads that write to it.
        private void Close()
        {
            forwarder?.Dispose();
            listener?.Dispose();
            microphone?.Dispose();
            writer?.Dispose();
            lipSync?.Dispose();
            forwarder = null;
            listener = null;
            microphone = null;
            writer = null;
            lipSync = null;
        }
    }
}

// ============================================================================
// GLOSSARY: FTRecorder Recorder session
// ============================================================================
//
// FTRecorderRecorderSettings (this file)  Output (.mkv), InPort 9010 (VRCFaceTracking's OSC output port), OutPort 9000
//                            (where its packets are passed on: VRChat, or FTEmulator), Microphone (a dshow name),
//                            MicrophoneFormat (dshow; pulse off Windows), Ffmpeg (null: ffmpeg on PATH), OvrLipSync
//                            (empty: VRChat's copy).
// FTRecorderRecorderSession (this file)  One recording of the Recorder app.
//   Start(settings, ask, log)  Checks the output and working files (Output.recording.ftrec.jsonl,
//                            Output.microphone.s16le) do not exist, ffmpeg (Check); VRChat not running (checked once,
//                            here) -> OVRLipSync through FTRecorderVisemeGenerator.Open with ask; then FTRecorderProxy,
//                            FTRecorderVrchatListener (VRChat running) and FTRecorderMicrophone. Blocking.
//   Status()                 Packets, VRChat viseme values and seconds of microphone so far.
//   Stop()                   Closes everything, then FTRecorderCaptions + FTRecorderMatroska.WriteWithRawAudio; deletes
//                            the working files once the .mkv is written, keeps them (and says where) if not. Blocking.
