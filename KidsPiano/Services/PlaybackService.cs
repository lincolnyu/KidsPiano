using KidsPiano.Models;
using NAudio.Midi;

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
            for (int mi = startIndex; mi < piece.Measures.Count; mi++)
            {
                token.ThrowIfCancellationRequested();

                var measure = piece.Measures[mi];
                OnMeasureStarted?.Invoke(mi);

                // tempo is the number of quarter notes (beats) / minute
                // each minute there are `tempo` beats
                // each beat takes 60/tempo secs = 60000 ms / tempo
                double beatMs = (60000 / measure.Tempo) * speed;

                //// Metronome: fire ticks at the start of each beat in this measure
                //int beats = measure.BeatsPerMeasure;
                //var metronomeTask = RunMetronomeForMeasure(beats, beatMs, token);

                // Build note events: we need to know when each note ends
                // Notes in MusicXML are sequential (or chords). We replay them in order.
                await PlayMeasureNotes(measure, beatMs, speed, token);

                // Wait for metronome task to complete for this measure
                //await metronomeTask;
            }
        }

        class NotesComparerByEnd : IComparer<Note>
        {
            public int Compare(Note? x, Note? y)
            {
                return (x.ActualStart + x.ActualDuration).CompareTo(y.ActualStart + y.ActualDuration);
            }

            public readonly static NotesComparerByEnd Instance = new();
        }

        private async Task PlayMeasureNotes(Measure measure, double beatMs, double speed, CancellationToken token)
        {
            if (_midiOut == null) return;

            // Group notes into time slots (chord members share the same onset)
            // Notes are stored sequentially; chord members have IsChordMember = true
            var slots = new List<List<Note>>();
            double currentOffset = 0;
            List<Note> notesOff = [];
            foreach (var n in measure.Notes)
            {
                token.ThrowIfCancellationRequested();

                int velocity = (int)(Volume / 100.0 * 100 + 27); // map 0-100 → 27-127
                velocity = Math.Clamp(velocity, 0, 127);

                var nStart = n.ActualStart;
                while (notesOff.Count > 0 && notesOff[0].ActualStart + notesOff[0].ActualDuration < nStart)
                {
                    var noteOff = notesOff[0];
                    await WaitUntil(noteOff.ActualStart + noteOff.ActualDuration - currentOffset);

                    currentOffset = noteOff.ActualStart + noteOff.ActualDuration;

                    // Note off
                    if (noteOff.MidiPitch >= 21 && noteOff.MidiPitch <= 108)
                        _midiOut.Send(MidiMessage.StopNote(noteOff.MidiPitch, 0, MidiChannel).RawData);

                    notesOff.RemoveAt(0);
                }

                await WaitUntil(n.ActualStart - currentOffset);

                // Note on
                if (n.MidiPitch >= 21 && n.MidiPitch <= 108)
                {
                    _midiOut.Send(MidiMessage.StartNote(n.MidiPitch, velocity, MidiChannel).RawData);
                    var idx = notesOff.BinarySearch(n, NotesComparerByEnd.Instance);
                    if (idx < 0)
                    {
                        notesOff.Insert(-idx - 1, n);
                    }
                }

                currentOffset = n.ActualStart;
            }

            while (notesOff.Count > 0)
            {
                var noteOff = notesOff[0];
                await WaitUntil(noteOff.ActualStart + noteOff.ActualDuration - currentOffset);

                currentOffset = noteOff.ActualStart + noteOff.ActualDuration;

                // Note off
                if (noteOff.MidiPitch >= 21 && noteOff.MidiPitch <= 108)
                    _midiOut.Send(MidiMessage.StopNote(noteOff.MidiPitch, 0, MidiChannel).RawData);

                notesOff.RemoveAt(0);
            }

            double DurationToMs(double duration)
            {
                return duration * beatMs;
            }

            async Task WaitUntil(double delay)
            {
                var delayMs = (int)DurationToMs(delay);
                if (delayMs > 50)
                {
                    await Task.Delay(delayMs, token);
                }
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
