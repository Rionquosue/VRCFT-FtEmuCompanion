"""A recorded video into a recording: every frame through the face models as Unified Expressions and eyes, and the sound
track into visemes (OVRLipSync, FTRecorder's classifier, or a running VRChat), in one file in time order. Frames with no
face are left out, so playback holds the last face."""

from __future__ import annotations

import os
import threading
import time

import numpy as np

from . import expressions, lipsync, playback, vrchat
from . import ffmpeg as ff
from .recording import Entry, PARAMETER, RecordingWriter

CLASSIFIER = "FTRecorder classifier"
OVRLIPSYNC = "OVRLipSync"
_VRCHAT_RATE = 48000
_LEAD_IN = 5


class Options:
    def __init__(self, **values):
        self.video = values.get("video")
        self.output = values.get("output")
        self.ffmpeg = values.get("ffmpeg")
        self.babble_model = values.get("babble_model")
        self.babble_labels = values.get("babble_labels")
        self.babble_crop = values.get("babble_crop", 1.0)
        self.ovrlipsync = values.get("ovrlipsync")
        # None: no visemes made here; "ovrlipsync" or "classifier".
        self.engine = values.get("engine")
        self.ask_for_ovrlipsync = values.get("ask_for_ovrlipsync")
        # Viseme and Voice already taken from VRChat (Entry list, times from the start of the audio), or None.
        self.vrchat_visemes = values.get("vrchat_visemes")
        self.fps = values.get("fps", 30.0)
        self.size = values.get("size", 640)
        self.threads = values.get("threads", 2)


def capture_vrchat_visemes(ffmpeg, video, cable, progress, cancel: threading.Event) -> list[Entry]:
    """Visemes for a video from VRChat itself: its sound is played into a virtual audio cable that VRChat uses as its
    microphone, and the Viseme and Voice VRChat sends back over OSC are kept, timed from the start of playback. Runs at
    the video's own speed. Windows only (playback through winmm)."""
    audio = ff.read_audio(ffmpeg, video, _VRCHAT_RATE, cancel)
    entries: list[Entry] = []
    if not audio:
        return entries
    gate = threading.Lock()
    started = [None]

    def clock():
        return -1.0 if started[0] is None else time.perf_counter() - started[0]

    def sink(t, name, kind, value):
        if t >= 0:
            with gate:
                entries.append(Entry(t, PARAMETER, name=name, type=kind, value=value))

    listener = vrchat.Listener(clock, sink, progress)
    try:
        if not listener.discoverable:
            raise RuntimeError("VRChat cannot find the viseme listener: multicast DNS is unavailable.")
        progress("Waiting %d s for VRChat to find the viseme listener" % _LEAD_IN)
        if cancel.wait(_LEAD_IN):
            raise ff.Cancelled()
        progress('Playing %.1f s of audio into VRChat through "%s"' % (len(audio) / _VRCHAT_RATE, cable))
        playback.play(cable, audio, _VRCHAT_RATE, lambda: started.__setitem__(0, time.perf_counter()), cancel)
        # VRChat's last values arrive a moment after the sound ends.
        cancel.wait(0.5)
        if cancel.is_set():
            raise ff.Cancelled()
    finally:
        listener.close()
    with gate:
        if not entries:
            raise RuntimeError("VRChat sent no visemes. Check that OSC is on, the microphone is the cable's output and not "
                               "muted, and the avatar uses visemes.")
        return sorted(entries, key=lambda entry: entry.time)


def process(options: Options, progress, cancel: threading.Event) -> str:
    """Writes options.output (it must not exist). progress gets short status lines. Returns a summary."""
    if not os.path.isfile(options.video):
        raise FileNotFoundError("The video was not found: " + options.video)
    if os.path.exists(options.output):
        raise FileExistsError("The output already exists: " + options.output)
    from .face import FaceTracker

    # OVRLipSync is found (or asked for) before any work, so a missing library stops the run at once.
    lip = None
    engine = "VRChat (OSC)" if options.vrchat_visemes is not None else "none"
    if options.vrchat_visemes is None and options.engine == "ovrlipsync":
        lip, loaded = lipsync.open_ovrlipsync(options.ovrlipsync, options.ask_for_ovrlipsync)
        engine = OVRLIPSYNC
        progress("OVRLipSync: " + loaded)
    elif options.vrchat_visemes is None and options.engine == "classifier":
        engine = CLASSIFIER
    visemes = []
    try:
        if options.vrchat_visemes is None and options.engine is not None:
            progress("Reading audio")
            audio = ff.read_audio(options.ffmpeg, options.video, 16000, cancel)
            if not audio:
                engine = "none (no audio)"
            else:
                progress("Making visemes")
                visemes = _with_ovrlipsync(lip, audio, cancel) if lip is not None else _with_classifier(audio)
    finally:
        if lip is not None:
            lip.close()

    tracker = FaceTracker(options.babble_model, options.babble_labels, options.babble_crop, options.threads)
    source = "video:%s; face: MediaPipe%s; visemes: %s" % (os.path.basename(options.video), " + Babble" if tracker.uses_babble else "", engine)
    frames = faces = 0
    vrchat_entries = options.vrchat_visemes or []
    size = options.size
    with RecordingWriter(options.output, source) as writer, ff.open_video(options.ffmpeg, options.video, size, options.fps) as video:
        unified = [0.0] * len(expressions.UNIFIED_NAMES)
        eye = [0.0] * len(expressions.EYE_NAMES)
        following = following_vrchat = 0
        while True:
            raw = video.read_exactly(size * size * 3)
            if raw is None:
                break
            if cancel.is_set():
                raise ff.Cancelled()
            t = frames / options.fps
            while following < len(visemes) and visemes[following][0] <= t:
                writer.write_viseme(*visemes[following])
                following += 1
            while following_vrchat < len(vrchat_entries) and vrchat_entries[following_vrchat].time <= t:
                _write_parameter(writer, vrchat_entries[following_vrchat])
                following_vrchat += 1
            frame = np.frombuffer(raw, dtype=np.uint8).reshape(size, size, 3)
            if tracker.track(frame, unified, eye):
                writer.write_expressions(t, unified, eye)
                faces += 1
            frames += 1
            if frames % 150 == 0:
                progress("Frame %d (%.1f s), face in %d" % (frames, t, faces))
        for entry in visemes[following:]:
            writer.write_viseme(*entry)
        for entry in vrchat_entries[following_vrchat:]:
            _write_parameter(writer, entry)
        if frames == 0:
            raise RuntimeError("ffmpeg gave no video frames. " + video.errors)
    written = len(visemes) + len(vrchat_entries)
    kind = " VRChat viseme values" if options.vrchat_visemes is not None else " viseme frames"
    return "%d frames, a face in %d; %d%s (%s)." % (frames, faces, written, kind, engine)


def _write_parameter(writer, entry: Entry) -> None:
    writer.write_parameter(entry.time, entry.name, entry.type, entry.value)


def _with_ovrlipsync(lip, audio, cancel) -> list[tuple]:
    """(time, viseme, voice, weights) per 10 ms from OVRLipSync."""
    hop = lipsync.HOP
    result = []
    for start in range(0, len(audio) - hop + 1, hop):
        if cancel.is_set():
            raise ff.Cancelled()
        block = audio[start:start + hop]
        weights = lip.process(block)
        best, voice = lipsync.block_viseme(weights, block)
        result.append(((start + hop) / lipsync.SAMPLE_RATE, best, voice, weights))
    return result


def _with_classifier(audio) -> list[tuple]:
    from . import classifier
    return [(f.time, f.viseme, f.voice, f.weights) for f in classifier.classify(audio)]

# ============================================================================
# GLOSSARY: process
# ============================================================================
#
# process (this file)      The companion command line's process, without the command line.
#   Options                  video, output (the working .ftrec.jsonl), ffmpeg, babble_model/labels/crop, ovrlipsync,
#                            engine (None, "ovrlipsync" or "classifier"), ask_for_ovrlipsync, vrchat_visemes, fps 30,
#                            size 640, threads 2.
#   process(options, progress, cancel)  Visemes first (OVRLipSync opened, or asked for, before any work; the sound read
#                            at 16 kHz), then every frame through face.FaceTracker; entries interleaved by time. Returns a
#                            summary. ffmpeg.Cancelled when cancel is set.
#   capture_vrchat_visemes(ffmpeg, video, cable, progress, cancel)  48 kHz playback into the cable (playback.play) after a
#                            5 s lead-in, recording VRChat's Viseme and Voice (vrchat.Listener) from the start of playback.
