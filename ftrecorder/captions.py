"""The viseme subtitle track of an .mkv recording, as SRT: one caption each time the viseme data changes, lasting
until the next. A caption holds everything known at that moment: the viseme's index and name, Voice, and the 15 weights
when OVRLipSync or the classifier made them (VRChat sends only Viseme and Voice)."""

from __future__ import annotations

from . import recording as rec

NAMES = ["sil", "PP", "FF", "TH", "DD", "kk", "CH", "SS", "nn", "RR", "aa", "E", "ih", "oh", "ou"]
_LAST_LENGTH = 0.5


def build(recording: rec.Recording) -> str | None:
    """SRT text for the recording's visemes; None when it has none."""
    moments = []
    viseme, voice = -1, 0.0
    for entry in recording.entries:
        if entry.kind == rec.VISEME:
            moments.append((entry.time, _text(entry.viseme, entry.voice, entry.weights)))
        elif entry.kind == rec.PARAMETER and entry.name in ("Viseme", "Voice"):
            if entry.name == "Viseme":
                viseme = int(entry.value)
            else:
                voice = entry.value
            moments.append((entry.time, _text(viseme, voice, None)))
    if not moments:
        return None
    moments.sort(key=lambda moment: moment[0])
    lines = []
    number = 0
    for i, (start, text) in enumerate(moments):
        end = moments[i + 1][0] if i + 1 < len(moments) else start + _LAST_LENGTH
        # Values that arrive together (Viseme and Voice in one packet) make one caption: the last one.
        if end <= start:
            continue
        number += 1
        lines.append("%d\n%s --> %s\n%s\n\n" % (number, _clock(start), _clock(end), text))
    return "".join(lines)


def _text(viseme: int, voice: float, weights) -> str:
    head = ("%d %s" % (viseme, NAMES[viseme] if viseme < len(NAMES) else "?")) if viseme >= 0 else "-"
    text = "%s  voice %.2f" % (head, voice)
    if weights:
        text += "\n" + " ".join("%s %.2f" % (NAMES[k], weights[k]) for k in range(min(len(weights), len(NAMES))))
    return text


def _clock(seconds: float) -> str:
    milliseconds = int(round(max(0.0, seconds) * 1000))
    hours, rest = divmod(milliseconds, 3600000)
    minutes, rest = divmod(rest, 60000)
    secs, millis = divmod(rest, 1000)
    return "%02d:%02d:%02d,%03d" % (hours, minutes, secs, millis)

# ============================================================================
# GLOSSARY: captions
# ============================================================================
#
# captions (this file)     The "Visemes" subtitle track.
#   build(recording)         SRT: a caption per change (each viseme entry; each VRChat Viseme or Voice value), until
#                            the next change (the last lasts 0.5 s). Line 1: "<index> <name>  voice <0.00>"; line 2 (when
#                            weights were made): the 15 weights by name. None when there are no visemes.
#   NAMES                    VRChat's viseme order: sil PP FF TH DD kk CH SS nn RR aa E ih oh ou.
