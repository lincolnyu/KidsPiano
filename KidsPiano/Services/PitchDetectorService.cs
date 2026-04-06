using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace KidsPiano.Services
{
    /// <summary>
    /// Listens to microphone input and detects piano pitches in real time using
    /// Harmonic Product Spectrum (HPS). Emits a list of detected MIDI pitches
    /// to the caller via the NotesDetected event / callback.
    /// </summary>
    public class PitchDetectorService : IDisposable
    {
        // ── Config ────────────────────────────────────────────────────────────
        private const int SampleRate      = 44100;
        private const int FftSize         = 8192;      // ~5.4 Hz/bin resolution
        private const int HpsHarmonics    = 4;         // HPS downsampling passes
        private const double NoiseFloor   = 0.015;     // relative magnitude threshold
        private const double MinFreq      = 27.5;      // A0 - lowest piano key
        private const double MaxFreq      = 4186.0;    // C8 - highest piano key
        private const double MinSemitoneGap = 4.0;     // suppress near-octave duplicates
        private const double PeakProminence = 1.8;     // peak/neighbourhood ratio

        // ── State ─────────────────────────────────────────────────────────────
        private WaveInEvent?     _mic;
        private readonly float[] _ring = new float[FftSize * 2];
        private int              _writePos;
        private int              _samplesSinceLastAnalysis;

        // Analyse every ~100 ms
        private const int AnalysisInterval = SampleRate / 10;

        public bool IsListening { get; private set; }

        // Callback (legacy ctor kept for backward compat with MainWindow)
        private readonly Action<List<int>>? _callback;
        public PitchDetectorService(Action<List<int>> onNotesDetected)
        {
            _callback = onNotesDetected;
        }

        // ── Public API ────────────────────────────────────────────────────────
        public void StartListening()
        {
            if (IsListening) return;
            _mic = new WaveInEvent
            {
                WaveFormat        = new WaveFormat(SampleRate, 16, 1),
                BufferMilliseconds = 50
            };
            _mic.DataAvailable += OnMicData;
            _mic.StartRecording();
            IsListening = true;
        }

        public void StopListening()
        {
            if (_mic is not null)
            {
                _mic.StopRecording();
                _mic.DataAvailable -= OnMicData;
                _mic.Dispose();
                _mic = null;
            }
            IsListening = false;
        }

        // ── Data handler ──────────────────────────────────────────────────────
        private void OnMicData(object? sender, WaveInEventArgs e)
        {
            int sampleCount = e.BytesRecorded / 2;
            for (int i = 0; i < sampleCount; i++)
            {
                short raw = BitConverter.ToInt16(e.Buffer, i * 2);
                _ring[_writePos % _ring.Length] = raw / 32768f;
                _writePos++;
            }

            _samplesSinceLastAnalysis += sampleCount;
            if (_samplesSinceLastAnalysis < AnalysisInterval) return;
            _samplesSinceLastAnalysis = 0;

            // Unwrap ring buffer into contiguous frame
            var frame = new float[FftSize];
            int start = _writePos - FftSize;
            for (int i = 0; i < FftSize; i++)
                frame[i] = _ring[(start + i + _ring.Length * 4) % _ring.Length];

            var notes = Analyse(frame);
            _callback?.Invoke(notes);
        }

        // ── DSP pipeline ──────────────────────────────────────────────────────
        private static List<int> Analyse(float[] frame)
        {
            // 1. Hann window
            var windowed = new Complex[FftSize];
            for (int i = 0; i < FftSize; i++)
            {
                double w = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (FftSize - 1)));
                windowed[i] = new Complex(frame[i] * w, 0);
            }

            // 2. FFT
            Fft(windowed);

            // 3. Magnitude spectrum (one-sided, normalised)
            int bins = FftSize / 2;
            double[] mag = new double[bins];
            double maxMag = 0;
            for (int i = 0; i < bins; i++)
            {
                mag[i] = windowed[i].Magnitude;
                if (mag[i] > maxMag) maxMag = mag[i];
            }
            if (maxMag < 1e-6) return new List<int>(); // silence
            for (int i = 0; i < bins; i++) mag[i] /= maxMag;

            // 4. Harmonic Product Spectrum
            double[] hps = HarmonicProductSpectrum(mag, bins, HpsHarmonics);

            // 5. Peaks → MIDI
            return PeaksToMidi(hps, bins);
        }

        private static double[] HarmonicProductSpectrum(double[] mag, int bins, int harmonics)
        {
            double[] hps = (double[])mag.Clone();
            for (int h = 2; h <= harmonics; h++)
                for (int i = 0; i < bins; i++)
                {
                    int src = i * h;
                    hps[i] *= (src < bins) ? mag[src] : 0;
                }
            return hps;
        }

        private static List<int> PeaksToMidi(double[] hps, int bins)
        {
            double freqPerBin = (double)SampleRate / FftSize;
            int minBin = Math.Max(1, (int)(MinFreq / freqPerBin));
            int maxBin = Math.Min(bins - 2, (int)(MaxFreq / freqPerBin));

            // Normalise HPS in valid range
            double hpsMax = 0;
            for (int i = minBin; i <= maxBin; i++) if (hps[i] > hpsMax) hpsMax = hps[i];
            if (hpsMax < 1e-12) return new List<int>();
            for (int i = 0; i < bins; i++) hps[i] /= hpsMax;

            int halfWindow = 8;
            var candidates = new List<(double freq, double strength)>();

            for (int i = minBin; i <= maxBin; i++)
            {
                if (hps[i] < NoiseFloor) continue;

                // Must be local maximum (±2 bins)
                bool isPeak = true;
                for (int d = -2; d <= 2; d++)
                {
                    if (d == 0) continue;
                    int j = i + d;
                    if (j >= 0 && j < bins && hps[j] >= hps[i]) { isPeak = false; break; }
                }
                if (!isPeak) continue;

                // Local prominence check
                double localSum = 0; int localCount = 0;
                for (int d = -halfWindow; d <= halfWindow; d++)
                {
                    int j = i + d;
                    if (j >= 0 && j < bins) { localSum += hps[j]; localCount++; }
                }
                double localAvg = localCount > 0 ? localSum / localCount : 0;
                if (localAvg < 1e-12 || hps[i] / localAvg < PeakProminence) continue;

                // Parabolic interpolation for sub-bin accuracy
                double alpha = i > 0      ? hps[i - 1] : 0;
                double beta  = hps[i];
                double gamma = i < bins-1 ? hps[i + 1] : 0;
                double denom = alpha - 2 * beta + gamma;
                double offset = denom != 0 ? 0.5 * (alpha - gamma) / denom : 0;
                double freq = (i + offset) * freqPerBin;

                candidates.Add((freq, hps[i]));
            }

            // Sort by strength
            candidates.Sort((a, b) => b.strength.CompareTo(a.strength));

            // Convert to MIDI, suppress duplicates within MinSemitoneGap
            var accepted = new List<int>();
            foreach (var (freq, _) in candidates)
            {
                if (freq < MinFreq) continue;
                double midi = 69 + 12 * Math.Log2(freq / 440.0);
                int midiInt = (int)Math.Round(midi);
                if (midiInt < 21 || midiInt > 108) continue;

                bool tooClose = false;
                foreach (int ex in accepted)
                    if (Math.Abs(ex - midiInt) < MinSemitoneGap) { tooClose = true; break; }
                if (tooClose) continue;

                accepted.Add(midiInt);
                if (accepted.Count >= 6) break;
            }

            accepted.Sort();
            return accepted;
        }

        // ── Cooley-Tukey FFT ──────────────────────────────────────────────────
        private static void Fft(Complex[] buf)
        {
            int n = buf.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j) (buf[i], buf[j]) = (buf[j], buf[i]);
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = -2 * Math.PI / len;
                Complex wLen = new(Math.Cos(ang), Math.Sin(ang));
                for (int i = 0; i < n; i += len)
                {
                    Complex w = Complex.One;
                    for (int j = 0; j < len / 2; j++)
                    {
                        Complex u = buf[i + j], v = buf[i + j + len / 2] * w;
                        buf[i + j]           = u + v;
                        buf[i + j + len / 2] = u - v;
                        w *= wLen;
                    }
                }
            }
        }

        public void Dispose() => StopListening();
    }
}
