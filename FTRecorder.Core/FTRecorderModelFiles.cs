using System;
using System.IO;
using System.Text.Json;

namespace Riry.RiryTools.FTRecorder
{
    /// <summary>
    /// The bundled model files: the folder "Models" next to this assembly, and what mediapipe.json says about the
    /// MediaPipe models (which landmarks the blendshape model takes, and the names of its outputs).
    /// </summary>
    internal sealed class FTRecorderModelFiles
    {
        internal const string FolderName = "Models";
        internal const string DetectorFile = "face_detector.onnx";
        internal const string LandmarksFile = "face_landmarks_detector.onnx";
        internal const string BlendshapesFile = "face_blendshapes.onnx";
        internal const string MediaPipeFile = "mediapipe.json";
        internal const string BabbleLabelsFile = "babble-labels.json";

        internal string Folder { get; private set; }
        internal int[] BlendshapeInputLandmarks { get; private set; }
        internal string[] BlendshapeNames { get; private set; }

        internal string Detector => Path.Combine(Folder, DetectorFile);
        internal string Landmarks => Path.Combine(Folder, LandmarksFile);
        internal string Blendshapes => Path.Combine(Folder, BlendshapesFile);
        internal string BabbleLabels => Path.Combine(Folder, BabbleLabelsFile);

        /// <summary>Reads the Models folder beside this assembly. Throws, naming the file, when one is missing or wrong.</summary>
        internal static FTRecorderModelFiles Load()
        {
            string folder = Path.Combine(AppContext.BaseDirectory, FolderName);
            string beside = Path.GetDirectoryName(typeof(FTRecorderModelFiles).Assembly.Location);
            // A VRCFaceTracking module runs in VRCFaceTracking's process: its folder is not the base directory.
            if (!string.IsNullOrEmpty(beside) && Directory.Exists(Path.Combine(beside, FolderName))) folder = Path.Combine(beside, FolderName);
            var files = new FTRecorderModelFiles { Folder = folder };
            foreach (string name in new[] { DetectorFile, LandmarksFile, BlendshapesFile, MediaPipeFile })
            {
                if (!File.Exists(Path.Combine(folder, name))) throw new FileNotFoundException("A bundled model file is missing.", Path.Combine(folder, name));
            }
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, MediaPipeFile)));
            JsonElement root = document.RootElement;
            JsonElement indices = root.GetProperty("blendshapeInputLandmarks");
            files.BlendshapeInputLandmarks = new int[indices.GetArrayLength()];
            int i = 0;
            foreach (JsonElement index in indices.EnumerateArray()) files.BlendshapeInputLandmarks[i++] = index.GetInt32();
            files.BlendshapeNames = ReadStrings(root.GetProperty("blendshapeNames"));
            return files;
        }

        /// <summary>The "labels" list of a Babble labels file: one name per model output, in output order.</summary>
        internal static string[] ReadLabels(string path)
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            return ReadStrings(document.RootElement.GetProperty("labels"));
        }

        private static string[] ReadStrings(JsonElement array)
        {
            var result = new string[array.GetArrayLength()];
            int i = 0;
            foreach (JsonElement item in array.EnumerateArray()) result[i++] = item.GetString() ?? "";
            return result;
        }
    }
}

// ============================================================================
// GLOSSARY: FTRecorder model files
// ============================================================================
//
// FTRecorderModelFiles (this file)
//   Load()                   The "Models" folder beside this assembly (or the base directory): checks the three ONNX
//                            files and mediapipe.json are there and reads BlendshapeInputLandmarks (146 landmark
//                            indices) and BlendshapeNames (52, "_neutral" first).
//   ReadLabels(path)         A Babble labels file's "labels": one name per model output.
//   Detector, Landmarks, Blendshapes, BabbleLabels  Full paths of the bundled files.
