"""RiryTools FTRecorder: the Recorder window and the companion command line (process, replay).

The VRCFaceTracking module (FTRecorder.Module, C#) and FTEmulator in Unity are separate; this package shares only the
recording format and the bundled models (Models/) with them.
"""

VERSION = "0.1.0"

# ============================================================================
# GLOSSARY: ftrecorder package
# ============================================================================
#
# ftrecorder (this package)  Python 3.10 or newer. The Recorder needs only the standard library (tkinter); process also
#                            needs numpy and onnxruntime (requirements.txt). ffmpeg is the user's own.
#   VERSION                  The package version, matching the module's.
