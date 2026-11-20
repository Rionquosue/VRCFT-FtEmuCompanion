"""Plays mono 16-bit audio to one Windows playback device, chosen by name, through the Windows multimedia API (winmm).
Used to feed a virtual audio cable that VRChat takes as its microphone."""

from __future__ import annotations

import array
import ctypes
import os
import threading

_SUCCESS = 0
_DONE = 1


def devices() -> list[str]:
    """Every playback device's name (Windows cuts names to 31 characters); empty off Windows."""
    if os.name != "nt":
        return []
    winmm = ctypes.WinDLL("winmm")
    names = []
    for index in range(winmm.waveOutGetNumDevs()):
        caps = _Caps()
        if winmm.waveOutGetDevCapsW(ctypes.c_size_t(index), ctypes.byref(caps), ctypes.sizeof(caps)) == _SUCCESS:
            names.append(caps.name)
    return names


class _Caps(ctypes.Structure):
    _fields_ = [("manufacturer", ctypes.c_ushort), ("product", ctypes.c_ushort), ("driver_version", ctypes.c_uint),
                ("name", ctypes.c_wchar * 32), ("formats", ctypes.c_uint), ("channels", ctypes.c_ushort),
                ("reserved", ctypes.c_ushort), ("support", ctypes.c_uint)]


class _Format(ctypes.Structure):
    _fields_ = [("tag", ctypes.c_ushort), ("channels", ctypes.c_ushort), ("sample_rate", ctypes.c_uint),
                ("bytes_per_second", ctypes.c_uint), ("block_align", ctypes.c_ushort), ("bits_per_sample", ctypes.c_ushort),
                ("extra", ctypes.c_ushort)]


class _Header(ctypes.Structure):
    _fields_ = [("data", ctypes.c_void_p), ("length", ctypes.c_uint), ("recorded", ctypes.c_uint), ("user", ctypes.c_void_p),
                ("flags", ctypes.c_uint), ("loops", ctypes.c_uint), ("next", ctypes.c_void_p), ("reserved", ctypes.c_void_p)]


def play(device: str, samples: array.array, sample_rate: int, started, cancel: threading.Event) -> None:
    """Plays samples on the first device whose name contains device (any case); returns when they have played or
    cancel is set. started() is called the moment playback is handed to Windows."""
    if os.name != "nt":
        raise RuntimeError("Playing into VRChat needs Windows.")
    names = devices()
    index = next((i for i, name in enumerate(names) if device.lower() in name.lower()), -1)
    if index < 0:
        raise RuntimeError('No playback device named like "%s" (found: %s). Install a virtual audio cable, set VRChat\'s '
                           "microphone to its output, and pass its input with --cable." % (device, ", ".join(names) or "none"))
    winmm = ctypes.WinDLL("winmm")
    handle = ctypes.c_void_p()
    form = _Format(1, 1, sample_rate, sample_rate * 2, 2, 16, 0)
    result = winmm.waveOutOpen(ctypes.byref(handle), ctypes.c_size_t(index), ctypes.byref(form), None, None, 0)
    if result != _SUCCESS:
        raise RuntimeError('"%s" could not be opened (error %d).' % (names[index], result))
    data = ctypes.create_string_buffer(samples.tobytes(), max(2, len(samples) * 2))
    header = _Header(ctypes.cast(data, ctypes.c_void_p), len(samples) * 2, 0, None, 0, 0, None, None)
    size = ctypes.sizeof(header)
    try:
        winmm.waveOutPrepareHeader(handle, ctypes.byref(header), size)
        started()
        winmm.waveOutWrite(handle, ctypes.byref(header), size)
        while not header.flags & _DONE:
            if cancel.wait(0.02):
                break
        winmm.waveOutReset(handle)
        winmm.waveOutUnprepareHeader(handle, ctypes.byref(header), size)
    finally:
        winmm.waveOutClose(handle)

# ============================================================================
# GLOSSARY: playback
# ============================================================================
#
# playback (this file)     winmm playback (Windows), for process's VRChat visemes.
#   devices()                Playback device names (31 characters at most).
#   play(device, samples, rate, started, cancel)  The first device whose name contains device; one buffer of mono s16;
#                            started() when handed to Windows; returns when done or cancelled. RuntimeError off Windows
#                            or when no device matches (the message lists them).
