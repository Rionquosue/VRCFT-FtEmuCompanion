using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace Riry.RiryTools.FTRecorder
{
    /// <summary>
    /// Video frames through the user's own ffmpeg (not bundled). ffmpeg is started with an argument list,
    /// never through a shell, and writes raw data to its standard output: frames as RGB, fitted into a square with black
    /// borders so their size is known in advance.
    /// </summary>
    internal sealed class FTRecorderFfmpeg : IDisposable
    {
        private readonly Process process;
        private readonly Stream output;
        private readonly StringBuilder errors = new StringBuilder();
        private readonly Thread errorReader;
        private bool disposed;

        private FTRecorderFfmpeg(string ffmpeg, List<string> arguments)
        {
            var start = new ProcessStartInfo(string.IsNullOrWhiteSpace(ffmpeg) ? "ffmpeg" : ffmpeg)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
            };
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            try
            {
                process = Process.Start(start) ?? throw new InvalidOperationException("ffmpeg did not start.");
            }
            catch (System.ComponentModel.Win32Exception exception)
            {
                throw new FileNotFoundException(NotFound(start.FileName), start.FileName, exception);
            }
            output = process.StandardOutput.BaseStream;
            errorReader = new Thread(ReadErrors) { IsBackground = true, Name = "RiryTools FTRecorder ffmpeg errors" };
            errorReader.Start();
        }

        private void ReadErrors()
        {
            try
            {
                string line;
                while ((line = process.StandardError.ReadLine()) != null)
                {
                    lock (errors)
                    {
                        if (errors.Length < 8192) errors.AppendLine(line);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException || exception is ObjectDisposedException || exception is InvalidOperationException)
            {
                // ffmpeg was closed.
            }
        }

        /// <summary>The message for an ffmpeg that cannot be started: what was looked for, and how to fix it.</summary>
        internal static string NotFound(string ffmpeg)
        {
            return "ffmpeg was not found (looked for \"" + (string.IsNullOrWhiteSpace(ffmpeg) ? "ffmpeg" : ffmpeg) + "\"). Install ffmpeg from " +
                   "https://ffmpeg.org/download.html and add it to PATH, or give its full path (--ffmpeg, or ffmpegPath in the module's settings).";
        }

        /// <summary>Throws FileNotFoundException with NotFound's message when ffmpeg cannot be started.</summary>
        internal static void Check(string ffmpeg)
        {
            (int code, string _) = Run(ffmpeg, new List<string> { "-hide_banner", "-version" }, 15000);
            if (code != 0) throw new FileNotFoundException(NotFound(ffmpeg), ffmpeg);
        }

        /// <summary>Runs ffmpeg to the end without a shell; its exit code and error output come back. Throws when it cannot start.</summary>
        internal static (int code, string errors) Run(string ffmpeg, List<string> arguments, int timeout)
        {
            var start = new ProcessStartInfo(string.IsNullOrWhiteSpace(ffmpeg) ? "ffmpeg" : ffmpeg)
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            Process process;
            try
            {
                process = Process.Start(start) ?? throw new InvalidOperationException("ffmpeg did not start.");
            }
            catch (System.ComponentModel.Win32Exception exception)
            {
                throw new FileNotFoundException(NotFound(ffmpeg), start.FileName, exception);
            }
            using (process)
            {
                process.StandardOutput.ReadToEndAsync();
                string errors = process.StandardError.ReadToEnd();
                if (!process.WaitForExit(timeout))
                {
                    process.Kill(true);
                    return (-1, "ffmpeg took too long. " + errors.Trim());
                }
                return (process.ExitCode, errors.Trim());
            }
        }

        /// <summary>What ffmpeg has written to its error output so far (at most 8 KB).</summary>
        internal string Errors
        {
            get { lock (errors) return errors.ToString().Trim(); }
        }

        /// <summary>
        /// Frames of a video file, a webcam or any other ffmpeg input. input holds the arguments before "-i" and the input
        /// itself: for a file, just its path. realTime paces a file at its own speed (ffmpeg's -re) for live use.
        /// </summary>
        internal static FTRecorderFfmpeg OpenVideo(string ffmpeg, IList<string> inputOptions, string input, int size, double fps, bool realTime)
        {
            // Standard input stays open: "q" on it is how ffmpeg is asked to stop.
            var arguments = new List<string> { "-hide_banner", "-loglevel", "error" };
            if (realTime) arguments.Add("-re");
            arguments.AddRange(inputOptions);
            arguments.Add("-i");
            arguments.Add(input);
            arguments.Add("-an");
            arguments.Add("-vf");
            string rate = fps > 0 ? "fps=" + fps.ToString(System.Globalization.CultureInfo.InvariantCulture) + "," : "";
            arguments.Add(rate + "scale=" + size + ":" + size + ":force_original_aspect_ratio=decrease,pad=" + size + ":" + size + ":(ow-iw)/2:(oh-ih)/2");
            arguments.AddRange(new[] { "-pix_fmt", "rgb24", "-f", "rawvideo", "-" });
            return new FTRecorderFfmpeg(ffmpeg, arguments);
        }

        /// <summary>Reads the next frame into frame (size × size). False at the end of the input or when ffmpeg stops.</summary>
        internal bool ReadFrame(FTRecorderFrame frame)
        {
            int filled = 0;
            while (filled < frame.Rgb.Length)
            {
                int read;
                try
                {
                    read = output.Read(frame.Rgb, filled, frame.Rgb.Length - filled);
                }
                catch (Exception exception) when (exception is IOException || exception is ObjectDisposedException)
                {
                    return false;
                }
                if (read <= 0) return false;
                filled += read;
            }
            return true;
        }

        public void Dispose()
        {
            // May be called from another thread to end a blocked read, then again by the reader's own using.
            lock (errors)
            {
                if (disposed) return;
                disposed = true;
            }
            try
            {
                if (!process.HasExited)
                {
                    // "q" asks ffmpeg to stop cleanly (a webcam is released); it is killed if it does not.
                    try { process.StandardInput.Write('q'); process.StandardInput.Flush(); }
                    catch (IOException) { }
                    if (!process.WaitForExit(2000)) process.Kill(true);
                }
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }
            output.Dispose();
            errorReader.Join(1000);
            process.Dispose();
        }
    }
}

// ============================================================================
// GLOSSARY: FTRecorder ffmpeg
// ============================================================================
//
// FTRecorderFfmpeg (this file)  The user's ffmpeg, started without a shell, raw data on standard output.
//   OpenVideo(ffmpeg, inputOptions, input, size, fps, realTime)  RGB frames size × size (fitted, black borders), at
//                            fps when above 0. inputOptions go before -i (a webcam's "-f dshow"). realTime adds -re.
//   ReadFrame(frame)         The next frame; false at the end.
//   Errors                   ffmpeg's error output so far (8 KB at most).
//   NotFound(ffmpeg)         The not-found message: what was looked for; install from ffmpeg.org, add to PATH or give
//                            the path (--ffmpeg, ffmpegPath). Every failed start throws FileNotFoundException with it.
//   Check(ffmpeg)            Runs ffmpeg -version; throws with NotFound's message when it fails.
//   Run(ffmpeg, arguments, timeout)  Runs to the end; exit code and error output.
//   Dispose()                Once, from any thread: sends "q", waits 2 s, then kills.
