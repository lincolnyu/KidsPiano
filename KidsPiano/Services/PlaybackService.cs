using NAudio.Midi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KidsPiano.Models;

namespace KidsPiano.Services
{
    /// <summary>
    /// Plays back a Piece measure-by-measure using NAudio's software MIDI synth.
    /// Fires OnMeasureStarted when each measure begins so the UI can highlight it.
    /// Also drives the metronome tick.
    /// </summary>
    public class PlaybackService : IDisposable
    {
        // ── Events ─────────────────────────────────────────────────────────────
        public event Action<int>?  OnMeasureStarted;   // measure index (0-based)
        public event Action?       OnPlaybackFinished;
        public event Action?       OnMetronomeTick;

        // ── Config ─────────────────────────────────────────────────────────────
        private const int MidiChannel   = 1;   // 1-based channel for piano (ch 1 = index 0)
        private const int PianoProgram  = 0;   // GM program 0 = Grand Piano
        private const int DefaultTempo  = 120; // BPM when no tempo marking in score

        // ── State ──────────────────────────────────────────────────────────────
        private MidiOut?            _midiOut;
        private CancellationTokenSource? _cts;
        private Task?               _playTask;
        private bool                _disposed;

        public bool IsPlaying { get; private set; }

        // Volume 0-100
        private int _volume = 80;
        public int Volume
        {
            get => _volume;
            set
            {
                _volume = Math.Clamp(value, 0, 100);
                // MIDI velocity is 0-127
                // We'll scale note velocity on next notes
            }
        }

        // Metronome volume 0-100; 0 = disabled
        private int _metronomeVolume = 50;
        public int MetronomeVolume
        {
            get => _metronomeVolume;
            set => _metronomeVolume = Math.Clamp(value, 0, 100);
        }

        public PlaybackService()
        {
            TryOpenMidiOut();
        }

        private void TryOpenMidiOut()
        {
            if (MidiOut.NumberOfDevices == 0) return;
            try
            {
                _midiOut = new MidiOut(0);
                // Set piano patch on channel 1
                _midiOut.Send(MidiMessage.ChangePatch(PianoProgram, MidiChannel).RawData);
            }
            catch { _midiOut = null; }
        }

        // ── Public API ─────────────────────────────────────────────────────────

        public void Play(Piece piece, int startMeasureIndex, double speedMultiplier)
        {
            if (IsPlaying) Stop();
            if (_midiOut == null) return;

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            IsPlaying = true;

            _playTask = Task.Run(async () =>
            {
                try
                {
                    await PlayPiece(piece, startMeasureIndex, speedMultiplier, token);
                }
                catch (OperationCanceledException) { }
                finally
                {
                    AllNotesOff();
                    IsPlaying = false;
                    OnPlaybackFinished?.Invoke();
                }
            }, token);
        }

        public void Stop()
        {
            _cts?.Cancel();
            _playTask?.Wait(500);
            _cts?.Dispose();
            _cts = null;
            _playTask = null;
            AllNotesOff();
            IsPlaying = false;
        }

        // ── Playback loop ──────────────────────────────────────────────────────

        private async Task PlayPiece(Piece piece, int startIndex, double speed, CancellationToken token)
        {
            // Determine BPM (use default; later we can read tempo from MusicXML)
            double bpm = DefaultTempo * speed;
            double beatMs = 60000.0 / bpm;  // ms per quarter note

            for (int mi = startIndex; mi < piece.Measures.Count; mi++)
            {
                token.ThrowIfCancellationRequested();

                var measure = piece.Measures[mi];
                OnMeasureStarted?.Invoke(mi);

                // Metronome: fire ticks at the start of each beat in this measure
                int beats = measure.BeatsPerMeasure;
                var metronomeTask = RunMetronomeForMeasure(beats, beatMs, token);

                // Build note events: we need to know when each note ends
                // Notes in MusicXML are sequential (or chords). We replay them in order.
                await PlayMeasureNotes(measure, beatMs, speed, token);

                // Wait for metronome task to complete for this measure
                await metronomeTask;
            }
        }

        private async Task PlayMeasureNotes(Measure measure, double beatMs, double speed, CancellationToken token)
        {
            if (_midiOut == null) return;

            // Group notes into time slots (chord members share the same onset)
            // Notes are stored sequentially; chord members have IsChordMember = true
            var slots = new List<List<Note>>();
            List<Note>? current = null;

            foreach (var n in measure.Notes)
            {
                if (n.IsChordMember && current != null)
                    current.Add(n);
                else
                {
                    current = new List<Note> { n };
                    slots.Add(current);
                }
            }

            foreach (var slot in slots)
            {
                token.ThrowIfCancellationRequested();

                int velocity = (int)(Volume / 100.0 * 100 + 27); // map 0-100 → 27-127
                velocity = Math.Clamp(velocity, 0, 127);

                // Note on
                foreach (var n in slot)
                    if (n.MidiPitch >= 21 && n.MidiPitch <= 108)
                        _midiOut.Send(MidiMessage.StartNote(n.MidiPitch, velocity, MidiChannel).RawData);

                // Duration = shortest note in chord (they should all be the same in practice)
                double durationBeats = slot.Min(n => n.Duration);
                int durationMs = (int)(durationBeats * beatMs);
                durationMs = Math.Max(durationMs, 50);

                await Task.Delay(durationMs, token);

                // Note off
                foreach (var n in slot)
                    if (n.MidiPitch >= 21 && n.MidiPitch <= 108)
                        _midiOut.Send(MidiMessage.StopNote(n.MidiPitch, 0, MidiChannel).RawData);
            }
        }

        private async Task RunMetronomeForMeasure(int beats, double beatMs, CancellationToken token)
        {
            if (_metronomeVolume == 0 || _midiOut == null) return;

            for (int b = 0; b < beats; b++)
            {
                token.ThrowIfCancellationRequested();
                OnMetronomeTick?.Invoke();

                if (_metronomeVolume > 0)
                {
                    // Use channel 10 (percussion) — note 37 = Side Stick, 76 = High Wood Block
                    int note = (b == 0) ? 76 : 77; // accent on beat 1
                    int vel  = (int)(_metronomeVolume / 100.0 * 100 + 27);
                    vel = Math.Clamp(vel, 0, 127);
                    _midiOut.Send(MidiMessage.StartNote(note, vel, 9).RawData); // ch 10 = index 9
                    await Task.Delay(30, token);
                    _midiOut.Send(MidiMessage.StopNote(note, 0, 9).RawData);
                }

                await Task.Delay(Math.Max(0, (int)beatMs - 30), token);
            }
        }

        private void AllNotesOff()
        {
            if (_midiOut == null) return;
            try
            {
                for (int ch = 0; ch < 16; ch++)
                    _midiOut.Send(new MidiMessage(0xB0 | ch, 123, 0).RawData); // All Notes Off CC
            }
            catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            _midiOut?.Dispose();
        }
    }
}
