using System;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Riry.RiryTools.FTRecorder
{
    /// <summary>What the landmarker found in one frame.</summary>
    internal sealed class FTRecorderFaceResult
    {
        /// <summary>478 landmarks, x, y, z each; x and y in frame pixels.</summary>
        internal readonly float[] Landmarks = new float[FTRecorderFaceLandmarker.LandmarkCount * 3];
        /// <summary>The blendshape model's values, in FTRecorderModelFiles.BlendshapeNames order.</summary>
        internal float[] Blendshapes = Array.Empty<float>();
        /// <summary>The face's roll, radians: the angle of the line from its right eye to its left eye.</summary>
        internal float Roll;
    }

    /// <summary>
    /// MediaPipe's face landmarker run with ONNX Runtime: the detector finds the face, the landmark model places 478
    /// points in a 256 × 256 crop turned upright, and the blendshape model turns 146 of them into 52 ARKit-style values.
    /// While a face stays in view the next crop is taken from this frame's landmarks, as MediaPipe tracks, and the
    /// detector runs again only when the face is lost.
    /// </summary>
    internal sealed class FTRecorderFaceLandmarker : IDisposable
    {
        internal const int LandmarkCount = 478;
        private const int Size = 256;
        private const float Enlarge = 1.5f;
        private const float PresenceThreshold = 0.5f;
        // The outer corners of the eyes, for the roll of a tracked face.
        private const int RightEyeOuter = 33, LeftEyeOuter = 263;

        private readonly FTRecorderModelFiles files;
        private readonly FTRecorderFaceDetector detector;
        private readonly InferenceSession landmarks;
        private readonly InferenceSession blendshapes;
        private readonly string landmarksInput;
        private readonly string blendshapesInput;
        private readonly float[] input = new float[Size * Size * 3];
        private readonly float[] points;
        private FTRecorderRect? tracked;

        internal FTRecorderFaceLandmarker(FTRecorderModelFiles files, int threads)
        {
            this.files = files;
            var options = new SessionOptions { IntraOpNumThreads = Math.Max(1, threads), InterOpNumThreads = 1 };
            detector = new FTRecorderFaceDetector(files.Detector, options);
            landmarks = new InferenceSession(files.Landmarks, options);
            blendshapes = new InferenceSession(files.Blendshapes, options);
            landmarksInput = landmarks.InputMetadata.Keys.First();
            blendshapesInput = blendshapes.InputMetadata.Keys.First();
            points = new float[files.BlendshapeInputLandmarks.Length * 2];
        }

        /// <summary>Forgets the tracked face, so the next frame starts with the detector.</summary>
        internal void Reset() => tracked = null;

        /// <summary>Landmarks and blendshapes for the face in frame. False, and tracking reset, when there is none.</summary>
        internal bool TryProcess(FTRecorderFrame frame, FTRecorderFaceResult result)
        {
            FTRecorderRect rect;
            if (tracked.HasValue) rect = tracked.Value;
            else
            {
                if (!detector.TryDetect(frame, out FTRecorderDetection face)) return false;
                rect = FromDetection(face);
            }
            if (!RunLandmarks(frame, rect, result))
            {
                tracked = null;
                return false;
            }
            tracked = FromLandmarks(result.Landmarks);
            result.Roll = Angle(result.Landmarks, RightEyeOuter, LeftEyeOuter);
            RunBlendshapes(result);
            return true;
        }

        // The crop around a detected face: square on the larger side of its box, enlarged 1.5 times, turned so the eyes
        // are level.
        private static FTRecorderRect FromDetection(FTRecorderDetection face)
        {
            float rotation = MathF.Atan2(face.Keypoints[3] - face.Keypoints[1], face.Keypoints[2] - face.Keypoints[0]);
            float side = Math.Max(face.Width, face.Height) * Enlarge;
            return new FTRecorderRect { CenterX = face.CenterX, CenterY = face.CenterY, Width = side, Height = side, Rotation = rotation };
        }

        // The crop for the next frame from this frame's landmarks: their bounds measured along the face's own axes.
        private static FTRecorderRect FromLandmarks(float[] landmarks)
        {
            float rotation = Angle(landmarks, RightEyeOuter, LeftEyeOuter);
            float cos = MathF.Cos(rotation), sin = MathF.Sin(rotation);
            float minU = float.MaxValue, maxU = float.MinValue, minV = float.MaxValue, maxV = float.MinValue;
            for (int i = 0; i < LandmarkCount; i++)
            {
                float x = landmarks[i * 3], y = landmarks[i * 3 + 1];
                float u = x * cos + y * sin, v = -x * sin + y * cos;
                minU = Math.Min(minU, u); maxU = Math.Max(maxU, u);
                minV = Math.Min(minV, v); maxV = Math.Max(maxV, v);
            }
            float cu = (minU + maxU) / 2, cv = (minV + maxV) / 2;
            float side = Math.Max(maxU - minU, maxV - minV) * Enlarge;
            return new FTRecorderRect { CenterX = cu * cos - cv * sin, CenterY = cu * sin + cv * cos, Width = side, Height = side, Rotation = rotation };
        }

        private static float Angle(float[] landmarks, int from, int to)
        {
            return MathF.Atan2(landmarks[to * 3 + 1] - landmarks[from * 3 + 1], landmarks[to * 3] - landmarks[from * 3]);
        }

        private bool RunLandmarks(FTRecorderFrame frame, FTRecorderRect rect, FTRecorderFaceResult result)
        {
            FTRecorderImage.CropRgb(frame, rect, Size, Size, input, 1f / 255f, 0f);
            var tensor = new DenseTensor<float>(input, new[] { 1, Size, Size, 3 });
            float[] coordinates = null;
            float presence = float.NaN;
            using (var outputs = landmarks.Run(new[] { NamedOnnxValue.CreateFromTensor(landmarksInput, tensor) }))
            {
                foreach (DisposableNamedOnnxValue output in outputs)
                {
                    float[] values = output.AsEnumerable<float>().ToArray();
                    if (values.Length == LandmarkCount * 3) coordinates = values;
                    // Of the two single values, the face presence is the first output after the landmarks.
                    else if (values.Length == 1 && float.IsNaN(presence)) presence = values[0];
                }
            }
            if (coordinates == null) throw new InvalidOperationException("The landmark model's output is not 478 × 3 values.");
            if (!float.IsNaN(presence) && 1f / (1f + MathF.Exp(-presence)) < PresenceThreshold) return false;
            for (int i = 0; i < LandmarkCount; i++)
            {
                rect.ToFrame(coordinates[i * 3] / Size, coordinates[i * 3 + 1] / Size, out float x, out float y);
                result.Landmarks[i * 3] = x;
                result.Landmarks[i * 3 + 1] = y;
                result.Landmarks[i * 3 + 2] = coordinates[i * 3 + 2] / Size * rect.Width;
            }
            return true;
        }

        // The blendshape model takes the chosen landmarks' x and y in frame pixels.
        private void RunBlendshapes(FTRecorderFaceResult result)
        {
            int[] chosen = files.BlendshapeInputLandmarks;
            for (int i = 0; i < chosen.Length; i++)
            {
                points[i * 2] = result.Landmarks[chosen[i] * 3];
                points[i * 2 + 1] = result.Landmarks[chosen[i] * 3 + 1];
            }
            var tensor = new DenseTensor<float>(points, new[] { 1, chosen.Length, 2 });
            using var outputs = blendshapes.Run(new[] { NamedOnnxValue.CreateFromTensor(blendshapesInput, tensor) });
            result.Blendshapes = outputs.First().AsEnumerable<float>().ToArray();
        }

        public void Dispose()
        {
            detector.Dispose();
            landmarks.Dispose();
            blendshapes.Dispose();
        }
    }
}

// ============================================================================
// GLOSSARY: FTRecorder face landmarker
// ============================================================================
//
// FTRecorderFaceResult (this file)  Landmarks (478 × x, y, z; x and y in frame pixels), Blendshapes (52, MediaPipe
//                            names), Roll (radians, right eye to left eye).
// FTRecorderFaceLandmarker   Detector, then the 256 × 256 landmark model (RGB 0..1) on a crop turned upright, then the
//                            blendshape model on 146 landmarks' pixel x and y.
//   TryProcess(frame, result)  False, and tracking reset, when no face is found or the face presence is under 0.5.
//   Reset()                  The next frame starts with the detector.
//   Crops                    From a detection: the box's larger side × 1.5, rotated by the eye key points. While
//                            tracking: the landmarks' bounds along the face's axes (eye corners 33 and 263) × 1.5.
