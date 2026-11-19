using System;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Riry.RiryTools.FTRecorder
{
    /// <summary>
    /// A Babble face model the user already has (never bundled: Babble's licence does not allow it here), run on the
    /// mouth of an ordinary camera image. Babble's own camera looks up at the mouth from below; here the crop is taken
    /// square around the mouth, turned upright with the face, in grey, scaled to the model's input and divided by 255,
    /// as Babble feeds its model. The model's outputs are named by a labels file, in output order.
    /// </summary>
    internal sealed class FTRecorderBabbleModel : IDisposable
    {
        // Landmarks that frame the lower face: upper lip centre, lower lip centre, chin, and the cheeks beside the mouth.
        private const int UpperLip = 13, LowerLip = 14, Chin = 152, RightCheek = 205, LeftCheek = 425;

        private readonly InferenceSession session;
        private readonly string inputName;
        private readonly int channels, height, width;
        private readonly float[] input;

        /// <summary>One name per output, from the labels file.</summary>
        internal readonly string[] Labels;

        /// <summary>How much of the face the crop takes: 1 is the distance between the cheeks beside the mouth, times 2.</summary>
        internal float CropScale = 1f;

        internal FTRecorderBabbleModel(string modelPath, string labelsPath, int threads)
        {
            if (!File.Exists(modelPath)) throw new FileNotFoundException("The Babble model was not found.", modelPath);
            Labels = FTRecorderModelFiles.ReadLabels(labelsPath);
            session = new InferenceSession(modelPath, new SessionOptions { IntraOpNumThreads = Math.Max(1, threads), InterOpNumThreads = 1 });
            NodeMetadata metadata = session.InputMetadata.Values.First();
            inputName = session.InputMetadata.Keys.First();
            int[] dimensions = metadata.Dimensions;
            if (dimensions.Length != 4 || dimensions[1] <= 0 || dimensions[2] <= 0 || dimensions[3] <= 0)
            {
                session.Dispose();
                throw new InvalidDataException("The Babble model's input is not [1, channels, height, width].");
            }
            channels = dimensions[1];
            height = dimensions[2];
            width = dimensions[3];
            input = new float[channels * height * width];
        }

        /// <summary>The model's values for the mouth in frame, one per label (extra outputs are dropped, missing ones are 0).</summary>
        internal float[] Run(FTRecorderFrame frame, FTRecorderFaceResult face)
        {
            float[] l = face.Landmarks;
            float centerX = (l[UpperLip * 3] + l[LowerLip * 3] + l[Chin * 3]) / 3f;
            float centerY = (l[UpperLip * 3 + 1] + l[LowerLip * 3 + 1] + l[Chin * 3 + 1]) / 3f;
            float cheeks = MathF.Sqrt(Square(l[LeftCheek * 3] - l[RightCheek * 3]) + Square(l[LeftCheek * 3 + 1] - l[RightCheek * 3 + 1]));
            float side = Math.Max(1f, cheeks * 2f * CropScale);
            var rect = new FTRecorderRect { CenterX = centerX, CenterY = centerY, Width = side, Height = side, Rotation = face.Roll };
            FTRecorderImage.CropGray(frame, rect, width, height, input, channels);
            var tensor = new DenseTensor<float>(input, new[] { 1, channels, height, width });
            using var outputs = session.Run(new[] { NamedOnnxValue.CreateFromTensor(inputName, tensor) });
            float[] raw = outputs.First().AsEnumerable<float>().ToArray();
            var values = new float[Labels.Length];
            Array.Copy(raw, values, Math.Min(raw.Length, values.Length));
            return values;
        }

        private static float Square(float value) => value * value;

        public void Dispose() => session.Dispose();
    }
}

// ============================================================================
// GLOSSARY: FTRecorder Babble model
// ============================================================================
//
// FTRecorderBabbleModel (this file)  A user-supplied Babble ONNX face model, never bundled.
//   Labels                   Output names from the labels file (Models/babble-labels.json by default).
//   CropScale                Crop side = distance between landmarks 205 and 425 × 2 × CropScale, centred on the mean
//                            of landmarks 13, 14 and 152, turned by the face's roll.
//   Run(frame, face)         Grey / 255, NCHW at the model's input size; returns one value per label.
