using System;
using System.Collections.Generic;
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
        /// Combines a recording with raw 16-bit mono PCM audio at sampleRate (starting audioStart seconds into the recording;
        /// null or empty for none) and SRT captions (null for none) into output. Throws with ffmpeg's error on failure.
        /// </summary>
        internal static void WriteWithRawAudio(string ffmpeg, string recording, string audio, int sampleRate, double audioStart, string captions, string output)
        {
            bool hasAudio = !string.IsNullOrEmpty(audio) && File.Exists(audio) && new FileInfo(audio).Length > 0;
            var media = hasAudio
                ? new List<string> { "-f", "s16le", "-ar", sampleRate.ToString(CultureInfo.InvariantCulture), "-ac", "1",
                                     "-itsoffset", Math.Max(0, audioStart).ToString("0.###", CultureInfo.InvariantCulture), "-i", audio }
                : new List<string>();
            Write(ffmpeg, recording, media, false, hasAudio, "48k", captions, output);
        }

        /// <summary>
        /// Like WriteWithRawAudio, with a video file's first video track copied unchanged and its first audio track (none
        /// when it has none) as Opus, both from the video's start.
        /// </summary>
        internal static void WriteWithVideo(string ffmpeg, string recording, string video, string captions, string output)
        {
            // Asked first: mapping a missing audio track fails the whole write.
            (int code, string _) = FTRecorderFfmpeg.Run(ffmpeg, new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-i", video, "-map", "0:a:0", "-t", "0", "-f", "null", "-" }, 60000);
            Write(ffmpeg, recording, new List<string> { "-i", video }, true, code == 0, "96k", captions, output);
        }

        private static void Write(string ffmpeg, string recording, List<string> media, bool hasVideo, bool hasAudio, string bitrate, string captions, string output)
        {
            var arguments = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-y" };
            int input = 0;
            int mediaIndex = -1, captionIndex = -1;
            string captionFile = null;
            // A Matroska file needs a track: with no picture, sound or visemes, 20 ms of silence carries the attachment.
            if (!hasVideo && !hasAudio && string.IsNullOrEmpty(captions))
            {
                media = new List<string> { "-f", "lavfi", "-t", "0.02", "-i", "anullsrc=r=16000:cl=mono" };
                hasAudio = true;
            }
            if (hasVideo || hasAudio)
            {
                arguments.AddRange(media);
                mediaIndex = input++;
            }
            if (!string.IsNullOrEmpty(captions))
            {
                captionFile = output + ".captions.srt";
                File.WriteAllText(captionFile, captions);
                arguments.AddRange(new[] { "-i", captionFile });
                captionIndex = input++;
            }
            try
            {
                // The picture is copied as it is; Matroska holds every common video codec.
                if (hasVideo) arguments.AddRange(new[] { "-map", mediaIndex + ":v:0", "-c:v", "copy" });
                if (hasAudio) arguments.AddRange(new[] { "-map", mediaIndex + ":a:0", "-c:a", "libopus", "-b:a", bitrate });
                if (captionIndex >= 0) arguments.AddRange(new[] { "-map", captionIndex + ":s", "-c:s", "srt", "-metadata:s:s:0", "title=Visemes" });
                arguments.AddRange(new[] { "-attach", recording, "-metadata:s:t:0", "mimetype=" + AttachmentType, "-metadata:s:t:0", "filename=" + AttachmentName });
                arguments.AddRange(new[] { "-f", "matroska", output });
                (int code, string errors) = FTRecorderFfmpeg.Run(ffmpeg, arguments, 3600000);
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
// FTRecorderMatroska (this file)  One-file recordings (.mkv) through the user's ffmpeg.
//   WriteWithRawAudio(ffmpeg, recording, audio, sampleRate, audioStart, captions, output)  Opus 48 kb/s audio from raw
//                            s16le (offset by audioStart), an SRT subtitle track titled "Visemes", and the recording
//                            attached as recording.ftrec.jsonl (application/x-ndjson). For the Recorder. With no video, audio
//                            or captions, 20 ms of silence is the track (a Matroska file needs one).
//   WriteWithVideo(ffmpeg, recording, video, captions, output)  The same with a video file's first video track copied
//                            unchanged and its first audio track as Opus 96 kb/s (no audio track when it has none,
//                            checked first). For process.
//   ExtractRecording(ffmpeg, mkv)  The attached recording copied to a new temporary folder; its path.
//   IsMatroska(path)         The file starts with the EBML magic 1A 45 DF A3.
