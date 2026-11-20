using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace Riry.RiryTools.FTRecorder
{
    /// <summary>
    /// The one-file form of a recording: a Matroska (.mkv) file with the microphone audio (Opus), the visemes as a
    /// subtitle track, and the whole recording (.ftrec.jsonl) attached. Written and read through the user's ffmpeg.
    /// </summary>
    internal static class FTRecorderMatroska
    {
        internal const string AttachmentName = "recording.ftrec.jsonl";
        internal const string AttachmentType = "application/x-ndjson";
        private static readonly byte[] Magic = { 0x1A, 0x45, 0xDF, 0xA3 };

        /// <summary>True when the file starts like a Matroska file.</summary>
        internal static bool IsMatroska(string path)
        {
            var head = new byte[4];
            using var file = File.OpenRead(path);
            return file.Read(head, 0, 4) == 4 && head[0] == Magic[0] && head[1] == Magic[1] && head[2] == Magic[2] && head[3] == Magic[3];
        }

        /// <summary>
        /// Combines the parts into output. audio is raw 16-bit mono PCM at sampleRate starting audioStart seconds into the
        /// recording (null or empty for none); captions is SRT text (null for none). Throws with ffmpeg's error on failure.
        /// </summary>
        internal static void Write(string ffmpeg, string recording, string audio, int sampleRate, double audioStart, string captions, string output)
        {
            var arguments = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-y" };
            int input = 0;
            int audioInput = -1, captionInput = -1;
            string captionFile = null;
            if (!string.IsNullOrEmpty(audio) && File.Exists(audio) && new FileInfo(audio).Length > 0)
            {
                arguments.AddRange(new[] { "-f", "s16le", "-ar", sampleRate.ToString(CultureInfo.InvariantCulture), "-ac", "1",
                                           "-itsoffset", Math.Max(0, audioStart).ToString("0.###", CultureInfo.InvariantCulture), "-i", audio });
                audioInput = input++;
            }
            if (!string.IsNullOrEmpty(captions))
            {
                captionFile = output + ".captions.srt";
                File.WriteAllText(captionFile, captions);
                arguments.AddRange(new[] { "-i", captionFile });
                captionInput = input++;
            }
            try
            {
                if (audioInput >= 0) arguments.AddRange(new[] { "-map", audioInput + ":a", "-c:a", "libopus", "-b:a", "48k" });
                if (captionInput >= 0) arguments.AddRange(new[] { "-map", captionInput + ":s", "-c:s", "srt", "-metadata:s:s:0", "title=Visemes" });
                arguments.AddRange(new[] { "-attach", recording, "-metadata:s:t:0", "mimetype=" + AttachmentType, "-metadata:s:t:0", "filename=" + AttachmentName });
                arguments.AddRange(new[] { "-f", "matroska", output });
                (int code, string errors) = Run(ffmpeg, arguments, 600000);
                if (code != 0) throw new InvalidOperationException("ffmpeg could not write " + output + ": " + errors);
            }
            finally
            {
                if (captionFile != null) File.Delete(captionFile);
            }
        }

        /// <summary>Copies the attached recording out of an .mkv into a new temporary file and returns its path.</summary>
        internal static string ExtractRecording(string ffmpeg, string mkv)
        {
            string folder = Path.Combine(Path.GetTempPath(), "RiryTools.FTRecorder." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            string target = Path.Combine(folder, AttachmentName);
            var arguments = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-dump_attachment:t:0", target, "-i", mkv, "-t", "0", "-f", "null", "-" };
            (int code, string errors) = Run(ffmpeg, arguments, 120000);
            if (!File.Exists(target))
            {
                Directory.Delete(folder, true);
                throw new InvalidDataException(mkv + " has no attached recording" + (code != 0 ? ": " + errors : "."));
            }
            return target;
        }

        // Runs ffmpeg to the end without a shell; its error output comes back with the exit code.
        private static (int code, string errors) Run(string ffmpeg, List<string> arguments, int timeout)
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
                throw new FileNotFoundException("ffmpeg was not found at \"" + start.FileName + "\". Install ffmpeg and set its path.", start.FileName, exception);
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
    }
}

// ============================================================================
// GLOSSARY: FTRecorder Matroska
// ============================================================================
//
// FTRecorderMatroska (this file)  One-file recordings (.mkv) through the user's ffmpeg.
//   Write(ffmpeg, recording, audio, sampleRate, audioStart, captions, output)  Opus 48 kb/s audio from raw s16le
//                            (offset by audioStart), an SRT subtitle track titled "Visemes", and the recording attached
//                            as recording.ftrec.jsonl (application/x-ndjson).
//   ExtractRecording(ffmpeg, mkv)  The attached recording copied to a new temporary folder; its path.
//   IsMatroska(path)         The file starts with the EBML magic 1A 45 DF A3.
