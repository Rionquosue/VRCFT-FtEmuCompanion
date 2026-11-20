"""Video frames and audio through the user's own ffmpeg (not bundled). ffmpeg is started with an argument list, never
through a shell, and writes raw data to its standard output: frames as RGB fitted into a square with black borders, and
audio as mono 16-bit samples."""

from __future__ import annotations

import os
import re
import subprocess
import sys
import threading

import array

_HIDDEN = getattr(subprocess, "CREATE_NO_WINDOW", 0)
_ERROR_LIMIT = 8192


def _environment() -> dict | None:
    """The environment ffmpeg starts with. In the compiled Linux app, PyInstaller points LD_LIBRARY_PATH at its own
    libraries; ffmpeg gets the user's original back, so it loads the system's."""
    if not getattr(sys, "frozen", False) or os.name == "nt":
        return None
    environment = dict(os.environ)
    original = environment.pop("LD_LIBRARY_PATH_ORIG", None)
    if original is not None:
        environment["LD_LIBRARY_PATH"] = original
    else:
        environment.pop("LD_LIBRARY_PATH", None)
    return environment


def executable(ffmpeg: str | None) -> str:
    return ffmpeg.strip() if ffmpeg and ffmpeg.strip() else "ffmpeg"


def not_found(ffmpeg: str | None) -> str:
    """The message for an ffmpeg that cannot be started: what was looked for, and how to fix it."""
    return ('ffmpeg was not found (looked for "%s"). Install ffmpeg from https://ffmpeg.org/download.html and add it to '
            "PATH, or give its full path (--ffmpeg, or the Recorder's ffmpeg field)." % executable(ffmpeg))


class Cancelled(Exception):
    """The work was stopped (Ctrl+C, or the Recorder's Stop)."""


class FfmpegNotFound(FileNotFoundError):
    """ffmpeg could not be started; the message says what was looked for and how to install it."""


def run(ffmpeg: str | None, arguments: list[str], timeout: float) -> tuple[int, str]:
    """Runs ffmpeg to the end; its exit code and error output. Raises FfmpegNotFound when it cannot start."""
    try:
        done = subprocess.run([executable(ffmpeg)] + arguments, stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL,
                              stderr=subprocess.PIPE, timeout=timeout, creationflags=_HIDDEN, env=_environment())
    except OSError as error:
        raise FfmpegNotFound(not_found(ffmpeg)) from error
    except subprocess.TimeoutExpired as error:
        return -1, "ffmpeg took too long. " + (error.stderr or b"").decode("utf-8", "replace").strip()
    return done.returncode, done.stderr.decode("utf-8", "replace").strip()


def check(ffmpeg: str | None) -> None:
    """Raises FfmpegNotFound when ffmpeg cannot be started."""
    code, _ = run(ffmpeg, ["-hide_banner", "-version"], 15)
    if code != 0:
        raise FfmpegNotFound(not_found(ffmpeg))


class Stream:
    """A running ffmpeg whose standard output is read. close() may be called from another thread to end a read."""

    def __init__(self, ffmpeg: str | None, arguments: list[str]):
        try:
            self._process = subprocess.Popen([executable(ffmpeg)] + arguments, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                             stderr=subprocess.PIPE, creationflags=_HIDDEN, env=_environment())
        except OSError as error:
            raise FfmpegNotFound(not_found(ffmpeg)) from error
        self._errors = []
        self._size = 0
        self._gate = threading.Lock()
        self._closed = False
        self._reader = threading.Thread(target=self._read_errors, name="FTRecorder ffmpeg errors", daemon=True)
        self._reader.start()

    def _read_errors(self) -> None:
        for line in self._process.stderr:
            with self._gate:
                if self._size < _ERROR_LIMIT:
                    text = line.decode("utf-8", "replace")
                    self._errors.append(text)
                    self._size += len(text)

    @property
    def errors(self) -> str:
        """What ffmpeg has written to its error output so far (at most 8 KB)."""
        with self._gate:
            return "".join(self._errors).strip()

    def read_exactly(self, size: int) -> bytes | None:
        """size bytes, or None at the end of the input or when ffmpeg stops."""
        out = self._process.stdout
        chunks = []
        left = size
        while left > 0:
            try:
                chunk = out.read(left)
            except (OSError, ValueError):
                return None
            if not chunk:
                return None
            chunks.append(chunk)
            left -= len(chunk)
        return b"".join(chunks)

    def wait(self, timeout: float) -> None:
        try:
            self._process.wait(timeout)
        except subprocess.TimeoutExpired:
            pass
        self._reader.join(2)

    def close(self) -> None:
        """Once, from any thread: "q" asks ffmpeg to stop cleanly (a device is released); it is killed if it does not."""
        with self._gate:
            if self._closed:
                return
            self._closed = True
        if self._process.poll() is None:
            try:
                self._process.stdin.write(b"q")
                self._process.stdin.flush()
            except OSError:
                pass
            try:
                self._process.wait(2)
            except subprocess.TimeoutExpired:
                self._process.kill()
                self._process.wait()
        for pipe in (self._process.stdin, self._process.stdout):
            try:
                pipe.close()
            except OSError:
                pass
        self._reader.join(1)

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self.close()


def open_video(ffmpeg: str | None, video: str, size: int, fps: float) -> Stream:
    """Frames of a video file, fitted into size x size RGB with black borders, at fps."""
    rate = "fps=%s," % _number(fps) if fps > 0 else ""
    scale = "scale=%d:%d:force_original_aspect_ratio=decrease,pad=%d:%d:(ow-iw)/2:(oh-ih)/2" % (size, size, size, size)
    return Stream(ffmpeg, ["-hide_banner", "-loglevel", "error", "-i", video, "-an", "-vf", rate + scale,
                           "-pix_fmt", "rgb24", "-f", "rawvideo", "-"])


def read_audio(ffmpeg: str | None, video: str, sample_rate: int = 16000, cancel: threading.Event | None = None) -> array.array:
    """The audio of a file as mono 16-bit samples ('h' array) at sample_rate; empty when it has none."""
    stream = Stream(ffmpeg, ["-hide_banner", "-loglevel", "error", "-nostdin", "-i", video, "-vn", "-ac", "1",
                             "-ar", str(sample_rate), "-f", "s16le", "-"])
    data = bytearray()
    try:
        while True:
            if cancel is not None and cancel.is_set():
                raise Cancelled()
            chunk = stream._process.stdout.read(65536)
            if not chunk:
                break
            data += chunk
    finally:
        stream.close()
    samples = array.array("h")
    samples.frombytes(bytes(data[:len(data) // 2 * 2]))
    if sys.byteorder == "big":
        samples.byteswap()
    return samples


def open_microphone(ffmpeg: str | None, microphone: "Microphone") -> Stream:
    """Live audio from a microphone as 16 kHz mono 16-bit samples."""
    return Stream(ffmpeg, ["-hide_banner", "-loglevel", "error", "-f", microphone.format, "-i", microphone.device,
                           "-ac", "1", "-ar", "16000", "-f", "s16le", "-"])


class Microphone:
    """A capture device: what the Recorder lists (label), and ffmpeg's input format and device for it."""

    def __init__(self, label: str, format: str, device: str):
        self.label = label
        self.format = format
        self.device = device

    def __repr__(self):
        return self.label


def list_microphones(ffmpeg: str | None) -> list[Microphone]:
    """The microphones ffmpeg can open here: DirectShow on Windows; PipeWire/PulseAudio (through ffmpeg's pulse input,
    which PipeWire serves) and ALSA on Linux. Raises FfmpegNotFound when ffmpeg cannot be started."""
    if os.name == "nt":
        _, errors = run(ffmpeg, ["-hide_banner", "-list_devices", "true", "-f", "dshow", "-i", "dummy"], 15)
        found = []
        for line in errors.splitlines():
            # Lines look like: [dshow @ 000001] "Microphone (Realtek Audio)" (audio)
            if "(audio)" not in line:
                continue
            first, last = line.find('"'), line.rfind('"')
            if 0 <= first < last:
                name = line[first + 1:last]
                found.append(Microphone(name, "dshow", "audio=" + name))
        return found
    found = []
    for name, description in _sources(ffmpeg, "pulse"):
        # A monitor is what a speaker plays, not a microphone.
        if not name.endswith(".monitor"):
            found.append(Microphone("PipeWire/PulseAudio: " + (description or name), "pulse", name))
    for name, description in _sources(ffmpeg, "alsa"):
        # ALSA's default device and its cards (plughw converts formats); its plugins (resamplers, jack, oss) are not
        # microphones.
        if name == "default" or name.startswith("plughw:"):
            found.append(Microphone("ALSA: " + (description or name) + " (" + name + ")", "alsa", name))
    return found


_SOURCE_LINE = re.compile(r"^\s*\*?\s*(\S+)\s+\[(.*)\]")


def _sources(ffmpeg: str | None, format: str) -> list[tuple[str, str]]:
    """ffmpeg -sources FORMAT: (name, description) pairs; empty when the system or ffmpeg has none."""
    try:
        done = subprocess.run([executable(ffmpeg), "-hide_banner", "-sources", format], stdin=subprocess.DEVNULL,
                              stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=15, creationflags=_HIDDEN, env=_environment())
    except OSError as error:
        raise FfmpegNotFound(not_found(ffmpeg)) from error
    except subprocess.TimeoutExpired:
        return []
    pairs = []
    for line in done.stdout.decode("utf-8", "replace").splitlines():
        match = _SOURCE_LINE.match(line)
        if match and not line.startswith("Auto-detected"):
            pairs.append((match.group(1), match.group(2).strip()))
    return pairs


def _number(value: float) -> str:
    return ("%f" % value).rstrip("0").rstrip(".")

# ============================================================================
# GLOSSARY: ffmpeg
# ============================================================================
#
# ffmpeg (this file)       The user's ffmpeg, without a shell (and without a console window on Windows).
#   _environment()           Compiled Linux app: LD_LIBRARY_PATH restored from PyInstaller's LD_LIBRARY_PATH_ORIG for
#                            ffmpeg, so it does not load the bundled libraries.
#   not_found(ffmpeg), FfmpegNotFound  What was looked for and how to install it.
#   Cancelled                Raised when a cancel event stops work.
#   check(ffmpeg)            ffmpeg -version; FfmpegNotFound when it fails.
#   run(ffmpeg, arguments, timeout)  To the end: (exit code, error output).
#   Stream                   A running ffmpeg: read_exactly(size), errors (8 KB), wait(timeout), close() ("q", then kill).
#   open_video(ffmpeg, video, size, fps)  RGB frames fitted into size x size.
#   read_audio(ffmpeg, video, rate, cancel)  The whole sound track, mono s16 ('h' array); empty without one.
#   open_microphone(ffmpeg, microphone)  16 kHz mono s16 from a Microphone.
#   list_microphones(ffmpeg)  Windows: dshow "(audio)" devices. Linux: ffmpeg -sources pulse (PipeWire/PulseAudio;
#                            ".monitor" sources left out) and -sources alsa ("default" and "plughw:" cards only).
#                            Nothing else.
#   Microphone               label (shown), format (dshow, pulse, alsa) and device (ffmpeg's -i).
