"""Visemes from speech without any third-party model: every 10 ms of 16 kHz audio is measured (loudness, voicing,
spectral balance, the first three formants from a linear prediction envelope, sudden onsets) and sorted into VRChat's
15 visemes by rules. Less exact than a trained model: it does not know the speaker, and stops and nasals are guesses
from a few cues. Used only when asked for (process --visemes classifier). Needs numpy."""

from __future__ import annotations

import numpy as np

SAMPLE_RATE = 16000
HOP = 160
_WINDOW = 400
_FFT = 512
_ORDER = 14
_BURST_HOLD = 3
SIL, PP, FF, TH, DD, KK, CH, SS, NN, RR, AA, E, IH, OH, OU = range(15)
# Typical adult vowel formants (F1, F2) in Hz, for the vowel visemes.
_VOWELS = [(AA, 750.0, 1250.0), (E, 550.0, 1900.0), (IH, 350.0, 2150.0), (OH, 500.0, 900.0), (OU, 330.0, 850.0)]


class VisemeFrame:
    """One measurement: time (the middle of the window, seconds), the 15 weights, the strongest viseme and Voice."""

    __slots__ = ("time", "weights", "viseme", "voice")

    def __init__(self, time, weights, viseme, voice):
        self.time, self.weights, self.viseme, self.voice = time, weights, viseme, voice


def classify(samples) -> list[VisemeFrame]:
    """One frame per 10 ms of 16 kHz mono 16-bit samples: the 15 weights (summing to 1), the strongest, and Voice."""
    audio = np.asarray(samples, dtype=np.float64) / 32768.0
    count = 0 if len(audio) < _WINDOW else (len(audio) - _WINDOW) // HOP + 1
    if count == 0:
        return []
    starts = np.arange(count) * HOP
    frames = audio[starts[:, None] + np.arange(_WINDOW)[None, :]]
    features = _measure(frames)
    db = features["db"]
    levels = np.sort(db)
    floor = levels[count // 10]
    threshold = max(floor + 10, -55.0)
    voiced = np.sort(db[(db >= threshold) & (features["voicing"] >= 0.35)])
    voiced_median = voiced[len(voiced) // 2] if len(voiced) else threshold + 20

    result = []
    smooth = np.zeros(15)
    burst, burst_left = -1, 0
    for i in range(count):
        f_db = db[i]
        weights = np.zeros(15)
        silent = f_db < threshold
        before = min(db[max(0, i - 3):i]) if i > 0 else float("inf")
        centroid = features["centroid"][i]
        if not silent and i > 0 and before < threshold + 6 and f_db - before > 12:
            burst = PP if centroid < 1800 else KK if centroid < 3500 else DD
            burst_left = _BURST_HOLD
        f1, f2, f3 = features["f1"][i], features["f2"][i], features["f3"][i]
        if burst_left > 0:
            weights[burst] = 1
            burst_left -= 1
        elif silent:
            weights[SIL] = 1
        elif features["voicing"][i] < 0.35 and features["high"][i] > 0.35:
            fricative = SS if centroid > 5000 else CH if centroid > 3000 else FF
            if fricative != SS and f_db < threshold + 8 and centroid < 4000:
                fricative = TH
            weights[fricative] = 1
        elif features["low"][i] > 0.85 and f1 < 400 and f_db < voiced_median - 6:
            weights[NN] = 1
        elif 0 < f3 < 2000 and 900 < f2 < 1600:
            weights[RR] = 1
        elif f1 > 0 and f2 > 0:
            _vowel_weights(f1, f2, weights)
        else:
            weights[AA] = 1
        # Light smoothing (half old, half new) keeps single odd frames from flickering.
        smooth = weights.copy() if i == 0 else smooth * 0.5 + weights * 0.5
        total = smooth.sum()
        output = smooth / total if total > 0 else np.zeros(15)
        output = output.astype(np.float32)
        best = int(np.argmax(output))
        voice = float(min(1.0, max(0.0, (f_db - threshold) / 30.0)))
        result.append(VisemeFrame((i * HOP + _WINDOW / 2.0) / SAMPLE_RATE, [float(v) for v in output], best, voice))
    return result


def _vowel_weights(f1, f2, weights):
    scores = np.array([np.exp(-(((f1 - v1) / 150.0) ** 2 + ((f2 - v2) / 400.0) ** 2)) for _, v1, v2 in _VOWELS])
    total = scores.sum()
    for (viseme, _, _), score in zip(_VOWELS, scores):
        weights[viseme] = score / total if total > 0 else 0.2


def _autocorrelation(frames, lags):
    """Linear autocorrelation of each row for lags 0..lags-1 (zero padded, so no wrap-around)."""
    size = 1 << int(np.ceil(np.log2(2 * frames.shape[1])))
    spectrum = np.fft.rfft(frames, size, axis=1)
    return np.fft.irfft(np.abs(spectrum) ** 2, size, axis=1)[:, :lags]


def _measure(frames):
    count = frames.shape[0]
    energy = (frames ** 2).sum(axis=1)
    db = 10 * np.log10(energy / _WINDOW + 1e-12)
    # Voicing: the strongest normalised autocorrelation between 70 Hz and 400 Hz (never below 0).
    low_lag, high_lag = SAMPLE_RATE // 400, min(SAMPLE_RATE // 70, _WINDOW - 1)
    r = _autocorrelation(frames, high_lag + 1)
    zero = energy
    with np.errstate(divide="ignore", invalid="ignore"):
        ratio = np.where(zero[:, None] > 0, r[:, low_lag:high_lag + 1] / zero[:, None], 0.0)
    voicing = np.maximum(ratio.max(axis=1), 0.0)
    # Pre-emphasis and window, then the spectrum.
    hamming = 0.54 - 0.46 * np.cos(2 * np.pi * np.arange(_WINDOW) / (_WINDOW - 1))
    previous = np.concatenate([np.zeros((count, 1)), frames[:, :-1]], axis=1)
    shaped = (frames - 0.97 * previous) * hamming
    spectrum = np.fft.fft(shaped, _FFT, axis=1)[:, 1:_FFT // 2 + 1]
    power = spectrum.real ** 2 + spectrum.imag ** 2
    hz = np.arange(1, _FFT // 2 + 1) * SAMPLE_RATE / _FFT
    total = power.sum(axis=1)
    safe = np.where(total > 0, total, 1.0)
    low = np.where(total > 0, power[:, hz < 1000].sum(axis=1) / safe, 0.0)
    high = np.where(total > 0, power[:, hz >= 3000].sum(axis=1) / safe, 0.0)
    centroid = np.where(total > 0, (power * hz).sum(axis=1) / safe, 0.0)
    lpc_r = _autocorrelation(shaped, _ORDER + 1)
    f1 = np.zeros(count)
    f2 = np.zeros(count)
    f3 = np.zeros(count)
    for i in range(count):
        f1[i], f2[i], f3[i] = _formants(lpc_r[i])
    return {"db": db, "voicing": voicing, "low": low, "high": high, "centroid": centroid, "f1": f1, "f2": f2, "f3": f3}


_POINTS = 256
_ENVELOPE_BASIS = np.exp(-1j * np.outer(np.pi * np.arange(_POINTS) / _POINTS, np.arange(_ORDER + 1)))
_PEAK_HZ = np.arange(_POINTS) * (SAMPLE_RATE / 2.0) / _POINTS


def _formants(r):
    """F1, F2, F3 (0 when not found): peaks of the linear prediction envelope (Levinson-Durbin)."""
    r = r.copy()
    if r[0] <= 0:
        return 0.0, 0.0, 0.0
    r[0] *= 1.0001
    a = np.zeros(_ORDER + 1)
    a[0] = 1.0
    error = r[0]
    for m in range(1, _ORDER + 1):
        acc = r[m] + np.dot(a[1:m], r[m - 1:0:-1])
        k = -acc / error
        previous = a.copy()
        a[1:m] = previous[1:m] + k * previous[m - 1:0:-1]
        a[m] = k
        error *= 1 - k * k
        if error <= 0:
            return 0.0, 0.0, 0.0
    response = _ENVELOPE_BASIS @ a
    envelope = 1.0 / (response.real ** 2 + response.imag ** 2 + 1e-12)
    peaks = [p for p in range(1, _POINTS - 1) if envelope[p] > envelope[p - 1] and envelope[p] >= envelope[p + 1] and _PEAK_HZ[p] > 150]
    f1 = f2 = f3 = 0.0
    for p in peaks:
        frequency = _PEAK_HZ[p]
        if f1 == 0:
            if frequency <= 1000:
                f1 = frequency
        elif f2 == 0:
            if max(f1 + 200, 600) <= frequency <= 2800:
                f2 = frequency
        elif f3 == 0 and f2 + 200 <= frequency <= 3600:
            f3 = frequency
    return f1, f2, f3

# ============================================================================
# GLOSSARY: classifier
# ============================================================================
#
# classifier (this file)   FTRecorder's own rule-based viseme classifier (numpy), for process --visemes classifier.
#   classify(samples)        VisemeFrame per 10 ms (400-sample windows): silence below max(10th percentile + 10 dB,
#                            -55 dBFS); bursts after a closure held 3 frames (PP/kk/DD by spectral centroid); unvoiced
#                            noise as SS/CH/FF (TH when quiet and low); nasal (nn), r-coloured (RR); vowels by distance to
#                            typical (F1, F2); half-and-half smoothing; Voice: 0 at the threshold, 1 at 30 dB above.
#   VisemeFrame              time, weights[15], viseme, voice.
