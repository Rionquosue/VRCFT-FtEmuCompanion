"""Unified Expressions by name, and how the names other trackers give (ARKit blendshapes from MediaPipe, the Babble
model's outputs) land on them."""

from __future__ import annotations

UNIFIED_NAMES = [
    "EyeSquintRight", "EyeSquintLeft", "EyeWideRight", "EyeWideLeft",
    "BrowPinchRight", "BrowPinchLeft", "BrowLowererRight", "BrowLowererLeft",
    "BrowInnerUpRight", "BrowInnerUpLeft", "BrowOuterUpRight", "BrowOuterUpLeft",
    "NasalDilationRight", "NasalDilationLeft", "NasalConstrictRight", "NasalConstrictLeft",
    "CheekSquintRight", "CheekSquintLeft", "CheekPuffRight", "CheekPuffLeft", "CheekSuckRight", "CheekSuckLeft",
    "JawOpen", "JawRight", "JawLeft", "JawForward", "JawBackward", "JawClench", "JawMandibleRaise", "MouthClosed",
    "LipSuckUpperRight", "LipSuckUpperLeft", "LipSuckLowerRight", "LipSuckLowerLeft", "LipSuckCornerRight", "LipSuckCornerLeft",
    "LipFunnelUpperRight", "LipFunnelUpperLeft", "LipFunnelLowerRight", "LipFunnelLowerLeft",
    "LipPuckerUpperRight", "LipPuckerUpperLeft", "LipPuckerLowerRight", "LipPuckerLowerLeft",
    "MouthUpperUpRight", "MouthUpperUpLeft", "MouthUpperDeepenRight", "MouthUpperDeepenLeft",
    "NoseSneerRight", "NoseSneerLeft", "MouthLowerDownRight", "MouthLowerDownLeft",
    "MouthUpperRight", "MouthUpperLeft", "MouthLowerRight", "MouthLowerLeft",
    "MouthCornerPullRight", "MouthCornerPullLeft", "MouthCornerSlantRight", "MouthCornerSlantLeft",
    "MouthFrownRight", "MouthFrownLeft", "MouthStretchRight", "MouthStretchLeft", "MouthDimpleRight", "MouthDimpleLeft",
    "MouthRaiserUpper", "MouthRaiserLower", "MouthPressRight", "MouthPressLeft", "MouthTightenerRight", "MouthTightenerLeft",
    "TongueOut", "TongueUp", "TongueDown", "TongueRight", "TongueLeft", "TongueRoll", "TongueBendDown", "TongueCurlUp",
    "TongueSquish", "TongueFlat", "TongueTwistRight", "TongueTwistLeft",
    "SoftPalateClose", "ThroatSwallow", "NeckFlexRight", "NeckFlexLeft",
]

EYE_NAMES = ["LeftGazeX", "LeftGazeY", "RightGazeX", "RightGazeY", "LeftOpenness", "RightOpenness"]
LEFT_GAZE_X, LEFT_GAZE_Y, RIGHT_GAZE_X, RIGHT_GAZE_Y, LEFT_OPENNESS, RIGHT_OPENNESS = range(6)

# tan(30 degrees): how far a full eyeLook value turns the eye.
_FULL_LOOK = 0.577

_INDEX = {name: i for i, name in enumerate(UNIFIED_NAMES)}

# Tracker names (any case) to the Unified Expressions they drive.
_SOURCES = {key.lower(): value for key, value in {
    "browDownLeft": ["BrowLowererLeft", "BrowPinchLeft"],
    "browDownRight": ["BrowLowererRight", "BrowPinchRight"],
    "browInnerUp": ["BrowInnerUpLeft", "BrowInnerUpRight"],
    "browOuterUpLeft": ["BrowOuterUpLeft"],
    "browOuterUpRight": ["BrowOuterUpRight"],
    "cheekPuff": ["CheekPuffLeft", "CheekPuffRight"],
    "cheekPuffLeft": ["CheekPuffLeft"],
    "cheekPuffRight": ["CheekPuffRight"],
    "cheekSuckLeft": ["CheekSuckLeft"],
    "cheekSuckRight": ["CheekSuckRight"],
    "cheekSquintLeft": ["CheekSquintLeft"],
    "cheekSquintRight": ["CheekSquintRight"],
    "eyeSquintLeft": ["EyeSquintLeft"],
    "eyeSquintRight": ["EyeSquintRight"],
    "eyeWideLeft": ["EyeWideLeft"],
    "eyeWideRight": ["EyeWideRight"],
    "jawForward": ["JawForward"],
    "jawLeft": ["JawLeft"],
    "jawRight": ["JawRight"],
    "jawOpen": ["JawOpen"],
    "mouthClose": ["MouthClosed"],
    "mouthFunnel": ["LipFunnelUpperLeft", "LipFunnelUpperRight", "LipFunnelLowerLeft", "LipFunnelLowerRight"],
    "mouthPucker": ["LipPuckerUpperLeft", "LipPuckerUpperRight", "LipPuckerLowerLeft", "LipPuckerLowerRight"],
    "mouthLeft": ["MouthUpperLeft", "MouthLowerLeft"],
    "mouthRight": ["MouthUpperRight", "MouthLowerRight"],
    "mouthRollUpper": ["LipSuckUpperLeft", "LipSuckUpperRight"],
    "mouthRollLower": ["LipSuckLowerLeft", "LipSuckLowerRight"],
    "mouthShrugUpper": ["MouthRaiserUpper"],
    "mouthShrugLower": ["MouthRaiserLower"],
    "mouthSmileLeft": ["MouthCornerPullLeft", "MouthCornerSlantLeft"],
    "mouthSmileRight": ["MouthCornerPullRight", "MouthCornerSlantRight"],
    "mouthFrownLeft": ["MouthFrownLeft"],
    "mouthFrownRight": ["MouthFrownRight"],
    "mouthDimpleLeft": ["MouthDimpleLeft"],
    "mouthDimpleRight": ["MouthDimpleRight"],
    "mouthUpperUpLeft": ["MouthUpperUpLeft"],
    "mouthUpperUpRight": ["MouthUpperUpRight"],
    "mouthLowerDownLeft": ["MouthLowerDownLeft"],
    "mouthLowerDownRight": ["MouthLowerDownRight"],
    "mouthPressLeft": ["MouthPressLeft"],
    "mouthPressRight": ["MouthPressRight"],
    "mouthStretchLeft": ["MouthStretchLeft"],
    "mouthStretchRight": ["MouthStretchRight"],
    "noseSneerLeft": ["NoseSneerLeft"],
    "noseSneerRight": ["NoseSneerRight"],
    "tongueOut": ["TongueOut"],
    "tongueUp": ["TongueUp"],
    "tongueDown": ["TongueDown"],
    "tongueLeft": ["TongueLeft"],
    "tongueRight": ["TongueRight"],
    "tongueRoll": ["TongueRoll"],
    "tongueBendDown": ["TongueBendDown"],
    "tongueCurlUp": ["TongueCurlUp"],
    "tongueSquish": ["TongueSquish"],
    "tongueFlat": ["TongueFlat"],
    "tongueTwistLeft": ["TongueTwistLeft"],
    "tongueTwistRight": ["TongueTwistRight"],
}.items()}


def clamp01(value: float) -> float:
    if value != value or value < 0.0:
        return 0.0
    return 1.0 if value > 1.0 else value


def from_arkit(names: list[str], values, unified: list[float], eye: list[float]) -> None:
    """MediaPipe's 52 values (names in blendshape order) into Unified Expressions and eyes. Every slot is written:
    shapes MediaPipe does not give are 0."""
    for i in range(len(unified)):
        unified[i] = 0.0
    look = {}
    for name, raw in zip(names, values):
        value = clamp01(float(raw))
        if name.startswith("eyeLook") or name.startswith("eyeBlink"):
            look[name] = value
            continue
        add(name, value, unified)
    get = look.get
    # The left eye looks in towards the nose, which is to the subject's right (+x).
    eye[LEFT_GAZE_X] = (get("eyeLookInLeft", 0.0) - get("eyeLookOutLeft", 0.0)) * _FULL_LOOK
    eye[LEFT_GAZE_Y] = (get("eyeLookUpLeft", 0.0) - get("eyeLookDownLeft", 0.0)) * _FULL_LOOK
    eye[RIGHT_GAZE_X] = (get("eyeLookOutRight", 0.0) - get("eyeLookInRight", 0.0)) * _FULL_LOOK
    eye[RIGHT_GAZE_Y] = (get("eyeLookUpRight", 0.0) - get("eyeLookDownRight", 0.0)) * _FULL_LOOK
    eye[LEFT_OPENNESS] = 1.0 - get("eyeBlinkLeft", 0.0)
    eye[RIGHT_OPENNESS] = 1.0 - get("eyeBlinkRight", 0.0)


def add(source: str, value: float, unified: list[float]) -> bool:
    """One tracker value into every slot its name drives (kept at the largest value given). False for an unknown name."""
    targets = _SOURCES.get((source or "").lower())
    if targets is None:
        return False
    value = clamp01(value)
    for name in targets:
        slot = _INDEX[name]
        if value > unified[slot]:
            unified[slot] = value
    return True


def set_value(source: str, value: float, unified: list[float]) -> bool:
    """Like add, but replaces what is in the slots: for a tracker that overrides another (Babble over MediaPipe)."""
    targets = _SOURCES.get((source or "").lower())
    if targets is None:
        return False
    value = clamp01(value)
    for name in targets:
        unified[_INDEX[name]] = value
    return True

# ============================================================================
# GLOSSARY: expressions
# ============================================================================
#
# expressions (this file)  The same table as the C# module's FTRecorderExpressionMap.
#   UNIFIED_NAMES            Every Unified Expression, in VRCFaceTracking's enum order (a recording's "unified").
#   EYE_NAMES                LeftGazeX/Y, RightGazeX/Y (tangents, x right, y up), Left/RightOpenness (0 to 1).
#   from_arkit(names, values, unified, eye)  MediaPipe's blendshapes; eyeLook* as gaze (a full look is tan 30 degrees),
#                            eyeBlink* as 1 - openness; the rest through add.
#   add(name, value, unified)  Raises the slots name drives to value. set_value(...) replaces them (Babble).
