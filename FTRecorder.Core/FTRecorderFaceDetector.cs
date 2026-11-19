using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Riry.RiryTools.FTRecorder
{
    /// <summary>One face found by the detector, in frame pixels.</summary>
    internal struct FTRecorderDetection
    {
        internal float Score;
        internal float CenterX, CenterY, Width, Height;
        /// <summary>Six key points, x then y: right eye, left eye, nose tip, mouth, right ear, left ear (the subject's sides).</summary>
        internal float[] Keypoints;
    }

    /// <summary>
    /// MediaPipe's short-range face detector (128 × 128 input, 896 anchors). Finds the strongest face in a frame; the
    /// frame is fitted into the square input with black borders, as MediaPipe does, and the result mapped back.
    /// </summary>
    internal sealed class FTRecorderFaceDetector : IDisposable
    {
        private const int Size = 128;
        private const int Anchors = 896;
        private const float ScoreThreshold = 0.5f;
        private const float OverlapThreshold = 0.3f;

        private readonly InferenceSession session;
        private readonly string inputName;
        private readonly float[] input = new float[Size * Size * 3];
        private readonly float[] anchorX = new float[Anchors];
        private readonly float[] anchorY = new float[Anchors];

        internal FTRecorderFaceDetector(string modelPath, SessionOptions options)
        {
            session = new InferenceSession(modelPath, options);
            inputName = session.InputMetadata.Keys.First();
            BuildAnchors();
        }

        // Anchors of the short-range model: a 16 × 16 grid (stride 8) with 2 anchors per cell, then an 8 × 8 grid (three
        // layers of stride 16 merged) with 6 per cell. Every anchor sits at its cell's centre and has size 1.
        private void BuildAnchors()
        {
            int n = 0;
            foreach ((int grid, int perCell) in new[] { (16, 2), (8, 6) })
            {
                for (int y = 0; y < grid; y++)
                {
                    for (int x = 0; x < grid; x++)
                    {
                        for (int k = 0; k < perCell; k++)
                        {
                            anchorX[n] = (x + 0.5f) / grid;
                            anchorY[n] = (y + 0.5f) / grid;
                            n++;
                        }
                    }
                }
            }
        }

        /// <summary>The strongest face, merged with the boxes that overlap it. False when none scores 0.5 or more.</summary>
        internal bool TryDetect(FTRecorderFrame frame, out FTRecorderDetection detection)
        {
            detection = default;
            float side = Math.Max(frame.Width, frame.Height);
            var square = new FTRecorderRect { CenterX = frame.Width / 2f, CenterY = frame.Height / 2f, Width = side, Height = side };
            FTRecorderImage.CropRgb(frame, square, Size, Size, input, 1f / 127.5f, -1f);
            var tensor = new DenseTensor<float>(input, new[] { 1, Size, Size, 3 });
            float[] boxes, scores;
            using (var results = session.Run(new[] { NamedOnnxValue.CreateFromTensor(inputName, tensor) }))
            {
                boxes = null;
                scores = null;
                foreach (DisposableNamedOnnxValue result in results)
                {
                    float[] values = result.AsEnumerable<float>().ToArray();
                    if (values.Length == Anchors * 16) boxes = values;
                    else if (values.Length == Anchors) scores = values;
                }
            }
            if (boxes == null || scores == null) throw new InvalidOperationException("The face detector's outputs are not the expected 896 × 16 boxes and 896 scores.");

            var candidates = new List<(float score, int index)>();
            for (int i = 0; i < Anchors; i++)
            {
                float score = 1f / (1f + MathF.Exp(-Math.Clamp(scores[i], -100f, 100f)));
                if (score >= ScoreThreshold) candidates.Add((score, i));
            }
            if (candidates.Count == 0) return false;
            candidates.Sort((a, b) => b.score.CompareTo(a.score));

            // Weighted suppression of the best face only: every box overlapping it adds to its position by score.
            int best = candidates[0].index;
            Box(boxes, best, out float bx0, out float by0, out float bx1, out float by1);
            var sum = new float[16];
            float total = 0;
            foreach ((float score, int index) in candidates)
            {
                Box(boxes, index, out float x0, out float y0, out float x1, out float y1);
                if (Overlap(bx0, by0, bx1, by1, x0, y0, x1, y1) < OverlapThreshold) continue;
                total += score;
                for (int k = 0; k < 16; k++) sum[k] += Decode(boxes, index, k) * score;
            }
            for (int k = 0; k < 16; k++) sum[k] /= total;

            // From the square input's 0..1 back to frame pixels.
            float left = square.CenterX - side / 2f, top = square.CenterY - side / 2f;
            detection.Score = candidates[0].score;
            detection.CenterX = left + sum[0] * side;
            detection.CenterY = top + sum[1] * side;
            detection.Width = sum[2] * side;
            detection.Height = sum[3] * side;
            detection.Keypoints = new float[12];
            for (int k = 0; k < 6; k++)
            {
                detection.Keypoints[k * 2] = left + sum[4 + k * 2] * side;
                detection.Keypoints[k * 2 + 1] = top + sum[5 + k * 2] * side;
            }
            return true;
        }

        // Value k of box i in the input's 0..1: positions are offsets from the anchor, sizes are absolute.
        private float Decode(float[] boxes, int i, int k)
        {
            float raw = boxes[i * 16 + k] / Size;
            if (k == 2 || k == 3) return raw;
            return raw + ((k & 1) == 0 ? anchorX[i] : anchorY[i]);
        }

        private void Box(float[] boxes, int i, out float x0, out float y0, out float x1, out float y1)
        {
            float cx = Decode(boxes, i, 0), cy = Decode(boxes, i, 1), w = Decode(boxes, i, 2), h = Decode(boxes, i, 3);
            x0 = cx - w / 2;
            y0 = cy - h / 2;
            x1 = cx + w / 2;
            y1 = cy + h / 2;
        }

        private static float Overlap(float ax0, float ay0, float ax1, float ay1, float bx0, float by0, float bx1, float by1)
        {
            float w = Math.Min(ax1, bx1) - Math.Max(ax0, bx0), h = Math.Min(ay1, by1) - Math.Max(ay0, by0);
            if (w <= 0 || h <= 0) return 0;
            float shared = w * h;
            return shared / ((ax1 - ax0) * (ay1 - ay0) + (bx1 - bx0) * (by1 - by0) - shared);
        }

        public void Dispose() => session.Dispose();
    }
}

// ============================================================================
// GLOSSARY: FTRecorder face detector
// ============================================================================
//
// FTRecorderDetection (this file)  Score; CenterX, CenterY, Width, Height and Keypoints (6 × x, y: right eye, left
//                            eye, nose tip, mouth, right ear, left ear), all in frame pixels.
// FTRecorderFaceDetector     MediaPipe short-range face detector, 128 × 128 input in -1..1, 896 anchors (16 × 16 × 2
//                            and 8 × 8 × 6, size 1, at cell centres).
//   TryDetect(frame, out detection)  The best face scoring 0.5 or more (sigmoid of the raw score, clamped to ±100),
//                            merged with every candidate overlapping it by 0.3 or more, weighted by score.
