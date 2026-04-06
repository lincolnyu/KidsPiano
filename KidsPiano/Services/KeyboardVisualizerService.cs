using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using KidsPiano.Models;

namespace KidsPiano.Services;

/// <summary>
/// Draws an 88-key piano keyboard onto a WPF Canvas and maintains
/// expected (light-green) and played (colored circles) note overlays.
/// </summary>
public class KeyboardVisualizerService
{
    // ── Internals ─────────────────────────────────────────────────────────────
    private readonly Canvas     _canvas;
    private readonly KeyboardState _state = new();

    // key rectangles keyed by MIDI pitch
    private readonly Dictionary<int, Rectangle> _whiteKeys = new();
    private readonly Dictionary<int, Rectangle> _blackKeys = new();
    private readonly List<Ellipse>              _playedCircles = new();

    // MIDI pitch of the first key drawn (C of _state.StartOctave)
    private int _startMidiPitch;

    public KeyboardVisualizerService(Canvas canvas) => _canvas = canvas;

    // ── Init ──────────────────────────────────────────────────────────────────
    public void InitializeKeyboard()
    {
        _canvas.Children.Clear();
        _whiteKeys.Clear();
        _blackKeys.Clear();
        _playedCircles.Clear();
        _canvas.UpdateLayout();
        DrawKeyboard();
    }

    // ── Drawing ───────────────────────────────────────────────────────────────

    private void DrawKeyboard()
    {
        double w = _canvas.ActualWidth;
        double h = _canvas.ActualHeight;
        if (w < 10 || h < 10) { _canvas.Dispatcher.InvokeAsync(DrawKeyboard); return; }

        int totalWhiteKeys = GetVisibleWhiteKeyCount();
        double wkW = w / totalWhiteKeys;        // white-key width
        double wkH = h - 8;                      // white-key height
        double bkW = wkW * 0.60;
        double bkH = wkH * 0.62;

        // Start at C of the start octave
        _startMidiPitch = _state.StartOctave * 12; // MIDI for C of that octave

        // The 88 piano keys start at A0 = MIDI 21 (C-1 = 0, so A0 = 21)
        // We need to map from our start octave C correctly.
        // Standard: MIDI 21 = A0, MIDI 60 = C4, MIDI 108 = C8
        // Octave in MIDI: octave n starts at MIDI (n+1)*12
        // So C4 = (4+1)*12 = 60 ✓

        double x = 0;
        int whiteKeyNum = 0;

        for (int midi = _startMidiPitch; whiteKeyNum < totalWhiteKeys; midi++)
        {
            if (midi > 108) break;

            bool isBlack = IsBlackKey(midi);
            if (isBlack) continue;   // handled after the white key below it

            // White key
            var wk = new Rectangle
            {
                Width  = wkW - 1.5,
                Height = wkH,
                Fill   = _state.ExpectedNotes.Contains(midi)
                         ? new SolidColorBrush(Color.FromRgb(144, 238, 144))
                         : Brushes.White,
                Stroke          = Brushes.DimGray,
                StrokeThickness = 1.5,
                RadiusX = 5, RadiusY = 5
            };
            Canvas.SetLeft(wk, x);
            Canvas.SetTop(wk, 4);
            _canvas.Children.Add(wk);
            _whiteKeys[midi] = wk;

            // Black key to the RIGHT of this white key (if applicable)
            int blackMidi = midi + 1;
            if (IsBlackKey(blackMidi) && blackMidi <= 108)
            {
                var bk = new Rectangle
                {
                    Width  = bkW,
                    Height = bkH,
                    Fill   = _state.ExpectedNotes.Contains(blackMidi)
                             ? new SolidColorBrush(Color.FromRgb(0, 180, 0))
                             : Brushes.Black,
                    Stroke          = Brushes.Gray,
                    StrokeThickness = 1,
                    RadiusX = 3, RadiusY = 3
                };
                Canvas.SetLeft(bk, x + wkW * 0.62);
                Canvas.SetTop(bk, 4);
                Panel.SetZIndex(bk, 1);
                _canvas.Children.Add(bk);
                _blackKeys[blackMidi] = bk;
            }

            x += wkW;
            whiteKeyNum++;
        }
    }

    // ── Key type helpers ──────────────────────────────────────────────────────
    // Within an octave (0=C..11=B): black keys are 1,3,6,8,10 (C#,D#,F#,G#,A#)
    private static bool IsBlackKey(int midi)
    {
        int pos = ((midi % 12) + 12) % 12;
        return pos == 1 || pos == 3 || pos == 6 || pos == 8 || pos == 10;
    }

    private int GetVisibleWhiteKeyCount()
    {
        return _state.ZoomLevel == 88
            ? 52 // full 88-key keyboard has 52 white keys
            : _state.ZoomLevel * 7; // 7 white keys per octave
    }

    // ── Zoom ──────────────────────────────────────────────────────────────────
    public void SetZoom(int zoomLevel)
    {
        _state.ZoomLevel = zoomLevel == 88 ? 88 : Math.Clamp(zoomLevel, 1, 4);
        InitializeKeyboard();
    }

    // ── Auto-center ───────────────────────────────────────────────────────────
    /// <summary>
    /// Shifts the keyboard view so the given notes are visible and centered.
    /// Always snaps to the start of an octave (C).
    /// </summary>
    public void CenterOnNotes(IEnumerable<int> pitches)
    {
        if (_state.ZoomLevel == 88) return; // no panning in full view

        var list = pitches.Where(p => p >= 21 && p <= 108).ToList();
        if (list.Count == 0) return;

        int lowestMidi  = list.Min();
        int highestMidi = list.Max();

        // Octave of the lowest note
        int lowestOctave  = lowestMidi / 12;
        int highestOctave = highestMidi / 12;

        // Pick the octave that centers the range under the current zoom window
        int halfZoom    = _state.ZoomLevel / 2;
        int targetStart = Math.Max(0, lowestOctave - halfZoom + (highestOctave - lowestOctave) / 2);

        // Clamp so we don't go past the keyboard edges
        int maxStart = 9 - _state.ZoomLevel; // C9 is beyond piano, so 9 octaves
        targetStart = Math.Clamp(targetStart, 0, Math.Max(0, maxStart));

        if (targetStart == _state.StartOctave) return;
        _state.StartOctave = targetStart;
        InitializeKeyboard();
    }

    // ── Expected notes ────────────────────────────────────────────────────────
    public void UpdateExpectedNotes(IEnumerable<int> expectedPitches)
    {
        _state.ExpectedNotes.Clear();
        foreach (var p in expectedPitches)
            _state.ExpectedNotes.Add(p);
        RefreshKeyColors();
    }

    private void RefreshKeyColors()
    {
        foreach (var kvp in _whiteKeys)
            kvp.Value.Fill = _state.ExpectedNotes.Contains(kvp.Key)
                ? new SolidColorBrush(Color.FromRgb(144, 238, 144))
                : Brushes.White;

        foreach (var kvp in _blackKeys)
            kvp.Value.Fill = _state.ExpectedNotes.Contains(kvp.Key)
                ? new SolidColorBrush(Color.FromRgb(0, 200, 0))
                : Brushes.Black;
    }

    // ── Played notes (circles) ────────────────────────────────────────────────
    public void UpdatePlayedNotes(Dictionary<int, string> played)
    {
        foreach (var c in _playedCircles) _canvas.Children.Remove(c);
        _playedCircles.Clear();

        if (played == null || played.Count == 0) return;

        foreach (var kvp in played)
        {
            int pitch = kvp.Key;
            Brush fill = kvp.Value switch
            {
                "red"       => Brushes.Red,
                "orange"    => Brushes.Orange,
                "darkgreen" => new SolidColorBrush(Color.FromRgb(0, 140, 0)),
                _           => new SolidColorBrush(Color.FromRgb(0, 140, 0))
            };

            double cx, cy, cSize = 28;

            if (_whiteKeys.TryGetValue(pitch, out var wk))
            {
                cx = Canvas.GetLeft(wk) + wk.Width / 2 - cSize / 2;
                cy = 4 + wk.Height - cSize - 10;
            }
            else if (_blackKeys.TryGetValue(pitch, out var bk))
            {
                cx = Canvas.GetLeft(bk) + bk.Width / 2 - cSize / 2;
                cy = 4 + bk.Height - cSize - 6;
            }
            else continue; // not visible in current zoom

            var circle = new Ellipse
            {
                Width  = cSize,
                Height = cSize,
                Fill   = fill,
                Stroke = Brushes.White,
                StrokeThickness = 3
            };
            Panel.SetZIndex(circle, 2);
            Canvas.SetLeft(circle, cx);
            Canvas.SetTop(circle, cy);
            _canvas.Children.Add(circle);
            _playedCircles.Add(circle);
        }
    }

    // Legacy test helper
    public void TestExpectedNotes() => UpdateExpectedNotes(new[] { 60, 64, 67 });
}
