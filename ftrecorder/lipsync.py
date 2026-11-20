"""Meta's OVRLipSync library, the viseme engine VRChat uses, loaded from a file the user supplies (by default VRChat's
own copy; it is not bundled). Windows only: VRChat ships it as a Windows DLL. Both builds are accepted: the Unity
plugin's (functions named ovrLipSyncDll_*) and the native SDK's (ovrLipSync_*). Audio goes in as 16-bit mono at
16 kHz, 10 ms at a time."""

from __future__ import annotations

import array
import ctypes
import math
import os

from . import vrchat

SAMPLE_RATE = 16000
HOP = 160
VISEME_COUNT = 15
_SUCCESS = 0
_PROVIDER_ORIGINAL, _PROVIDER_ENHANCED = 0, 1
_MONO_SHORT = 0


class LipSyncError(Exception):
    """OVRLipSync could not be found, loaded or used."""


class _NativeFrame(ctypes.Structure):
    _fields_ = [("frameNumber", ctypes.c_int), ("frameDelay", ctypes.c_int), ("visemes", ctypes.POINTER(ctypes.c_float)),
                ("visemesLength", ctypes.c_uint), ("laughterScore", ctypes.c_float),
                ("laughterCategories", ctypes.POINTER(ctypes.c_float)), ("laughterCategoriesLength", ctypes.c_uint)]


class OvrLipSync:
    def __init__(self, path: str):
        if not os.path.isfile(path):
            raise LipSyncError("The OVRLipSync library was not found: " + path)
        try:
            self._library = ctypes.CDLL(os.path.abspath(path))
        except OSError as error:
            raise LipSyncError(str(error)) from error
        self._unity = hasattr(self._library, "ovrLipSyncDll_Initialize")
        prefix = "ovrLipSyncDll_" if self._unity else "ovrLipSync_"
        initialize = self._export(prefix + "Initialize", [ctypes.c_int, ctypes.c_int])
        self._shutdown = self._export(prefix + "Shutdown", [])
        self._destroy = self._export(prefix + "DestroyContext", [ctypes.c_uint])
        create = self._export(prefix + "CreateContextEx", [ctypes.POINTER(ctypes.c_uint), ctypes.c_int, ctypes.c_int, ctypes.c_int])
        if self._unity:
            self._process = self._export(prefix + "ProcessFrameEx", [
                ctypes.c_uint, ctypes.POINTER(ctypes.c_short), ctypes.c_uint, ctypes.c_int, ctypes.POINTER(ctypes.c_int),
                ctypes.POINTER(ctypes.c_int), ctypes.POINTER(ctypes.c_float), ctypes.c_int, ctypes.POINTER(ctypes.c_float),
                ctypes.POINTER(ctypes.c_float), ctypes.c_int])
        else:
            self._process = self._export(prefix + "ProcessFrameEx", [
                ctypes.c_uint, ctypes.POINTER(ctypes.c_short), ctypes.c_int, ctypes.c_int, ctypes.POINTER(_NativeFrame)])
        result = initialize(SAMPLE_RATE, 1024)
        if result != _SUCCESS:
            raise LipSyncError("OVRLipSync did not initialise (error %d)." % result)
        context = ctypes.c_uint(0)
        result = create(ctypes.byref(context), _PROVIDER_ENHANCED, SAMPLE_RATE, 0)
        if result != _SUCCESS:
            result = create(ctypes.byref(context), _PROVIDER_ORIGINAL, SAMPLE_RATE, 0)
        if result != _SUCCESS:
            self._shutdown()
            raise LipSyncError("OVRLipSync could not make a context (error %d)." % result)
        self._context = context.value
        self._visemes = (ctypes.c_float * VISEME_COUNT)()
        self._laughter = (ctypes.c_float * 16)()
        self._closed = False

    def _export(self, name, arguments):
        try:
            function = getattr(self._library, name)
        except AttributeError:
            raise LipSyncError("The OVRLipSync library has no %s: it is not a supported OVRLipSync build." % name) from None
        function.argtypes = arguments
        function.restype = ctypes.c_int if name.endswith(("Initialize", "DestroyContext", "CreateContextEx", "ProcessFrameEx")) else None
        return function

    def process(self, samples: array.array) -> list[float]:
        """The 15 viseme weights (VRChat's order) after one block of 16-bit samples."""
        audio = (ctypes.c_short * len(samples)).from_buffer_copy(samples.tobytes())
        if self._unity:
            number, delay, laughter_score = ctypes.c_int(0), ctypes.c_int(0), ctypes.c_float(0)
            result = self._process(self._context, audio, len(samples), _MONO_SHORT, ctypes.byref(number), ctypes.byref(delay),
                                   self._visemes, VISEME_COUNT, ctypes.byref(laughter_score), self._laughter, 16)
        else:
            frame = _NativeFrame(0, 0, self._visemes, VISEME_COUNT, 0.0, self._laughter, 16)
            result = self._process(self._context, audio, len(samples), _MONO_SHORT, ctypes.byref(frame))
        if result != _SUCCESS:
            raise LipSyncError("OVRLipSync could not process audio (error %d)." % result)
        return list(self._visemes)

    def close(self) -> None:
        if self._closed:
            return
        self._closed = True
        self._destroy(self._context)
        self._shutdown()


def block_viseme(weights: list[float], samples: array.array) -> tuple[int, float]:
    """The strongest viseme and Voice for one block: Voice is loudness, -55 dBFS as 0 and -25 dBFS as 1."""
    best = max(range(len(weights)), key=lambda k: (weights[k], -k))
    energy = sum(float(s) * s for s in samples)
    db = 10 * math.log10(energy / max(1, len(samples)) / (32768.0 * 32768.0) + 1e-12)
    return best, min(1.0, max(0.0, (db + 55) / 30))


def open_ovrlipsync(path: str | None, ask=None) -> tuple[OvrLipSync, str]:
    """OVRLipSync from path when given, else VRChat's copy. When that fails and ask is set, ask(reason) returns another
    path (None or empty to give up). Raises LipSyncError, with the reason, when nothing loads. Windows only."""
    if os.name != "nt":
        raise LipSyncError("OVRLipSync is only available on Windows (VRChat ships it as a Windows DLL).")
    if not path or not path.strip():
        path, searched = vrchat.find_ovrlipsync()
        if path is None:
            where = "no VRChat install was found through Steam" if not searched else "none in " + ", ".join(searched)
            path = _ask(ask, "OVRLipSync.dll was not found in an installed VRChat (%s)." % where)
    while True:
        try:
            return OvrLipSync(path), path
        except LipSyncError as error:
            path = _ask(ask, 'OVRLipSync could not be loaded from "%s": %s' % (path, error))


def _ask(ask, reason: str) -> str:
    hint = " Give the path with --ovrlipsync (the Recorder's OVRLipSync.dll field), or use --visemes classifier."
    if ask is None:
        raise LipSyncError(reason + hint)
    answer = ask(reason)
    if not answer or not answer.strip():
        raise LipSyncError("No OVRLipSync path was given.")
    return answer.strip().strip('"')

# ============================================================================
# GLOSSARY: OVRLipSync
# ============================================================================
#
# lipsync (this file)      OVRLipSync through ctypes (Windows; the user's copy, VRChat's by default).
#   OvrLipSync(path)         Loads the Unity plugin build (ovrLipSyncDll_*) or the native SDK build (ovrLipSync_*):
#                            Initialize(16000, 1024), CreateContextEx (enhanced provider, else original).
#                            process(samples) gives the 15 weights of a block; close() destroys the context and shuts
#                            the library down.
#   block_viseme(weights, samples)  (strongest viseme, Voice: -55 to -25 dBFS as 0 to 1).
#   open_ovrlipsync(path, ask)  path, else vrchat.find_ovrlipsync(); on failure ask(reason) for another path until one
#                            loads or none is given (LipSyncError). LipSyncError straight away off Windows.
#   SAMPLE_RATE, HOP         16000 Hz, 160 samples (10 ms).
