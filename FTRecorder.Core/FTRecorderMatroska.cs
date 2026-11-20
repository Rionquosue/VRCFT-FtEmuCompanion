using System;
using System.Collections.Generic;
using System.IO;

namespace Riry.RiryTools.FTRecorder
{
    /// <summary>
    /// The one-file form of a recording: a Matroska (.mkv) file with the whole recording (.ftrec.jsonl) attached, written
    /// by the Recorder and process (Python, ftrecorder/matroska.py). The module reads it through the user's ffmpeg.
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

        /// <summary>Copies the attached recording out of an .mkv into a new temporary file and returns its path.</summary>
        internal static string ExtractRecording(string ffmpeg, string mkv)
        {
            string folder = Path.Combine(Path.GetTempPath(), "RiryTools.FTRecorder." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            string target = Path.Combine(folder, AttachmentName);
            var arguments = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-dump_attachment:t:0", target, "-i", mkv, "-t", "0", "-f", "null", "-" };
            (int code, string errors) = FTRecorderFfmpeg.Run(ffmpeg, arguments, 120000);
            if (!File.Exists(target))
            {
                Directory.Delete(folder, true);
                throw new InvalidDataException(mkv + " has no attached recording" + (code != 0 ? ": " + errors : "."));
            }
            return target;
        }

    }
}

// ============================================================================
// GLOSSARY: FTRecorder Matroska
// ============================================================================
//
// FTRecorderMatroska (this file)  Reading one-file recordings (.mkv) through the user's ffmpeg (playback source).
//   ExtractRecording(ffmpeg, mkv)  The attached recording (recording.ftrec.jsonl) copied to a new temporary folder; its
//                            path.
//   IsMatroska(path)         The file starts with the EBML magic 1A 45 DF A3.
