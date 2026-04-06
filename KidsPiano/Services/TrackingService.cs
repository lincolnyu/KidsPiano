using System;
using System.Collections.Generic;
using System.Linq;
using KidsPiano.Models;

namespace KidsPiano.Services
{
    /// <summary>
    /// Player-led tracking: given detected MIDI pitches and the expected notes
    /// for the current chord/beat, decides whether the input is accepted and
    /// what colour each key should show.
    /// </summary>
    public class TrackingService
    {
        public enum ToleranceLevel { Strict, Medium, Easy }
        public enum WrongNotesMode  { UntilCorrected, RepeatSegment }

        // ── Config (set from UI) ───────────────────────────────────────────────
        public ToleranceLevel Tolerance    { get; set; } = ToleranceLevel.Strict;
        public WrongNotesMode WrongMode    { get; set; } = WrongNotesMode.UntilCorrected;

        // ── Events ─────────────────────────────────────────────────────────────
        /// Fired when the user plays the current chord correctly → advance
        public event Action? OnChordAccepted;
        /// Fired on wrong notes: Dictionary<midiPitch, "green"|"orange"|"red">
        public event Action<Dictionary<int, string>>? OnNoteColorsChanged;
        /// Fired when RepeatSegment mode triggers a rewind
        public event Action? OnRepeatSegment;

        // ── State ──────────────────────────────────────────────────────────────
        private List<int> _expectedPitches = new();
        private bool      _paused;
        private bool      _chordJustAccepted;

        // ── Public API ─────────────────────────────────────────────────────────

        public void SetExpectedPitches(IEnumerable<int> pitches)
        {
            _expectedPitches = pitches.ToList();
            _chordJustAccepted = false;
        }

        public void Pause()  => _paused = true;
        public void Resume() => _paused = false;

        /// <summary>
        /// Call this every time the pitch detector fires new notes.
        /// Returns keyboard coloring: Dictionary<midiPitch, "green"|"orange"|"red">
        /// for all expected + played notes.
        /// </summary>
        public Dictionary<int, string> ProcessDetectedNotes(List<int> detectedPitches)
        {
            if (_paused || _chordJustAccepted)
                return new Dictionary<int, string>();

            bool accepted   = IsAccepted(detectedPitches, _expectedPitches, Tolerance);
            bool allPerfect = accepted && detectedPitches.All(p => _expectedPitches.Contains(p))
                                       && _expectedPitches.All(p => detectedPitches.Contains(p));

            // Build keyboard color map for played notes
            var playedColors = new Dictionary<int, string>();
            foreach (int p in detectedPitches)
            {
                bool correct = _expectedPitches.Contains(p);
                if (accepted)
                    playedColors[p] = correct ? "darkgreen" : "orange";
                else
                    playedColors[p] = correct ? "darkgreen" : "red";
            }

            // Build score color map for expected notes
            var scoreColors = new Dictionary<int, string>();
            foreach (int p in _expectedPitches)
                scoreColors[p] = allPerfect ? "green" : accepted ? "orange" : "red";

            OnNoteColorsChanged?.Invoke(playedColors);

            if (accepted)
            {
                _chordJustAccepted = true;
                OnChordAccepted?.Invoke();
            }
            else
            {
                if (WrongMode == WrongNotesMode.RepeatSegment && detectedPitches.Count > 0)
                    OnRepeatSegment?.Invoke();
            }

            return playedColors;
        }

        // ── Tolerance logic ────────────────────────────────────────────────────

        public static bool IsAccepted(List<int> played, List<int> expected, ToleranceLevel tol)
        {
            if (expected.Count == 0) return true;
            if (played.Count == 0)   return false;

            // No extra unexpected notes allowed (played must be subset of expected)
            if (played.Any(p => !expected.Contains(p))) return false;

            int correctCount = played.Count(p => expected.Contains(p));

            return tol switch
            {
                ToleranceLevel.Strict => correctCount == expected.Count,
                ToleranceLevel.Medium => expected.Count >= 2
                                         ? correctCount >= 2
                                         : correctCount >= 1,
                ToleranceLevel.Easy   => correctCount >= 1,
                _                     => false
            };
        }

        /// <summary>
        /// Build the score note color map to send to OSMD.
        /// Key = MIDI pitch (as string), value = "green"|"orange"|"red"|"blue"
        /// </summary>
        public static Dictionary<string, string> BuildScoreColorMap(
            List<int> played, List<int> expected, ToleranceLevel tol)
        {
            bool accepted   = IsAccepted(played, expected, tol);
            bool allPerfect = accepted
                && played.Count == expected.Count
                && played.All(expected.Contains);

            var map = new Dictionary<string, string>();
            foreach (int p in expected)
            {
                string color = allPerfect ? "green"
                             : accepted   ? "orange"
                             : "red";
                map[p.ToString()] = color;
            }
            return map;
        }
    }
}
