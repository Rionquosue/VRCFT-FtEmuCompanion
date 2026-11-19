using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace Riry.RiryTools.FTRecorder
{
    /// <summary>
    /// VRCFaceTracking loads a module's DLL into its own load context but does not look beside it for the DLLs it
    /// needs (ONNX Runtime and its native library). This points that context at the module's folder, and only there.
    /// It must run before any code that touches ONNX Runtime, so the module type itself never refers to it.
    /// </summary>
    internal static class FTRecorderModuleDependencies
    {
        private static readonly object Gate = new object();
        private static bool installed;

        internal static void Install()
        {
            lock (Gate)
            {
                if (installed) return;
                installed = true;
            }
            Assembly self = typeof(FTRecorderModuleDependencies).Assembly;
            string folder = Path.GetDirectoryName(self.Location);
            if (string.IsNullOrEmpty(folder)) return;
            AssemblyLoadContext context = AssemblyLoadContext.GetLoadContext(self) ?? AssemblyLoadContext.Default;
            context.Resolving += (loader, name) =>
            {
                if (string.IsNullOrEmpty(name.Name) || name.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
                string path = Path.Combine(folder, name.Name + ".dll");
                return File.Exists(path) ? loader.LoadFromAssemblyPath(path) : null;
            };
            context.ResolvingUnmanagedDll += (assembly, name) =>
            {
                if (string.IsNullOrEmpty(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return IntPtr.Zero;
                foreach (string file in new[] { name, name + ".dll", "lib" + name + ".so", name + ".so", "lib" + name + ".dylib" })
                {
                    foreach (string place in new[] { folder, Path.Combine(folder, "runtimes", RuntimeInformation.RuntimeIdentifier, "native") })
                    {
                        string path = Path.Combine(place, file);
                        if (File.Exists(path) && NativeLibrary.TryLoad(path, out IntPtr handle)) return handle;
                    }
                }
                return IntPtr.Zero;
            };
        }
    }
}

// ============================================================================
// GLOSSARY: FTRecorder module dependencies
// ============================================================================
//
// FTRecorderModuleDependencies (this file)
//   Install()                Once: the module's load context resolves managed DLLs (name.dll) and native libraries
//                            (name, name.dll, libname.so, name.so, libname.dylib) from the module's folder or its
//                            runtimes/<rid>/native folder. Names with path characters are refused.
