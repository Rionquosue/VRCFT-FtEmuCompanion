"""Video frames and audio through the user's own ffmpeg (not bundled). ffmpeg is started with an argument list, never
through a shell, and writes raw data to its standard output: frames as RGB fitted into a square with black borders, and
a file's audio as mono 16-bit samples."""

from __future__ import annotations

import os
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
    """The work was stopped (Ctrl+C)."""


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
