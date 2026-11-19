using System;
using System.Collections.Generic;

namespace Riry.RiryTools.FTRecorder
{
    /// <summary>
    /// Visemes from speech without any third-party model: every 10 ms of 16 kHz audio is measured (loudness, voicing,
    /// spectral balance, the first three formants from a linear prediction envelope, sudden onsets) and sorted into
    /// VRChat's 15 visemes by rules. Vowels are scored by their distance to typical adult formant positions; fricatives
    /// by where their noise lies; stops by the burst after a closure. Less exact than a trained model: it does not know
    /// the speaker, and stops and nasals are guesses from a few cues.
    /// </summary>
    internal sealed class FTRecorderVisemeClassifier
    {
        internal const int SampleRate = 16000;
        internal const int Hop = 160;
        private const int Window = 400;
        private const int Fft = 512;
        private const int Order = 14;
        private const int BurstHold = 3;

        private const int Sil = 0, PP = 1, FF = 2, TH = 3, DD = 4, KK = 5, CH = 6, SS = 7, NN = 8, RR = 9, AA = 10, E = 11, IH = 12, OH = 13, OU = 14;

        // Typical adult vowel formants (F1, F2) in Hz, for the vowel visemes.
        private static readonly (int viseme, float f1, float f2)[] Vowels =
        {
            (AA, 750f, 1250f), (E, 550f, 1900f), (IH, 350f, 2150f), (OH, 500f, 900f), (OU, 330f, 850f),
        };

        private readonly double[] hamming = new double[Window];
        private readonly double[] re = new double[Fft];
        private readonly double[] im = new double[Fft];

        internal FTRecorderVisemeClassifier()
        {
            for (int i = 0; i < Window; i++) hamming[i] = 0.54 - 0.46 * Math.Cos(2 * Math.PI * i / (Window - 1));
        }

        private struct Features
        {
            internal double Db, Voicing, HighRatio, LowRatio, Centroid, F1, F2, F3;
        }

        /// <summary>One entry per 10 ms: the 15 weights (summing to 1), the strongest viseme, and Voice (0 to 1).</summary>
        internal List<FTRecorderVisemeFrame> Classify(short[] samples)
        {
            int count = samples.Length < Window ? 0 : (samples.Length - Window) / Hop + 1;
            var features = new Features[count];
            var levels = new List<double>(count);
            for (int i = 0; i < count; i++)
            {
                features[i] = Measure(samples, i * Hop);
                levels.Add(features[i].Db);
            }
            // The quiet between words sets the threshold: 10 dB above the 10th percentile, and never under -55 dBFS.
            levels.Sort();
            double floor = count > 0 ? levels[count / 10] : -90;
            double threshold = Math.Max(floor + 10, -55);
            double voicedMedian = Median(features, threshold);

            var frames = new List<FTRecorderVisemeFrame>(count);
            var smooth = new float[15];
            int burst = -1, burstLeft = 0;
            for (int i = 0; i < count; i++)
            {
                Features f = features[i];
                var weights = new float[15];
                bool silent = f.Db < threshold;
                double before = double.MaxValue;
                for (int back = 1; back <= 3 && i - back >= 0; back++) before = Math.Min(before, features[i - back].Db);
                if (!silent && i > 0 && before < threshold + 6 && f.Db - before > 12)
                {
                    burst = f.Centroid < 1800 ? PP : f.Centroid < 3500 ? KK : DD;
                    burstLeft = BurstHold;
                }
                if (burstLeft > 0)
                {
                    weights[burst] = 1;
                    burstLeft--;
                }
                else if (silent) weights[Sil] = 1;
                else if (f.Voicing < 0.35 && f.HighRatio > 0.35)
                {
                    int fricative = f.Centroid > 5000 ? SS : f.Centroid > 3000 ? CH : FF;
                    if (fricative != SS && f.Db < threshold + 8 && f.Centroid < 4000) fricative = TH;
                    weights[fricative] = 1;
                }
                else if (f.LowRatio > 0.85 && f.F1 < 400 && f.Db < voicedMedian - 6) weights[NN] = 1;
                else if (f.F3 > 0 && f.F3 < 2000 && f.F2 > 900 && f.F2 < 1600) weights[RR] = 1;
                else if (f.F1 > 0 && f.F2 > 0) VowelWeights(f.F1, f.F2, weights);
                else weights[AA] = 1;

                // Light smoothing (half old, half new) keeps single odd frames from flickering.
                float sum = 0;
                for (int k = 0; k < 15; k++)
                {
                    smooth[k] = i == 0 ? weights[k] : smooth[k] * 0.5f + weights[k] * 0.5f;
                    sum += smooth[k];
                }
                var output = new float[15];
                int best = 0;
                for (int k = 0; k < 15; k++)
                {
                    output[k] = sum > 0 ? smooth[k] / sum : 0;
                    if (output[k] > output[best]) best = k;
                }
                float voice = (float)Math.Clamp((f.Db - threshold) / 30.0, 0, 1);
                frames.Add(new FTRecorderVisemeFrame { Time = (i * Hop + Window / 2.0) / SampleRate, Weights = output, Viseme = best, Voice = voice });
            }
            return frames;
        }

        private static void VowelWeights(double f1, double f2, float[] weights)
        {
            double total = 0;
            var scores = new double[Vowels.Length];
            for (int v = 0; v < Vowels.Length; v++)
            {
                double d1 = (f1 - Vowels[v].f1) / 150.0, d2 = (f2 - Vowels[v].f2) / 400.0;
                scores[v] = Math.Exp(-(d1 * d1 + d2 * d2));
                total += scores[v];
            }
            for (int v = 0; v < Vowels.Length; v++) weights[Vowels[v].viseme] = (float)(total > 0 ? scores[v] / total : 0.2);
        }

        private static double Median(Features[] features, double threshold)
        {
            var voiced = new List<double>();
            foreach (Features f in features)
            {
                if (f.Db >= threshold && f.Voicing >= 0.35) voiced.Add(f.Db);
            }
            if (voiced.Count == 0) return threshold + 20;
            voiced.Sort();
            return voiced[voiced.Count / 2];
        }

        private Features Measure(short[] samples, int start)
        {
            var f = new Features();
            var frame = new double[Window];
            double energy = 0;
            for (int i = 0; i < Window; i++)
            {
                frame[i] = samples[start + i] / 32768.0;
                energy += frame[i] * frame[i];
            }
            f.Db = 10 * Math.Log10(energy / Window + 1e-12);

            // Voicing: the strongest normalised autocorrelation between 70 Hz and 400 Hz.
            double zero = 0;
            for (int i = 0; i < Window; i++) zero += frame[i] * frame[i];
            for (int lag = SampleRate / 400; lag <= SampleRate / 70 && lag < Window; lag++)
            {
                double sum = 0;
                for (int i = 0; i + lag < Window; i++) sum += frame[i] * frame[i + lag];
                if (zero > 0) f.Voicing = Math.Max(f.Voicing, sum / zero);
            }

            // Pre-emphasis and window, then the spectrum.
            var shaped = new double[Window];
            for (int i = 0; i < Window; i++) shaped[i] = (frame[i] - (i > 0 ? 0.97 * frame[i - 1] : 0)) * hamming[i];
            Array.Clear(re, 0, Fft);
            Array.Clear(im, 0, Fft);
            Array.Copy(shaped, re, Window);
            Transform(re, im);
            double total = 0, low = 0, high = 0, weighted = 0;
            for (int k = 1; k <= Fft / 2; k++)
            {
                double power = re[k] * re[k] + im[k] * im[k];
                double hz = k * (double)SampleRate / Fft;
                total += power;
                weighted += power * hz;
                if (hz < 1000) low += power;
                else if (hz >= 3000) high += power;
            }
            if (total > 0)
            {
                f.LowRatio = low / total;
                f.HighRatio = high / total;
                f.Centroid = weighted / total;
            }
            Formants(shaped, ref f);
            return f;
        }

        // Formants: peaks of the linear prediction envelope (Levinson-Durbin on the autocorrelation).
        private static void Formants(double[] shaped, ref Features f)
        {
            var r = new double[Order + 1];
            for (int lag = 0; lag <= Order; lag++)
            {
                for (int i = 0; i + lag < shaped.Length; i++) r[lag] += shaped[i] * shaped[i + lag];
            }
            if (r[0] <= 0) return;
            r[0] *= 1.0001;
            var a = new double[Order + 1];
            var previous = new double[Order + 1];
            a[0] = 1;
            double error = r[0];
            for (int m = 1; m <= Order; m++)
            {
                double acc = r[m];
                for (int j = 1; j < m; j++) acc += a[j] * r[m - j];
                double k = -acc / error;
                Array.Copy(a, previous, Order + 1);
                for (int j = 1; j < m; j++) a[j] = previous[j] + k * previous[m - j];
                a[m] = k;
                error *= 1 - k * k;
                if (error <= 0) return;
            }
            const int points = 256;
            var envelope = new double[points];
            for (int p = 0; p < points; p++)
            {
                double w = Math.PI * p / points, sumRe = 0, sumIm = 0;
                for (int j = 0; j <= Order; j++)
                {
                    sumRe += a[j] * Math.Cos(w * j);
                    sumIm -= a[j] * Math.Sin(w * j);
                }
                envelope[p] = 1.0 / (sumRe * sumRe + sumIm * sumIm + 1e-12);
            }
            var peaks = new List<double>();
            for (int p = 1; p < points - 1; p++)
            {
                if (envelope[p] > envelope[p - 1] && envelope[p] >= envelope[p + 1])
                {
                    double hz = p * (SampleRate / 2.0) / points;
                    if (hz > 150) peaks.Add(hz);
                }
            }
            foreach (double hz in peaks)
            {
                if (f.F1 == 0) { if (hz <= 1000) f.F1 = hz; }
                else if (f.F2 == 0) { if (hz >= Math.Max(f.F1 + 200, 600) && hz <= 2800) f.F2 = hz; }
                else if (f.F3 == 0 && hz >= f.F2 + 200 && hz <= 3600) f.F3 = hz;
            }
        }

        // In-place radix-2 FFT.
        private static void Transform(double[] re, double[] im)
        {
            int n = re.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j)
                {
                    (re[i], re[j]) = (re[j], re[i]);
                    (im[i], im[j]) = (im[j], im[i]);
                }
            }
            for (int length = 2; length <= n; length <<= 1)
            {
                double angle = -2 * Math.PI / length;
                for (int i = 0; i < n; i += length)
                {
                    for (int k = 0; k < length / 2; k++)
                    {
                        double wr = Math.Cos(angle * k), wi = Math.Sin(angle * k);
                        int a = i + k, b = i + k + length / 2;
                        double xr = re[b] * wr - im[b] * wi, xi = re[b] * wi + im[b] * wr;
                        re[b] = re[a] - xr;
                        im[b] = im[a] - xi;
                        re[a] += xr;
                        im[a] += xi;
                    }
                }
            }
        }
    }

    /// <summary>One viseme measurement.</summary>
    internal sealed class FTRecorderVisemeFrame
    {
        /// <summary>Seconds from the start of the audio (the middle of the measured window).</summary>
        internal double Time;
        /// <summary>VRChat's 15 visemes: sil, PP, FF, TH, DD, kk, CH, SS, nn, RR, aa, E, ih, oh, ou.</summary>
        internal float[] Weights;
        internal int Viseme;
        internal float Voice;
    }
}

// ============================================================================
// GLOSSARY: FTRecorder viseme classifier
// ============================================================================
//
// FTRecorderVisemeClassifier (this file)  Visemes from 16 kHz mono audio with rules, no third-party model.
//   Classify(samples)        One FTRecorderVisemeFrame per 10 ms (25 ms Hamming window).
//   Silence                  Under the 10th-percentile level + 10 dB (at least -55 dBFS): sil.
//   Stops                    A rise of more than 12 dB from near silence: PP (centroid under 1.8 kHz), kk (to 3.5 kHz)
//                            or DD, held 30 ms.
//   Fricatives               Unvoiced (autocorrelation under 0.35, 70-400 Hz) with over 35% energy above 3 kHz: SS
//                            (centroid over 5 kHz), CH (over 3 kHz), FF; quiet and under 4 kHz: TH.
//   nn                       Over 85% energy under 1 kHz, F1 under 400 Hz, 6 dB under the voiced median.
//   RR                       F3 under 2 kHz with F2 between 900 and 1600 Hz.
//   Vowels                   aa (750, 1250), E (550, 1900), ih (350, 2150), oh (500, 900), ou (330, 850): weights by
//                            Gaussian distance (150 Hz on F1, 400 Hz on F2). Formants from order-14 LPC envelope peaks.
//   Smoothing                Half the previous weights, half the new; Voice = (level - threshold) / 30 dB.
// FTRecorderVisemeFrame      Time, Weights (15, VRChat order), Viseme (strongest), Voice.
