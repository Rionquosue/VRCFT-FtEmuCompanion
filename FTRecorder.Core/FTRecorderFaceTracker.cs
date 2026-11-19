using System;

namespace Riry.RiryTools.FTRecorder
{
    /// <summary>
    /// Frames in, Unified Expressions and eyes out: MediaPipe for the whole face, and, when a Babble model is given, the
    /// Babble model for every shape it names, replacing MediaPipe's value for those shapes.
    /// </summary>
    internal sealed class FTRecorderFaceTracker : IDisposable
    {
        private readonly FTRecorderModelFiles files;
        private readonly FTRecorderFaceLandmarker landmarker;
        private readonly FTRecorderBabbleModel babble;
        private readonly FTRecorderFaceResult face = new FTRecorderFaceResult();

        /// <param name="babbleModel">A Babble ONNX model, or null or empty for MediaPipe only.</param>
        /// <param name="babbleLabels">Its labels file, or null or empty for the bundled one.</param>
        internal FTRecorderFaceTracker(string babbleModel, string babbleLabels, float babbleCropScale, int threads)
        {
            files = FTRecorderModelFiles.Load();
            landmarker = new FTRecorderFaceLandmarker(files, threads);
            if (!string.IsNullOrWhiteSpace(babbleModel))
            {
                try
                {
                    babble = new FTRecorderBabbleModel(babbleModel, string.IsNullOrWhiteSpace(babbleLabels) ? files.BabbleLabels : babbleLabels, threads)
                    {
                        CropScale = babbleCropScale > 0 ? babbleCropScale : 1f,
                    };
                }
                catch
                {
                    landmarker.Dispose();
                    throw;
                }
            }
        }

        internal bool UsesBabble => babble != null;

        /// <summary>
        /// Fills unified and eye for the face in frame. False when there is no face: unified and eye are then left as
        /// they were, so the caller decides whether to hold the last values.
        /// </summary>
        internal bool TryTrack(FTRecorderFrame frame, float[] unified, float[] eye)
        {
            if (!landmarker.TryProcess(frame, face)) return false;
            FTRecorderExpressionMap.FromArkit(files.BlendshapeNames, face.Blendshapes, unified, eye);
            if (babble != null)
            {
                float[] values = babble.Run(frame, face);
                for (int i = 0; i < values.Length; i++) FTRecorderExpressionMap.Set(babble.Labels[i], values[i], unified);
            }
            return true;
        }

        internal void Reset() => landmarker.Reset();

        public void Dispose()
        {
            landmarker.Dispose();
            babble?.Dispose();
        }
    }
}

// ============================================================================
// GLOSSARY: FTRecorder face tracker
// ============================================================================
//
// FTRecorderFaceTracker (this file)
//   (babbleModel, babbleLabels, babbleCropScale, threads)  MediaPipe always; a Babble model when babbleModel is set.
//   TryTrack(frame, unified, eye)  MediaPipe's values through FTRecorderExpressionMap.FromArkit, then each Babble
//                            output Set over the shapes its label drives. False with no face; arrays left as they were.
//   UsesBabble, Reset()
