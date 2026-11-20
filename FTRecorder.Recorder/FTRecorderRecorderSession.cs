using System;
using System.Globalization;
using System.IO;
using System.Threading;

namespace Riry.RiryTools.FTRecorder
{
    /// <summary>What one recording uses: the Recorder window's fields.</summary>
    internal sealed class FTRecorderRecorderSettings
    {
        /// <summary>The output folder; the recording is named there when it starts (OutputName).</summary>
        internal string Folder;
        internal string Microphone;
        /// <summary>ffmpeg's input format for the microphone: dshow on Windows.</summary>
        internal string MicrophoneFormat = OperatingSystem.IsWindows() ? "dshow" : "pulse";
        internal string Ffmpeg;
        /// <summary>Empty for VRChat's copy.</summary>
        internal string OvrLipSync;
    }

    /// <summary>
    /// One recording: VRCFaceTracking's OSC output from the running proxy (the relay), visemes (from VRChat's OSC while it runs, else
    /// OVRLipSync on the microphone) and the microphone, into working files next to the output, combined into one
    /// .mkv when stopped.
    /// </summary>
    internal sealed class FTRecorderRecorderSession
    {
        private readonly FTRecorderRecorderSettings settings;
        private readonly Action<string> log;
        private readonly string output;
        private readonly string data;
        private readonly string audio;
        private readonly bool vrchat;
        private FTRecorderOvrLipSync lipSync;
        private FTRecorderRecordingWriter writer;
        private readonly FTRecorderProxy relay;
        private long packetsAtStart;
        private bool recordingRelay;
        private FTRecorderVrchatListener listener;
        private FTRecorderMicrophone microphone;

        private FTRecorderRecorderSession(FTRecorderRecorderSettings settings, FTRecorderProxy relay, Action<string> log)
        {
            this.settings = settings;
            this.relay = relay;
            this.log = log;
            output = OutputName(settings.Folder, DateTime.Now);
            // The working files sit next to the output until it is written, so a crash keeps what was recorded.
            data = WorkingData(output);
            audio = WorkingAudio(output);
            vrchat = FTRecorderVrchatInstall.IsRunning();
        }

        /// <summary>
        /// Starts recording what the running relay passes on. Blocks (ffmpeg and OVRLipSync are checked first), so it is
        /// called off the window's thread. ask is given the error when OVRLipSync cannot be found or loaded and returns a
        /// path, or null to stop. The relay is not stopped by the session.
        /// </summary>
        internal static FTRecorderRecorderSession Start(FTRecorderRecorderSettings settings, FTRecorderProxy relay, Func<string, string> ask, Action<string> log)
        {
            if (string.IsNullOrWhiteSpace(settings.Folder)) throw new ArgumentException("Choose the output folder.");
            if (relay == null) throw new InvalidOperationException("The relay is not running: see the error above, or change the ports.");
            var session = new FTRecorderRecorderSession(settings, relay, log);
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
            Directory.CreateDirectory(settings.Folder);
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
                writer = new FTRecorderRecordingWriter(data, "vrcft-proxy:" + relay.InPort + "->" + relay.OutPort + "; " + (vrchat ? "vrchat-visemes" : "ovrlipsync-visemes") + "; microphone");
                packetsAtStart = Interlocked.Read(ref relay.Packets);
                relay.SetWriter(writer);
                recordingRelay = true;
                if (vrchat)
                {
                    listener = new FTRecorderVrchatListener(() => writer.Now, writer.WriteParameter);
                    log(listener.Discoverable ? "VRChat is running: recording the Viseme and Voice it sends (OSC must be on in VRChat)." : "VRChat is running, but it cannot find the viseme listener: multicast DNS is unavailable.");
                }
                microphone = new FTRecorderMicrophone(capture, audio, lipSync, writer);
                capture = null;
                log("Recording to " + output);
            }
            finally
            {
                capture?.Dispose();
            }
        }

        /// <summary>
        /// FTRecording-HHmm-ddMMyy.mkv in folder (24-hour time, the date as day, month, two-digit year), with " (2)",
        /// " (3)" and on added when that name, or its working files, already exist.
        /// </summary>
        internal static string OutputName(string folder, DateTime time)
        {
            string name = "FTRecording-" + time.ToString("HHmm", CultureInfo.InvariantCulture) + "-" + time.ToString("ddMMyy", CultureInfo.InvariantCulture);
            for (int number = 1; ; number++)
            {
                string path = Path.Combine(folder, name + (number == 1 ? "" : " (" + number + ")") + ".mkv");
                if (!File.Exists(path) && !File.Exists(WorkingData(path)) && !File.Exists(WorkingAudio(path))) return path;
            }
        }

        private static string WorkingData(string output) => output + ".recording.ftrec.jsonl";

        private static string WorkingAudio(string output) => output + ".microphone.s16le";

        /// <summary>The counts so far, for the window's status line.</summary>
        internal string Status()
        {
            // Read once: Stop clears them from another thread.
            FTRecorderVrchatListener l = listener;
            FTRecorderMicrophone m = microphone;
            if (m == null) return "";
            return "Recording: " + (Interlocked.Read(ref relay.Packets) - packetsAtStart) + " VRCFaceTracking packets, " + (l != null ? Interlocked.Read(ref l.Received) + " VRChat viseme values, " : "") +
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
            log("Writing " + output);
            string captions = FTRecorderCaptions.Build(FTRecorderRecording.Load(data));
            try
            {
                FTRecorderMatroska.WriteWithRawAudio(settings.Ffmpeg, data, audio, FTRecorderMicrophone.SampleRate, audioStart, captions, output);
            }
            catch (Exception exception) when (exception is InvalidOperationException || exception is IOException)
            {
                throw new InvalidOperationException(exception.Message + " The recording is kept in " + data + " and " + audio + ".", exception);
            }
            File.Delete(data);
            File.Delete(audio);
            log("Saved " + output);
        }

        // Everything started is stopped; the writer is closed last, after the threads that write to it.
        private void Close()
        {
            if (recordingRelay) relay.SetWriter(null);
            recordingRelay = false;
            listener?.Dispose();
            microphone?.Dispose();
            writer?.Dispose();
            lipSync?.Dispose();
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
// FTRecorderRecorderSettings (this file)  Folder (the output folder), Microphone (a dshow name),
//                            MicrophoneFormat (dshow; pulse off Windows), Ffmpeg (null: ffmpeg on PATH), OvrLipSync
//                            (empty: VRChat's copy).
// FTRecorderRecorderSession (this file)  One recording of the Recorder app.
//   Start(settings, relay, ask, log)  Records what the running relay (FTRecorderProxy, kept running) passes on, to
//                            OutputName(Folder, now) (the folder is created), working files NAME.mkv.recording.ftrec.jsonl
//                            and NAME.mkv.microphone.s16le. Checks ffmpeg (Check); VRChat not running (checked once,
//                            here) -> OVRLipSync through FTRecorderVisemeGenerator.Open with ask; then the relay's writer,
//                            FTRecorderVrchatListener (VRChat running) and FTRecorderMicrophone. Blocking.
//   OutputName(folder, time)  FTRecording-HHmm-ddMMyy.mkv (24-hour, no separators); " (2)", " (3)"... when it or its
//                            working files exist.
//   Status()                 Packets recorded, VRChat viseme values and seconds of microphone so far.
//   Stop()                   Detaches the writer from the relay (which keeps relaying), closes the rest, then FTRecorderCaptions + FTRecorderMatroska.WriteWithRawAudio; deletes
//                            the working files once the .mkv is written, keeps them (and says where) if not. Blocking.
