using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Riry.RiryTools.FTRecorder
{
    internal enum FTRecorderEntryKind
    {
        /// <summary>Unified Expressions and eyes, in the header's "unified" and "eye" order.</summary>
        Expressions,
        /// <summary>One OSC packet as it was sent, unchanged.</summary>
        Osc,
        /// <summary>One avatar parameter VRChat sent (Viseme, Voice).</summary>
        Parameter,
        /// <summary>A viseme made from audio: the strongest of the 15, Voice, and all 15 weights.</summary>
        Viseme,
    }

    internal sealed class FTRecorderEntry
    {
        /// <summary>Seconds from the start of the recording.</summary>
        internal double Time;
        internal FTRecorderEntryKind Kind;
        internal float[] Unified;
        internal float[] Eye;
        internal byte[] Packet;
        internal string Name;
        /// <summary>Parameter: 'i', 'f' or 'b'.</summary>
        internal char Type;
        internal float Value;
        internal int Viseme;
        internal float Voice;
        internal float[] Weights;
    }

    internal sealed class FTRecorderRecording
    {
        internal const string Format = "RiryTools.FTRecorder";
        internal const int Version = 1;

        internal string Source;
        internal string Created;
        internal string[] Unified = Array.Empty<string>();
        internal string[] Eye = Array.Empty<string>();
        internal readonly List<FTRecorderEntry> Entries = new List<FTRecorderEntry>();

        /// <summary>The time of the last entry, in seconds.</summary>
        internal double Duration => Entries.Count == 0 ? 0 : Entries[Entries.Count - 1].Time;

        /// <summary>
        /// Reads a recording: one JSON object per line, the first the header. A line that is not valid is refused with its
        /// number; entries are kept in time order as written.
        /// </summary>
        internal static FTRecorderRecording Load(string path)
        {
            var recording = new FTRecorderRecording();
            int number = 0;
            foreach (string line in File.ReadLines(path))
            {
                number++;
                if (line.Length == 0) continue;
                try
                {
                    using JsonDocument document = JsonDocument.Parse(line);
                    JsonElement root = document.RootElement;
                    if (number == 1)
                    {
                        if (!root.TryGetProperty("format", out JsonElement format) || format.GetString() != Format)
                            throw new InvalidDataException("not an FTRecorder recording");
                        if (root.TryGetProperty("version", out JsonElement version) && version.GetInt32() > Version)
                            throw new InvalidDataException("made by a newer FTRecorder (version " + version.GetInt32() + ")");
                        recording.Source = Text(root, "source");
                        recording.Created = Text(root, "created");
                        recording.Unified = Strings(root, "unified");
                        recording.Eye = Strings(root, "eye");
                        continue;
                    }
                    recording.Entries.Add(Entry(root));
                }
                catch (Exception exception) when (exception is JsonException || exception is InvalidOperationException || exception is FormatException || exception is InvalidDataException)
                {
                    throw new InvalidDataException(path + " line " + number + ": " + exception.Message, exception);
                }
            }
            if (number == 0) throw new InvalidDataException(path + " is empty.");
            return recording;
        }

        private static FTRecorderEntry Entry(JsonElement root)
        {
            var entry = new FTRecorderEntry { Time = root.GetProperty("t").GetDouble() };
            if (root.TryGetProperty("unified", out JsonElement unified))
            {
                entry.Kind = FTRecorderEntryKind.Expressions;
                entry.Unified = Floats(unified);
                entry.Eye = root.TryGetProperty("eye", out JsonElement eye) ? Floats(eye) : Array.Empty<float>();
            }
            else if (root.TryGetProperty("osc", out JsonElement osc))
            {
                entry.Kind = FTRecorderEntryKind.Osc;
                entry.Packet = Convert.FromBase64String(osc.GetString() ?? "");
            }
            else if (root.TryGetProperty("param", out JsonElement param))
            {
                entry.Kind = FTRecorderEntryKind.Parameter;
                entry.Name = param.GetString();
                if (root.TryGetProperty("i", out JsonElement i)) { entry.Type = 'i'; entry.Value = i.GetInt32(); }
                else if (root.TryGetProperty("f", out JsonElement f)) { entry.Type = 'f'; entry.Value = f.GetSingle(); }
                else if (root.TryGetProperty("b", out JsonElement b)) { entry.Type = 'b'; entry.Value = b.GetBoolean() ? 1f : 0f; }
                else throw new InvalidDataException("parameter without a value");
            }
            else if (root.TryGetProperty("viseme", out JsonElement viseme))
            {
                entry.Kind = FTRecorderEntryKind.Viseme;
                entry.Viseme = viseme.GetInt32();
                entry.Voice = root.TryGetProperty("voice", out JsonElement voice) ? voice.GetSingle() : 0f;
                entry.Weights = root.TryGetProperty("weights", out JsonElement weights) ? Floats(weights) : Array.Empty<float>();
            }
            else throw new InvalidDataException("unknown entry");
            return entry;
        }

        private static string Text(JsonElement root, string name) => root.TryGetProperty(name, out JsonElement value) ? value.GetString() : null;

        private static string[] Strings(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out JsonElement array)) return Array.Empty<string>();
            var result = new string[array.GetArrayLength()];
            int i = 0;
            foreach (JsonElement item in array.EnumerateArray()) result[i++] = item.GetString();
            return result;
        }

        private static float[] Floats(JsonElement array)
        {
            var result = new float[array.GetArrayLength()];
            int i = 0;
            foreach (JsonElement item in array.EnumerateArray()) result[i++] = item.GetSingle();
            return result;
        }
    }

    /// <summary>
    /// Writes a recording as it happens: a header line, then one line per entry. Safe to call from several threads; lines
    /// are written whole and in the order the calls take the lock. The file is flushed at least once a second, so a crash
    /// loses at most that much.
    /// </summary>
    internal sealed class FTRecorderRecordingWriter : IDisposable
    {
        private readonly object gate = new object();
        private readonly StreamWriter writer;
        private readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        private readonly MemoryStream line = new MemoryStream(4096);
        // A recording is a data file, never HTML: names and paths are written as they are.
        private static readonly JsonWriterOptions WriterOptions = new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        private long lastFlush;
        private bool disposed;

        internal string Path { get; }

        /// <summary>Seconds since the recording started: the time stamp entries get when none is given.</summary>
        internal double Now => clock.Elapsed.TotalSeconds;

        /// <summary>Creates path (it must not exist) and writes the header.</summary>
        internal FTRecorderRecordingWriter(string path, string source)
        {
            Path = path;
            string folder = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
            writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
            Line(json =>
            {
                json.WriteString("format", FTRecorderRecording.Format);
                json.WriteNumber("version", FTRecorderRecording.Version);
                json.WriteString("source", source ?? "");
                json.WriteString("created", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                Array(json, "unified", FTRecorderExpressionMap.UnifiedNames);
                Array(json, "eye", FTRecorderExpressionMap.EyeNames);
            });
        }

        /// <summary>A new file name in folder: prefix, the local date and time, ".ftrec.jsonl".</summary>
        internal static string NewPath(string folder, string prefix)
        {
            string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
            string path = System.IO.Path.Combine(folder, prefix + "_" + stamp + ".ftrec.jsonl");
            for (int n = 2; File.Exists(path); n++) path = System.IO.Path.Combine(folder, prefix + "_" + stamp + "_" + n + ".ftrec.jsonl");
            return path;
        }

        internal void WriteExpressions(double time, float[] unified, float[] eye)
        {
            Line(json =>
            {
                json.WriteNumber("t", Math.Round(time, 4));
                Array(json, "unified", unified);
                Array(json, "eye", eye);
            });
        }

        internal void WriteOsc(double time, byte[] packet, int length)
        {
            Line(json =>
            {
                json.WriteNumber("t", Math.Round(time, 4));
                json.WriteString("osc", Convert.ToBase64String(packet, 0, length));
            });
        }

        internal void WriteParameter(double time, string name, char type, float value)
        {
            Line(json =>
            {
                json.WriteNumber("t", Math.Round(time, 4));
                json.WriteString("param", name);
                if (type == 'i') json.WriteNumber("i", (int)value);
                else if (type == 'b') json.WriteBoolean("b", value != 0f);
                else json.WriteNumber("f", Math.Round(value, 4));
            });
        }

        internal void WriteViseme(double time, int viseme, float voice, float[] weights)
        {
            Line(json =>
            {
                json.WriteNumber("t", Math.Round(time, 4));
                json.WriteNumber("viseme", viseme);
                json.WriteNumber("voice", Math.Round(voice, 4));
                Array(json, "weights", weights);
            });
        }

        private void Line(Action<Utf8JsonWriter> write)
        {
            lock (gate)
            {
                if (disposed) return;
                line.SetLength(0);
                using (var json = new Utf8JsonWriter(line, WriterOptions))
                {
                    json.WriteStartObject();
                    write(json);
                    json.WriteEndObject();
                }
                writer.Write(Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length));
                writer.Write('\n');
                long now = clock.ElapsedMilliseconds;
                if (now - lastFlush >= 1000)
                {
                    writer.Flush();
                    lastFlush = now;
                }
            }
        }

        private static void Array(Utf8JsonWriter json, string name, float[] values)
        {
            json.WriteStartArray(name);
            foreach (float value in values) json.WriteNumberValue(Math.Round(value, 4));
            json.WriteEndArray();
        }

        private static void Array(Utf8JsonWriter json, string name, string[] values)
        {
            json.WriteStartArray(name);
            foreach (string value in values) json.WriteStringValue(value);
            json.WriteEndArray();
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                writer.Flush();
                writer.Dispose();
            }
        }
    }
}

// ============================================================================
// GLOSSARY: FTRecorder recordings
// ============================================================================
//
// File format (".ftrec.jsonl"): one JSON object per line.
//   Header                   {"format":"RiryTools.FTRecorder","version":1,"source":..,"created":ISO UTC,
//                            "unified":[Unified Expression names],"eye":[eye value names]}
//   Expressions              {"t":s,"unified":[..],"eye":[..]}  values in the header's order, 4 decimals.
//   Osc                      {"t":s,"osc":base64}  one OSC packet exactly as VRCFaceTracking sent it.
//   Parameter                {"t":s,"param":"Viseme","i":n} or "f" or "b": what VRChat sent.
//   Viseme                   {"t":s,"viseme":n,"voice":v,"weights":[15]}  made from audio; VRChat's viseme order.
//
// FTRecorderRecording (this file)
//   Load(path)               Reads a recording; a bad line is refused with its number. Source, Created, Unified, Eye,
//                            Entries (in written order), Duration (last entry's time).
// FTRecorderEntry            Time, Kind (Expressions, Osc, Parameter, Viseme) and that kind's fields.
// FTRecorderRecordingWriter  Writes as it happens, from any thread; flushed at least once a second.
//   NewPath(folder, prefix)  prefix_yyyy-MM-dd_HH-mm-ss.ftrec.jsonl, numbered when taken.
//   Now                      Seconds since the writer was made.
//   WriteExpressions, WriteOsc, WriteParameter, WriteViseme  One line each.
