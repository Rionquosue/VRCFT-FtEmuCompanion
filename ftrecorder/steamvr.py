"""Start with SteamVR: registers the Recorder with SteamVR as an overlay application (a .vrmanifest) and turns its
auto-launch on or off, through Valve's openvr_api.dll (bundled in ThirdParty/OpenVR, BSD 3-Clause). Windows only."""

from __future__ import annotations

import ctypes
import json
import os
import sys

APP_KEY = "riry.rirytools.ftrecorder.recorder"
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
LAUNCHER = os.path.join(ROOT, "Recorder.pyw")
LIBRARY = os.path.join(ROOT, "ThirdParty", "OpenVR", "openvr_api.dll")
_INTERFACE = b"FnTable:IVRApplications_008"
_BACKGROUND = 3        # EVRApplicationType: does not start SteamVR
_UTILITY = 4           # EVRApplicationType: settings access, no hardware
_NO_SERVER_FOR_BACKGROUND_APP = 121
# IVRApplications_008's function table, in openvr.h's order.
_ADD_MANIFEST, _REMOVE_MANIFEST, _ERROR_NAME, _SET_AUTO_LAUNCH, _GET_AUTO_LAUNCH = 0, 1, 13, 17, 18

_library = None


def manifest_path() -> str:
    """%LocalAppData%\\RiryTools\\FTRecorder\\RiryTools.FTRecorder.Recorder.vrmanifest."""
    base = os.environ.get("LOCALAPPDATA") or os.path.expanduser("~")
    return os.path.join(base, "RiryTools", "FTRecorder", "RiryTools.FTRecorder.Recorder.vrmanifest")


def is_auto_launch() -> bool | None:
    """Whether SteamVR starts the Recorder; None when SteamVR is not running (it is not started to find out)."""
    table = _open(_BACKGROUND)
    if table is None:
        return None
    try:
        key = ctypes.c_char_p(APP_KEY.encode())
        return bool(ctypes.CFUNCTYPE(ctypes.c_bool, ctypes.c_char_p)(table[_GET_AUTO_LAUNCH])(key))
    finally:
        _shutdown()


def set_auto_launch(on: bool) -> None:
    """On: writes the manifest, registers it and turns auto-launch on. Off: turns auto-launch off and unregisters it.
    Needs SteamVR's server, which OpenVR starts when it is not running."""
    if on:
        _write_manifest()
    table = _open(_UTILITY)
    try:
        path = ctypes.c_char_p(manifest_path().encode("utf-8"))
        key = ctypes.c_char_p(APP_KEY.encode())
        add = ctypes.CFUNCTYPE(ctypes.c_int, ctypes.c_char_p, ctypes.c_bool)(table[_ADD_MANIFEST])
        remove = ctypes.CFUNCTYPE(ctypes.c_int, ctypes.c_char_p)(table[_REMOVE_MANIFEST])
        auto = ctypes.CFUNCTYPE(ctypes.c_int, ctypes.c_char_p, ctypes.c_bool)(table[_SET_AUTO_LAUNCH])
        if on:
            _check(table, add(path, False), "registering the Recorder")
            _check(table, auto(key, True), "turning on its auto-launch")
        else:
            _check(table, auto(key, False), "turning off its auto-launch")
            _check(table, remove(path), "unregistering the Recorder")
            try:
                os.remove(manifest_path())
            except OSError:
                pass
    finally:
        _shutdown()


def _write_manifest() -> None:
    """An overlay application (SteamVR only auto-launches those) that starts the compiled .exe, or, run from source,
    Recorder.pyw with this Python's pythonw."""
    application = {"app_key": APP_KEY, "launch_type": "binary"}
    if getattr(sys, "frozen", False):
        application["binary_path_windows"] = sys.executable
    else:
        windowless = os.path.join(os.path.dirname(sys.executable), "pythonw.exe")
        application["binary_path_windows"] = windowless if os.path.isfile(windowless) else sys.executable
        application["arguments"] = '"%s"' % LAUNCHER
    application.update({
        "is_dashboard_overlay": True,
        "strings": {"en_us": {"name": "RiryTools FTRecorder Recorder",
                              "description": "Relays VRCFaceTracking's OSC output to VRChat and records it on demand."}},
    })
    manifest = {"applications": [application]}
    os.makedirs(os.path.dirname(manifest_path()), exist_ok=True)
    with open(manifest_path(), "w", encoding="utf-8") as file:
        json.dump(manifest, file, indent=2)


def _open(kind: int):
    """VR_InitInternal2 and the IVRApplications function table; None for a Background start without SteamVR."""
    global _library
    if os.name != "nt":
        raise RuntimeError("Starting with SteamVR is for Windows.")
    if _library is None:
        try:
            _library = ctypes.CDLL(LIBRARY)
        except OSError as error:
            raise RuntimeError("openvr_api.dll could not be loaded from %s: %s" % (LIBRARY, error)) from error
        _library.VR_InitInternal2.restype = ctypes.c_uint32
        _library.VR_InitInternal2.argtypes = [ctypes.POINTER(ctypes.c_int), ctypes.c_int, ctypes.c_char_p]
        _library.VR_GetGenericInterface.restype = ctypes.c_void_p
        _library.VR_GetGenericInterface.argtypes = [ctypes.c_char_p, ctypes.POINTER(ctypes.c_int)]
        _library.VR_GetVRInitErrorAsEnglishDescription.restype = ctypes.c_char_p
        _library.VR_GetVRInitErrorAsEnglishDescription.argtypes = [ctypes.c_int]
        _library.VR_ShutdownInternal.restype = None
    error = ctypes.c_int(0)
    _library.VR_InitInternal2(ctypes.byref(error), kind, b"")
    if error.value == _NO_SERVER_FOR_BACKGROUND_APP:
        return None
    if error.value != 0:
        raise RuntimeError("SteamVR could not be reached: %s." % _init_error(error.value))
    table = _library.VR_GetGenericInterface(_INTERFACE, ctypes.byref(error))
    if error.value != 0 or not table:
        _shutdown()
        raise RuntimeError("SteamVR's application settings are unavailable: %s." % _init_error(error.value))
    return ctypes.cast(table, ctypes.POINTER(ctypes.c_void_p))


def _shutdown() -> None:
    _library.VR_ShutdownInternal()


def _init_error(error: int) -> str:
    text = _library.VR_GetVRInitErrorAsEnglishDescription(error)
    return "%s (%d)" % (text.decode("utf-8", "replace") if text else "error", error)


def _check(table, error: int, what: str) -> None:
    if error == 0:
        return
    name = ctypes.CFUNCTYPE(ctypes.c_char_p, ctypes.c_int)(table[_ERROR_NAME])(error)
    raise RuntimeError("SteamVR refused %s: %s (%d)." % (what, name.decode("utf-8", "replace") if name else "error", error))

# ============================================================================
# GLOSSARY: SteamVR
# ============================================================================
#
# steamvr (this file)      The Recorder's Start with SteamVR (Windows), through ThirdParty/OpenVR/openvr_api.dll.
#   APP_KEY                  riry.rirytools.ftrecorder.recorder.
#   manifest_path()          %LocalAppData%/RiryTools/FTRecorder/RiryTools.FTRecorder.Recorder.vrmanifest: an overlay
#                            application (is_dashboard_overlay) launching the compiled .exe, or, from source, pythonw.exe
#                            (this Python's) with Recorder.pyw.
#   is_auto_launch()         GetApplicationAutoLaunch as a Background application: None when SteamVR is not running.
#   set_auto_launch(on)      On: manifest written, AddApplicationManifest (permanent), SetApplicationAutoLaunch(true).
#                            Off: SetApplicationAutoLaunch(false), RemoveApplicationManifest, manifest deleted. As a
#                            Utility application (starts SteamVR's server when needed). RuntimeError with SteamVR's error.
#   Function table           IVRApplications_008 (OpenVR SDK 2.15.6): Add 0, Remove 1, GetApplicationsErrorNameFromEnum 13,
#                            SetApplicationAutoLaunch 17, GetApplicationAutoLaunch 18.
