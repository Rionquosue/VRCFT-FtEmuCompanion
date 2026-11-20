"""Recordings: one JSON object per line (.ftrec.jsonl), the first the header; or an .mkv with that file attached."""

from __future__ import annotations

import base64
import datetime
import json
import os
import shutil
import threading
import time

from . import expressions, matroska

FORMAT = "RiryTools.FTRecorder"
VERSION = 1

EXPRESSIONS = "expressions"   # Unified Expressions and eyes, in the header's "unified" and "eye" order
OSC = "osc"                   # one OSC packet as it was sent, unchanged
PARAMETER = "param"           # one avatar parameter VRChat sent (Viseme, Voice)
VISEME = "viseme"             # a viseme made from audio: the strongest of the 15, Voice, and all 15 weights


class Entry:
    """One line of a recording. time is seconds from the start; the other fields depend on kind."""

    __slots__ = ("time", "kind", "unified", "eye", "packet", "name", "type", "value", "viseme", "voice", "weights")

    def __init__(self, time: float, kind: str, **fields):
        self.time = time
        self.kind = kind
        for slot in self.__slots__[2:]:
            setattr(self, slot, fields.get(slot))


class Recording:
    def __init__(self):
        self.source = None
        self.created = None
        self.unified: list[str] = []
        self.eye: list[str] = []
        self.entries: list[Entry] = []

    @property
    def duration(self) -> float:
        return self.entries[-1].time if self.entries else 0.0

    @staticmethod
    def load(path: str, ffmpeg: str | None = None) -> "Recording":
        """Reads a recording. A line that is not valid is refused with its number. An .mkv is read through ffmpeg (the
        recording attached to it)."""
        if matroska.is_matroska(path):
            extracted = matroska.extract_recording(ffmpeg, path)
            try:
                return Recording.load(extracted)
            finally:
                shutil.rmtree(os.path.dirname(extracted), ignore_errors=True)
        recording = Recording()
        number = 0
        with open(path, encoding="utf-8") as lines:
            for line in lines:
                number += 1
                line = line.strip()
                if not line:
                    continue
                try:
                    root = json.loads(line)
                    if number == 1:
                        if root.get("format") != FORMAT:
                            raise ValueError("not an FTRecorder recording")
                        if int(root.get("version", VERSION)) > VERSION:
                            raise ValueError("made by a newer FTRecorder (version %s)" % root["version"])
                        recording.source = root.get("source")
                        recording.created = root.get("created")
                        recording.unified = list(root.get("unified", []))
                        recording.eye = list(root.get("eye", []))
                        continue
                    recording.entries.append(_entry(root))
                except (ValueError, KeyError, TypeError) as error:
                    raise ValueError("%s line %d: %s" % (path, number, error)) from error
        if number == 0:
            raise ValueError(path + " is empty.")
        return recording


def _entry(root: dict) -> Entry:
    t = float(root["t"])
    if "unified" in root:
        return Entry(t, EXPRESSIONS, unified=[float(v) for v in root["unified"]], eye=[float(v) for v in root.get("eye", [])])
    if "osc" in root:
        return Entry(t, OSC, packet=base64.b64decode(root["osc"] or ""))
    if "param" in root:
        if "i" in root:
            return Entry(t, PARAMETER, name=root["param"], type="i", value=float(int(root["i"])))
        if "f" in root:
            return Entry(t, PARAMETER, name=root["param"], type="f", value=float(root["f"]))
        if "b" in root:
            return Entry(t, PARAMETER, name=root["param"], type="b", value=1.0 if root["b"] else 0.0)
        raise ValueError("parameter without a value")
    if "viseme" in root:
        return Entry(t, VISEME, viseme=int(root["viseme"]), voice=float(root.get("voice", 0.0)), weights=[float(v) for v in root.get("weights", [])])
    raise ValueError("unknown entry")


class RecordingWriter:
    """Writes a recording as it happens: a header line, then one line per entry. Safe to call from several threads;
    lines are written whole. The file is flushed at least once a second, so a crash loses at most that much."""

    def __init__(self, path: str, source: str):
        """Creates path (it must not exist) and writes the header."""
        self.path = path
        folder = os.path.dirname(os.path.abspath(path))
        os.makedirs(folder, exist_ok=True)
        self._file = open(path, "x", encoding="utf-8", newline="\n")
        self._gate = threading.Lock()
        self._start = time.perf_counter()
        self._last_flush = 0.0
        self._closed = False
        self._line({
            "format": FORMAT,
            "version": VERSION,
            "source": source or "",
            "created": datetime.datetime.now(datetime.timezone.utc).isoformat().replace("+00:00", "Z"),
            "unified": expressions.UNIFIED_NAMES,
            "eye": expressions.EYE_NAMES,
        })

    @property
    def now(self) -> float:
        """Seconds since the recording started."""
        return time.perf_counter() - self._start

    def write_expressions(self, t: float, unified, eye) -> None:
        self._line({"t": round(t, 4), "unified": [round(float(v), 4) for v in unified], "eye": [round(float(v), 4) for v in eye]})

    def write_osc(self, t: float, packet: bytes) -> None:
        self._line({"t": round(t, 4), "osc": base64.b64encode(packet).decode("ascii")})

    def write_parameter(self, t: float, name: str, kind: str, value: float) -> None:
        line = {"t": round(t, 4), "param": name}
        if kind == "i":
            line["i"] = int(value)
        elif kind == "b":
            line["b"] = value != 0
        else:
            line["f"] = round(float(value), 4)
        self._line(line)

    def write_viseme(self, t: float, viseme: int, voice: float, weights) -> None:
        self._line({"t": round(t, 4), "viseme": int(viseme), "voice": round(float(voice), 4), "weights": [round(float(v), 4) for v in weights]})

    def _line(self, value: dict) -> None:
        text = json.dumps(value, ensure_ascii=False, separators=(",", ":")) + "\n"
        with self._gate:
            if self._closed:
                return
            self._file.write(text)
            now = time.perf_counter()
            if now - self._last_flush >= 1.0:
                self._file.flush()
                self._last_flush = now

    def close(self) -> None:
        with self._gate:
            if self._closed:
                return
            self._closed = True
            self._file.close()

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self.close()

# ============================================================================
# GLOSSARY: recordings
# ============================================================================
#
# recording (this file)    The .ftrec.jsonl format, shared with the C# module (FTRecorderRecording).
#   Header                   format "RiryTools.FTRecorder", version 1, source, created (UTC), unified and eye names.
#   Entry kinds              EXPRESSIONS {"t","unified","eye"}; OSC {"t","osc": base64}; PARAMETER {"t","param", one of
#                            "i"/"f"/"b"}; VISEME {"t","viseme","voice","weights"[15]}. Numbers rounded to 4 places.
#   Recording.load(path, ffmpeg)  Reads .ftrec.jsonl, or the recording attached to an .mkv; ValueError with the line.
#   RecordingWriter(path, source)  Creates path (must not exist); thread-safe line writes, flushed every second; now is
#                            seconds since it was made; close() (or with) ends it, later writes are dropped.
