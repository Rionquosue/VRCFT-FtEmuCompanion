"""One recording of the Recorder: VRCFaceTracking's OSC output from the running relay, visemes (from VRChat's OSC while
it runs; else, on Windows, OVRLipSync on the microphone) and, when chosen, the microphone's audio, into working files
next to the output, combined into one .mkv when stopped."""

from __future__ import annotations

import array
import datetime
import os
import sys
import threading

from . import captions, lipsync, matroska, vrchat
from . import ffmpeg as ff
from .recording import Recording, RecordingWriter

NO_MIC_VISEMES = "Visemes will not be recorded without a mic"
LINUX_VISEMES = "Visemes are only available when VRChat is running."


class Settings:
    def __init__(self, folder: str, microphone: "ff.Microphone | None", record_audio: bool, ffmpeg: str | None, ovrlipsync: str | None):
        self.folder = folder
        self.microphone = microphone
        self.record_audio = record_audio
        self.ffmpeg = ffmpeg
        self.ovrlipsync = ovrlipsync


def output_name(folder: str, when: datetime.datetime) -> str:
    """FTRecording-HHmm-ddMMyy.mkv in folder (24-hour time, the date as day, month, two-digit year), with " (2)",
    " (3)" and on added when that name, or its working files, already exist."""
    name = "FTRecording-" + when.strftime("%H%M") + "-" + when.strftime("%d%m%y")
    number = 1
    while True:
        path = os.path.join(folder, name + ("" if number == 1 else " (%d)" % number) + ".mkv")
        if not any(os.path.exists(p) for p in (path, _data(path), _audio(path))):
            return path
        number += 1


def _data(output: str) -> str:
    return output + ".recording.ftrec.jsonl"


def _audio(output: str) -> str:
    return output + ".microphone.s16le"


class Microphone:
    """The microphone during a recording: ffmpeg's 16 kHz mono 16-bit samples, 10 ms at a time, appended raw to a working
    file when the audio is kept (so a crash keeps it) and, with OVRLipSync, turned into viseme entries."""

    def __init__(self, stream: ff.Stream, audio_path: str | None, lip, writer: RecordingWriter, log):
        self._stream = stream
        self._lip = lip
        self._writer = writer
        self._log = log
        self._audio = open(audio_path, "xb") if audio_path else None
        self._stopping = False
        self.audio_start = -1.0
        self.blocks = 0
        self._thread = threading.Thread(target=self._loop, name="FTRecorder microphone", daemon=True)
        self._thread.start()

    def _loop(self):
        hop = lipsync.HOP
        while not self._stopping:
            data = self._stream.read_exactly(hop * 2)
            if data is None:
                break
            if self.audio_start < 0:
                # The block just read began 10 ms ago.
                self.audio_start = max(0.0, self._writer.now - hop / lipsync.SAMPLE_RATE)
            if self._audio is not None:
                self._audio.write(data)
            self.blocks += 1
            if self._audio is not None and self.blocks % 100 == 0:
                self._audio.flush()
            if self._lip is None:
                continue
            samples = array.array("h")
            samples.frombytes(data)
            if sys.byteorder == "big":
                samples.byteswap()
            try:
                weights = self._lip.process(samples)
            except lipsync.LipSyncError as error:
                if not self._stopping:
                    self._log("Visemes stopped: %s" % error)
                self._lip = None
                continue
            best, voice = lipsync.block_viseme(weights, samples)
            self._writer.write_viseme(self._writer.now, best, voice, weights)
        if not self._stopping:
            self._log("The microphone stopped: " + self._stream.errors)

    def close(self):
        self._stopping = True
        self._stream.close()
        self._thread.join(2)
        if self._audio is not None:
            self._audio.close()


class Session:
    """Start with Session.start; status() for the counts; stop() writes the .mkv."""

    def __init__(self, settings: Settings, relay, log):
        self.settings = settings
        self.relay = relay
        self.log = log
        self.output = output_name(settings.folder, datetime.datetime.now())
        self.data = _data(self.output)
        self.audio = _audio(self.output) if settings.record_audio and settings.microphone is not None else None
        self.vrchat = vrchat.is_running()
        self.lip = None
        self.writer = None
        self.listener = None
        self.microphone = None
        self.packets_at_start = 0
        self._recording_relay = False

    @staticmethod
    def start(settings: Settings, relay, ask, log) -> "Session":
        """Starts recording what the running relay passes on. Blocks (ffmpeg and OVRLipSync are checked first), so it
        runs off the window's thread. ask(reason) is given the error when OVRLipSync cannot be found or loaded and
        returns a path, or None to record without visemes. The relay keeps running after the session."""
        if not settings.folder or not settings.folder.strip():
            raise ValueError("Choose the output folder.")
        if relay is None:
            raise RuntimeError("The relay is not running: see the error above, or change the ports.")
        session = Session(settings, relay, log)
        try:
            session._open(ask)
        except BaseException:
            session._close()
            raise
        return session

    def _open(self, ask):
        settings = self.settings
        # ffmpeg first, so nothing is created when it is missing.
        ff.check(settings.ffmpeg)
        os.makedirs(settings.folder, exist_ok=True)
        windows = os.name == "nt"
        visemes = "none"
        if self.vrchat:
            visemes = "vrchat"
        elif not windows:
            self.log(LINUX_VISEMES)
        elif settings.microphone is None:
            self.log(NO_MIC_VISEMES + ": recording face tracking only.")
        else:
            try:
                self.lip, loaded = lipsync.open_ovrlipsync(settings.ovrlipsync, ask)
                visemes = "ovrlipsync"
                self.log("VRChat is not running: visemes from your microphone through OVRLipSync (%s)." % loaded)
            except lipsync.LipSyncError as error:
                self.log("%s Recording without visemes." % error)
        # The microphone is opened to keep its audio, or to feed OVRLipSync.
        use_microphone = settings.microphone is not None and (self.audio is not None or self.lip is not None)
        if settings.microphone is None and settings.record_audio:
            self.log("No microphone: recording without audio.")
        parts = ["vrcft-relay:%d->%d" % (self.relay.in_port, self.relay.out_port), "visemes: " + visemes,
                 "microphone" if self.audio else "no audio"]
        self.writer = RecordingWriter(self.data, "; ".join(parts))
        self.packets_at_start = self.relay.packets
        self.relay.set_writer(self.writer)
        self._recording_relay = True
        if self.vrchat:
            self.listener = vrchat.Listener(lambda: self.writer.now, self.writer.write_parameter, self.log)
            self.log("VRChat is running: recording the Viseme and Voice it sends (OSC must be on in VRChat)." if self.listener.discoverable
                     else "VRChat is running, but it cannot find the viseme listener: multicast DNS is unavailable.")
        if use_microphone:
            stream = ff.open_microphone(settings.ffmpeg, settings.microphone)
            self.microphone = Microphone(stream, self.audio, self.lip, self.writer, self.log)
            self.log("Microphone: %s%s" % (settings.microphone.label, "" if self.audio else " (for visemes; its audio is not kept)"))
        self.log("Recording to " + self.output)

    def status(self) -> str:
        """The counts so far, for the window's status line."""
        listener, microphone = self.listener, self.microphone
        text = "Recording: %d VRCFaceTracking packets" % (self.relay.packets - self.packets_at_start)
        if listener is not None:
            text += ", %d VRChat viseme values" % listener.received
        if microphone is not None:
            text += ", %d s of microphone" % round(microphone.blocks / 100)
        return text

    def stop(self) -> None:
        """Stops recording and writes the .mkv; the working files are deleted once it is written. Blocks (ffmpeg
        encodes). Raises RuntimeError, keeping the working files, when the .mkv cannot be written."""
        audio_start = self.microphone.audio_start if self.microphone is not None else -1.0
        self._close()
        self.log("Writing " + self.output)
        kept = [self.data] + ([self.audio] if self.audio else [])
        try:
            text = captions.build(Recording.load(self.data))
            matroska.write_with_raw_audio(self.settings.ffmpeg, self.data, self.audio, lipsync.SAMPLE_RATE, audio_start, text, self.output)
        except (RuntimeError, OSError, ValueError) as error:
            raise RuntimeError("%s The recording is kept in %s." % (error, " and ".join(kept))) from error
        for path in kept:
            os.remove(path)
        self.log("Saved " + self.output)

    # Everything started is stopped; the writer is closed last, after the threads that write to it.
    def _close(self):
        if self._recording_relay:
            self.relay.set_writer(None)
            self._recording_relay = False
        for part in (self.listener, self.microphone):
            if part is not None:
                part.close()
        self.listener = self.microphone = None
        if self.writer is not None:
            self.writer.close()
        if self.lip is not None:
            self.lip.close()
            self.lip = None

# ============================================================================
# GLOSSARY: Recorder session
# ============================================================================
#
# session (this file)      One recording of the Recorder.
#   Settings                 folder (output), microphone (ffmpeg.Microphone or None), record_audio, ffmpeg, ovrlipsync
#                            (None or empty: VRChat's copy).
#   output_name(folder, when)  FTRecording-HHmm-ddMMyy.mkv; " (2)", " (3)"... when it or its working files exist.
#   Session.start(settings, relay, ask, log)  ffmpeg checked (FfmpegNotFound) before anything is made. Visemes: VRChat
#                            running (checked once) -> vrchat.Listener; else Windows with a microphone -> OVRLipSync
#                            (ask on failure; no path given -> recording without visemes, said in the log); else none,
#                            with NO_MIC_VISEMES (Windows, no microphone) or LINUX_VISEMES in the log. The microphone is
#                            opened when its audio is kept (record_audio) or OVRLipSync needs it. Working files
#                            NAME.mkv.recording.ftrec.jsonl and, with audio, NAME.mkv.microphone.s16le.
#   Session.status()         Packets recorded, VRChat viseme values, seconds of microphone.
#   Session.stop()           Detaches the writer from the relay, closes the rest, writes the .mkv (captions, audio when
#                            kept, the recording attached), deletes the working files; RuntimeError keeps them.
#   Microphone               Reads 10 ms blocks; appends them to the audio file when kept; OVRLipSync viseme entries.
