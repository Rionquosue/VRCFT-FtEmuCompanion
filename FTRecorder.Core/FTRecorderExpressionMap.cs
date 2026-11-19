using System;
using System.Collections.Generic;

namespace Riry.RiryTools.FTRecorder
{
    /// <summary>
    /// Unified Expressions by name, and how the names other trackers give (ARKit blendshapes from MediaPipe, and the
    /// Babble model's and Babble app's outputs) land on them. Names, not VRCFaceTracking's enum, so the companion app
    /// needs nothing from VRCFaceTracking; the module turns each name into its enum value once.
    /// </summary>
    internal static class FTRecorderExpressionMap
    {
        /// <summary>Every Unified Expression, in VRCFaceTracking's enum order. A recording's "unified" list.</summary>
        internal static readonly string[] UnifiedNames =
        {
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
        };

        /// <summary>The eye values a recording keeps, in this order. Gaze is a tangent (x right, y up); openness 0 to 1.</summary>
        internal static readonly string[] EyeNames = { "LeftGazeX", "LeftGazeY", "RightGazeX", "RightGazeY", "LeftOpenness", "RightOpenness" };

        internal const int LeftGazeX = 0, LeftGazeY = 1, RightGazeX = 2, RightGazeY = 3, LeftOpenness = 4, RightOpenness = 5;

        /// <summary>tan(30°): how far a full eyeLook value turns the eye.</summary>
        private const float FullLook = 0.577f;

        private static readonly Dictionary<string, int> UnifiedIndex = BuildIndex();

        // Tracker names (any case) to the Unified Expressions they drive. Shapes a tracker gives once for both sides or
        // for a whole lip drive every Unified Expression that shares it.
        private static readonly Dictionary<string, string[]> Sources = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "browDownLeft", new[] { "BrowLowererLeft", "BrowPinchLeft" } },
            { "browDownRight", new[] { "BrowLowererRight", "BrowPinchRight" } },
            { "browInnerUp", new[] { "BrowInnerUpLeft", "BrowInnerUpRight" } },
            { "browOuterUpLeft", new[] { "BrowOuterUpLeft" } },
            { "browOuterUpRight", new[] { "BrowOuterUpRight" } },
            { "cheekPuff", new[] { "CheekPuffLeft", "CheekPuffRight" } },
            { "cheekPuffLeft", new[] { "CheekPuffLeft" } },
            { "cheekPuffRight", new[] { "CheekPuffRight" } },
            { "cheekSuckLeft", new[] { "CheekSuckLeft" } },
            { "cheekSuckRight", new[] { "CheekSuckRight" } },
            { "cheekSquintLeft", new[] { "CheekSquintLeft" } },
            { "cheekSquintRight", new[] { "CheekSquintRight" } },
            { "eyeSquintLeft", new[] { "EyeSquintLeft" } },
            { "eyeSquintRight", new[] { "EyeSquintRight" } },
            { "eyeWideLeft", new[] { "EyeWideLeft" } },
            { "eyeWideRight", new[] { "EyeWideRight" } },
            { "jawForward", new[] { "JawForward" } },
            { "jawLeft", new[] { "JawLeft" } },
            { "jawRight", new[] { "JawRight" } },
            { "jawOpen", new[] { "JawOpen" } },
            { "mouthClose", new[] { "MouthClosed" } },
            { "mouthFunnel", new[] { "LipFunnelUpperLeft", "LipFunnelUpperRight", "LipFunnelLowerLeft", "LipFunnelLowerRight" } },
            { "mouthPucker", new[] { "LipPuckerUpperLeft", "LipPuckerUpperRight", "LipPuckerLowerLeft", "LipPuckerLowerRight" } },
            { "mouthLeft", new[] { "MouthUpperLeft", "MouthLowerLeft" } },
            { "mouthRight", new[] { "MouthUpperRight", "MouthLowerRight" } },
            { "mouthRollUpper", new[] { "LipSuckUpperLeft", "LipSuckUpperRight" } },
            { "mouthRollLower", new[] { "LipSuckLowerLeft", "LipSuckLowerRight" } },
            { "mouthShrugUpper", new[] { "MouthRaiserUpper" } },
            { "mouthShrugLower", new[] { "MouthRaiserLower" } },
            { "mouthSmileLeft", new[] { "MouthCornerPullLeft", "MouthCornerSlantLeft" } },
            { "mouthSmileRight", new[] { "MouthCornerPullRight", "MouthCornerSlantRight" } },
            { "mouthFrownLeft", new[] { "MouthFrownLeft" } },
            { "mouthFrownRight", new[] { "MouthFrownRight" } },
            { "mouthDimpleLeft", new[] { "MouthDimpleLeft" } },
            { "mouthDimpleRight", new[] { "MouthDimpleRight" } },
            { "mouthUpperUpLeft", new[] { "MouthUpperUpLeft" } },
            { "mouthUpperUpRight", new[] { "MouthUpperUpRight" } },
            { "mouthLowerDownLeft", new[] { "MouthLowerDownLeft" } },
            { "mouthLowerDownRight", new[] { "MouthLowerDownRight" } },
            { "mouthPressLeft", new[] { "MouthPressLeft" } },
            { "mouthPressRight", new[] { "MouthPressRight" } },
            { "mouthStretchLeft", new[] { "MouthStretchLeft" } },
            { "mouthStretchRight", new[] { "MouthStretchRight" } },
            { "noseSneerLeft", new[] { "NoseSneerLeft" } },
            { "noseSneerRight", new[] { "NoseSneerRight" } },
            { "tongueOut", new[] { "TongueOut" } },
            { "tongueUp", new[] { "TongueUp" } },
            { "tongueDown", new[] { "TongueDown" } },
            { "tongueLeft", new[] { "TongueLeft" } },
            { "tongueRight", new[] { "TongueRight" } },
            { "tongueRoll", new[] { "TongueRoll" } },
            { "tongueBendDown", new[] { "TongueBendDown" } },
            { "tongueCurlUp", new[] { "TongueCurlUp" } },
            { "tongueSquish", new[] { "TongueSquish" } },
            { "tongueFlat", new[] { "TongueFlat" } },
            { "tongueTwistLeft", new[] { "TongueTwistLeft" } },
            { "tongueTwistRight", new[] { "TongueTwistRight" } },
        };

        private static Dictionary<string, int> BuildIndex()
        {
            var index = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < UnifiedNames.Length; i++) index[UnifiedNames[i]] = i;
            return index;
        }

        /// <summary>The Unified Expression slots a tracker name drives, or null for a name with none (eyes, _neutral).</summary>
        internal static int[] Targets(string sourceName)
        {
            if (sourceName == null || !Sources.TryGetValue(sourceName, out string[] names)) return null;
            var targets = new int[names.Length];
            for (int i = 0; i < names.Length; i++) targets[i] = UnifiedIndex[names[i]];
            return targets;
        }

        /// <summary>The slot of one Unified Expression name, or -1.</summary>
        internal static int IndexOf(string unifiedName)
        {
            return unifiedName != null && UnifiedIndex.TryGetValue(unifiedName, out int index) ? index : -1;
        }

        /// <summary>
        /// MediaPipe's 52 values (names in blendshape order, index 0 "_neutral") into Unified Expressions and eyes.
        /// Every slot of unified and eye is written: shapes MediaPipe does not give are 0.
        /// </summary>
        internal static void FromArkit(string[] names, float[] values, float[] unified, float[] eye)
        {
            Array.Clear(unified, 0, unified.Length);
            float lookInL = 0, lookOutL = 0, lookUpL = 0, lookDownL = 0, lookInR = 0, lookOutR = 0, lookUpR = 0, lookDownR = 0;
            float blinkL = 0, blinkR = 0;
            for (int i = 0; i < names.Length && i < values.Length; i++)
            {
                float value = Clamp01(values[i]);
                switch (names[i])
                {
                    case "eyeLookInLeft": lookInL = value; continue;
                    case "eyeLookOutLeft": lookOutL = value; continue;
                    case "eyeLookUpLeft": lookUpL = value; continue;
                    case "eyeLookDownLeft": lookDownL = value; continue;
                    case "eyeLookInRight": lookInR = value; continue;
                    case "eyeLookOutRight": lookOutR = value; continue;
                    case "eyeLookUpRight": lookUpR = value; continue;
                    case "eyeLookDownRight": lookDownR = value; continue;
                    case "eyeBlinkLeft": blinkL = value; continue;
                    case "eyeBlinkRight": blinkR = value; continue;
                }
                Add(names[i], value, unified);
            }
            // The left eye looks in towards the nose, which is to the subject's right (+x).
            eye[LeftGazeX] = (lookInL - lookOutL) * FullLook;
            eye[LeftGazeY] = (lookUpL - lookDownL) * FullLook;
            eye[RightGazeX] = (lookOutR - lookInR) * FullLook;
            eye[RightGazeY] = (lookUpR - lookDownR) * FullLook;
            eye[LeftOpenness] = 1f - blinkL;
            eye[RightOpenness] = 1f - blinkR;
        }

        /// <summary>One tracker value into every slot its name drives (kept at the largest value given). False for an unknown name.</summary>
        internal static bool Add(string sourceName, float value, float[] unified)
        {
            if (sourceName == null || !Sources.TryGetValue(sourceName, out string[] names)) return false;
            value = Clamp01(value);
            foreach (string name in names)
            {
                int slot = UnifiedIndex[name];
                if (value > unified[slot]) unified[slot] = value;
            }
            return true;
        }

        /// <summary>Like Add, but replaces what is in the slots: for a tracker that overrides another (Babble over MediaPipe).</summary>
        internal static bool Set(string sourceName, float value, float[] unified)
        {
            if (sourceName == null || !Sources.TryGetValue(sourceName, out string[] names)) return false;
            value = Clamp01(value);
            foreach (string name in names) unified[UnifiedIndex[name]] = value;
            return true;
        }

        internal static float Clamp01(float value)
        {
            if (float.IsNaN(value) || value < 0f) return 0f;
            return value > 1f ? 1f : value;
        }
    }
}

// ============================================================================
// GLOSSARY: FTRecorder expression map
// ============================================================================
//
// FTRecorderExpressionMap (this file)
//   UnifiedNames             Every Unified Expression name in VRCFaceTracking's enum order (a recording's "unified").
//   EyeNames                 LeftGazeX, LeftGazeY, RightGazeX, RightGazeY, LeftOpenness, RightOpenness (slot constants
//                            of the same names). Gaze is a tangent, x right and y up; a full eyeLook is tan 30°.
//   Targets(sourceName)      The slots a tracker name drives, or null.
//   IndexOf(unifiedName)     A Unified Expression's slot, or -1.
//   FromArkit(names, values, unified, eye)  MediaPipe's 52 values into every slot: eyeLook* to gaze, eyeBlink* to
//                            openness, the rest by the source table.
//   Add(sourceName, value, unified)  Raises the driven slots to value. Set(...) replaces them (Babble over MediaPipe).
//   Sources                  The table: ARKit and Babble names (any case) to Unified Expressions. Shared shapes drive
//                            every expression that shares them (mouthFunnel the four LipFunnel quadrants, mouthLeft
//                            the upper and lower MouthLeft, mouthRoll* the LipSuck sides, mouthShrug* MouthRaiser*,
//                            mouthSmile* MouthCornerPull and MouthCornerSlant, browDown* BrowLowerer and BrowPinch).
