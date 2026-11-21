"""One recording of the Recorder: every avatar parameter VRChat sends over OSC (found through OSCQuery), into a working
file next to the output, combined into one .mkv when stopped."""

from __future__ import annotations

import datetime
import os

from . import captions, matroska, vrchat
from . import ffmpeg as ff
from .recording import Recording, RecordingWriter

VRCHAT_NOT_RUNNING = "VRChat is not running!"


class VRChatNotRunning(RuntimeError):
    """Recording was asked for while VRChat is not running."""


class Settings:
    def __init__(self, folder: str, ffmpeg: str | None):
        self.folder = folder
        self.ffmpeg = ffmpeg


def output_name(folder: str, when: datetime.datetime) -> str:
    """FTRecording-HHmm-ddMMyy.mkv in folder (24-hour time, the date as day, month, two-digit year), with " (2)",
    " (3)" and on added when that name, or its working file, already exists."""
    name = "FTRecording-" + when.strftime("%H%M") + "-" + when.strftime("%d%m%y")
    number = 1
    while True:
        path = os.path.join(folder, name + ("" if number == 1 else " (%d)" % number) + ".mkv")
        if not any(os.path.exists(p) for p in (path, _data(path))):
            return path
        number += 1


def _data(output: str) -> str:
    return output + ".recording.ftrec.jsonl"


class Session:
    """Start with Session.start; status() for the count; stop() writes the .mkv."""

    def __init__(self, settings: Settings, log):
        self.settings = settings
        self.log = log
        self.output = output_name(settings.folder, datetime.datetime.now())
        self.data = _data(self.output)
        self.writer = None
        self.listener = None

    @staticmethod
    def start(settings: Settings, log) -> "Session":
        """Starts recording what VRChat sends. ffmpeg is checked first (FfmpegNotFound), then VRChat (VRChatNotRunning);
        nothing is made when either fails."""
        if not settings.folder or not settings.folder.strip():
            raise ValueError("Choose the output folder.")
        ff.check(settings.ffmpeg)
        if not vrchat.is_running():
            raise VRChatNotRunning(VRCHAT_NOT_RUNNING)
        session = Session(settings, log)
        try:
            session._open()
        except BaseException:
            session._close()
            raise
        return session

    def _open(self):
        os.makedirs(self.settings.folder, exist_ok=True)
        self.writer = RecordingWriter(self.data, "vrchat-oscquery: avatar parameters")
        self.listener = vrchat.Listener(lambda: self.writer.now, self.writer.write_parameter, self.log, kept=None)
        self.log("Recording the avatar parameters VRChat sends (OSC must be on in VRChat)." if self.listener.discoverable
                 else "VRChat cannot find the Recorder: multicast DNS is unavailable.")
        self.log("Recording to " + self.output)

    def status(self) -> str:
        """The count so far, for the window's status line."""
        listener = self.listener
        return "Recording: %d VRChat values" % (listener.received if listener is not None else 0)

    def stop(self) -> None:
        """Stops recording and writes the .mkv; the working file is deleted once it is written. Blocks (ffmpeg).
        Raises RuntimeError, keeping the working file, when the .mkv cannot be written."""
        self._close()
        self.log("Writing " + self.output)
        try:
            text = captions.build(Recording.load(self.data))
            matroska.write_recording(self.settings.ffmpeg, self.data, text, self.output)
        except (RuntimeError, OSError, ValueError) as error:
            raise RuntimeError("%s The recording is kept in %s." % (error, self.data)) from error
        os.remove(self.data)
        self.log("Saved " + self.output)

    # The listener is stopped before the writer it writes to is closed.
    def _close(self):
        if self.listener is not None:
            self.listener.close()
            self.listener = None
        if self.writer is not None:
            self.writer.close()

# ============================================================================
# GLOSSARY: Recorder session
# ============================================================================
#
# session (this file)      One recording of the Recorder.
#   Settings                 folder (output), ffmpeg.
#   output_name(folder, when)  FTRecording-HHmm-ddMMyy.mkv; " (2)", " (3)"... when it or its working file exists.
#   VRCHAT_NOT_RUNNING       "VRChat is not running!": VRChatNotRunning's message, and the Recorder's popup.
#   Session.start(settings, log)  Folder checked, then ffmpeg (FfmpegNotFound), then VRChat (vrchat.is_running;
#                            VRChatNotRunning); nothing is made when one fails. Then vrchat.Listener with kept=None:
#                            every /avatar/parameters/ value VRChat sends, as parameter entries in the working file
#                            NAME.mkv.recording.ftrec.jsonl.
#   Session.status()         The VRChat values recorded.
#   Session.stop()           Closes the listener and writer, writes the .mkv (matroska.write_recording: viseme captions
#                            from Viseme and Voice, the recording attached), deletes the working file; RuntimeError keeps it.
