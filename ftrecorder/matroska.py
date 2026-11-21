"""The one-file form of a recording: a Matroska (.mkv) file with the picture and audio (Opus) when there are any, the
visemes as a subtitle track, and the whole recording (.ftrec.jsonl) attached. Written and read through the user's ffmpeg."""

from __future__ import annotations

import os
import tempfile

from . import ffmpeg as ff

ATTACHMENT_NAME = "recording.ftrec.jsonl"
ATTACHMENT_TYPE = "application/x-ndjson"
_MAGIC = b"\x1a\x45\xdf\xa3"


def is_matroska(path: str) -> bool:
    """True when the file starts like a Matroska file."""
    with open(path, "rb") as file:
        return file.read(4) == _MAGIC


def write_recording(ffmpeg: str | None, recording: str, captions: str | None, output: str) -> None:
    """A recording with SRT captions (None for none) and no picture or sound. Raises RuntimeError with ffmpeg's error
    on failure."""
    _write(ffmpeg, recording, [], False, False, "48k", captions, output)


def write_with_video(ffmpeg: str | None, recording: str, video: str, captions: str | None, output: str) -> None:
    """Like write_recording, with a video file's first video track copied unchanged and its first audio track (none
    when it has none) as Opus, both from the video's start."""
    # Asked first: mapping a missing audio track fails the whole write.
    code, _ = ff.run(ffmpeg, ["-hide_banner", "-loglevel", "error", "-nostdin", "-i", video, "-map", "0:a:0", "-t", "0", "-f", "null", "-"], 60)
    _write(ffmpeg, recording, ["-i", video], True, code == 0, "96k", captions, output)


def _write(ffmpeg, recording, media, has_video, has_audio, bitrate, captions, output) -> None:
    arguments = ["-hide_banner", "-loglevel", "error", "-nostdin", "-y"]
    inputs = 0
    media_index = caption_index = -1
    caption_file = None
    # A Matroska file needs a track: with no picture, sound or visemes, 20 ms of silence carries the attachment.
    if not has_video and not has_audio and not captions:
        media = ["-f", "lavfi", "-t", "0.02", "-i", "anullsrc=r=16000:cl=mono"]
        has_audio = True
    if has_video or has_audio:
        arguments += media
        media_index = inputs
        inputs += 1
    if captions:
        caption_file = output + ".captions.srt"
        with open(caption_file, "w", encoding="utf-8", newline="\n") as file:
            file.write(captions)
        arguments += ["-i", caption_file]
        caption_index = inputs
        inputs += 1
    try:
        if has_video:
            arguments += ["-map", "%d:v:0" % media_index, "-c:v", "copy"]
        if has_audio:
            arguments += ["-map", "%d:a:0" % media_index, "-c:a", "libopus", "-b:a", bitrate]
        if caption_index >= 0:
            arguments += ["-map", "%d:s" % caption_index, "-c:s", "srt", "-metadata:s:s:0", "title=Visemes"]
        arguments += ["-attach", recording, "-metadata:s:t:0", "mimetype=" + ATTACHMENT_TYPE, "-metadata:s:t:0", "filename=" + ATTACHMENT_NAME]
        arguments += ["-f", "matroska", output]
        code, errors = ff.run(ffmpeg, arguments, 3600)
        if code != 0:
            raise RuntimeError("ffmpeg could not write " + output + ": " + errors)
    finally:
        if caption_file:
            os.remove(caption_file)


def extract_recording(ffmpeg: str | None, mkv: str) -> str:
    """Copies the attached recording out of an .mkv into a new temporary folder; its path."""
    folder = tempfile.mkdtemp(prefix="RiryTools.FTRecorder.")
    target = os.path.join(folder, ATTACHMENT_NAME)
    code, errors = ff.run(ffmpeg, ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-dump_attachment:t:0", target,
                                   "-i", mkv, "-t", "0", "-f", "null", "-"], 120)
    if not os.path.isfile(target):
        os.rmdir(folder)
        raise ValueError(mkv + " has no attached recording" + (": " + errors if code != 0 else "."))
    return target

# ============================================================================
# GLOSSARY: Matroska
# ============================================================================
#
# matroska (this file)     One-file recordings (.mkv) through the user's ffmpeg.
#   write_recording(ffmpeg, recording, captions, output)  An SRT subtitle track titled "Visemes" and the recording attached
#                            as recording.ftrec.jsonl (application/x-ndjson). For the Recorder. With no video, audio or
#                            captions, 20 ms of silence is the track (a Matroska file needs one).
#   write_with_video(ffmpeg, recording, video, captions, output)  The video's first video track copied, its first audio
#                            track (checked first) as Opus 96 kb/s. For process.
#   extract_recording(ffmpeg, mkv)  The attachment copied to a new temporary folder; its path.
#   is_matroska(path)        The EBML magic 1A 45 DF A3.
