using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Riry.RiryTools.FTRecorder
{
    /// <summary>
    /// Meta's OVRLipSync library, the same viseme engine VRChat uses, loaded from a file the user supplies (it is not
    /// bundled). Both builds of the library are accepted: the Unity plugin's (functions named ovrLipSyncDll_*) and the
    /// native SDK's (ovrLipSync_*). Audio goes in as 16-bit mono at 16 kHz, 10 ms at a time.
    /// </summary>
    internal sealed unsafe class FTRecorderOvrLipSync : IDisposable
    {
        private const int Success = 0;
        private const int ProviderOriginal = 0, ProviderEnhanced = 1;
        private const int MonoShort = 0;
        private const int VisemeCount = 15;

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeFrame
        {
            internal int FrameNumber;
            internal int FrameDelay;
            internal float* Visemes;
            internal uint VisemesLength;
            internal float LaughterScore;
            internal float* LaughterCategories;
            internal uint LaughterCategoriesLength;
        }

        private readonly IntPtr library;
        private readonly bool unity;
        private readonly delegate* unmanaged<void> shutdown;
        private readonly delegate* unmanaged<uint, int> destroy;
        private readonly delegate* unmanaged<uint, short*, uint, int, int*, int*, float*, int, float*, float*, int, int> processUnity;
        private readonly delegate* unmanaged<uint, short*, int, int, NativeFrame*, int> processNative;
        private readonly uint context;
        private readonly float[] visemes = new float[VisemeCount];
        private readonly float[] laughter = new float[16];
        private bool disposed;

        private FTRecorderOvrLipSync(string path)
        {
            library = NativeLibrary.Load(path);
            try
            {
                unity = NativeLibrary.TryGetExport(library, "ovrLipSyncDll_Initialize", out IntPtr initialize);
                string prefix = unity ? "ovrLipSyncDll_" : "ovrLipSync_";
                if (!unity) initialize = Export(prefix + "Initialize");
                shutdown = (delegate* unmanaged<void>)Export(prefix + "Shutdown");
                destroy = (delegate* unmanaged<uint, int>)Export(prefix + "DestroyContext");
                var create = (delegate* unmanaged<uint*, int, int, int, int>)Export(prefix + "CreateContextEx");
                if (unity) processUnity = (delegate* unmanaged<uint, short*, uint, int, int*, int*, float*, int, float*, float*, int, int>)Export(prefix + "ProcessFrameEx");
                else processNative = (delegate* unmanaged<uint, short*, int, int, NativeFrame*, int>)Export(prefix + "ProcessFrameEx");

                int result = ((delegate* unmanaged<int, int, int>)initialize)(FTRecorderVisemeClassifier.SampleRate, 1024);
                if (result != Success) throw new InvalidOperationException("OVRLipSync did not initialise (error " + result + ").");
                uint made = 0;
                result = create(&made, ProviderEnhanced, FTRecorderVisemeClassifier.SampleRate, 0);
                if (result != Success) result = create(&made, ProviderOriginal, FTRecorderVisemeClassifier.SampleRate, 0);
                if (result != Success)
                {
                    shutdown();
                    throw new InvalidOperationException("OVRLipSync could not make a context (error " + result + ").");
                }
                context = made;
            }
            catch
            {
                NativeLibrary.Free(library);
                throw;
            }
        }

        private IntPtr Export(string name)
        {
            if (!NativeLibrary.TryGetExport(library, name, out IntPtr address))
                throw new EntryPointNotFoundException("The OVRLipSync library has no " + name + ": it is not a supported OVRLipSync build.");
            return address;
        }

        /// <summary>Loads the library at path. Throws, saying why, when it is missing or is not OVRLipSync.</summary>
        internal static FTRecorderOvrLipSync Load(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("The OVRLipSync library was not found.", path);
            return new FTRecorderOvrLipSync(Path.GetFullPath(path));
        }

        /// <summary>The 15 viseme weights after one block of samples (VRChat's order).</summary>
        internal float[] Process(short[] samples, int offset, int count)
        {
            if (offset < 0 || count < 0 || offset + count > samples.Length) throw new ArgumentOutOfRangeException(nameof(count));
            int result;
            fixed (short* audio = &samples[offset])
            fixed (float* weights = visemes)
            fixed (float* laughing = laughter)
            {
                if (unity)
                {
                    int number = 0, delay = 0;
                    float laughterScore = 0;
                    result = processUnity(context, audio, (uint)count, MonoShort, &number, &delay, weights, VisemeCount, &laughterScore, laughing, laughter.Length);
                }
                else
                {
                    var frame = new NativeFrame
                    {
                        Visemes = weights,
                        VisemesLength = VisemeCount,
                        LaughterCategories = laughing,
                        LaughterCategoriesLength = (uint)laughter.Length,
                    };
                    result = processNative(context, audio, count, MonoShort, &frame);
                }
            }
            if (result != Success) throw new InvalidOperationException("OVRLipSync could not process audio (error " + result + ").");
            return (float[])visemes.Clone();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            destroy(context);
            shutdown();
            NativeLibrary.Free(library);
        }
    }
}

// ============================================================================
// GLOSSARY: FTRecorder OVRLipSync
// ============================================================================
//
// FTRecorderOvrLipSync (this file)  Meta's OVRLipSync from a user-supplied file, never bundled.
//   Load(path)               Accepts the Unity plugin build (ovrLipSyncDll_*) or the native SDK build (ovrLipSync_*).
//                            Initialise at 16 kHz, buffer 1024; the Enhanced provider, else the Original one.
//   Process(samples, offset, count)  16-bit mono samples in; the 15 viseme weights out, VRChat's order.
//   Dispose()                Destroys the context, shuts the library down and unloads it.
