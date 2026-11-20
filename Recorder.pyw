"""Opens the RiryTools FTRecorder Recorder window. On Windows, double-click it (it runs with pythonw, without a console);
anywhere, python Recorder.pyw. SteamVR's Start with SteamVR launches this file."""

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from ftrecorder.recorder import main  # noqa: E402

main()

# ============================================================================
# GLOSSARY: Recorder launcher
# ============================================================================
#
# Recorder.pyw (this file)  Puts this folder on the import path and opens ftrecorder.recorder's window.
