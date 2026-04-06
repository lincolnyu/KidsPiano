using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using KidsPiano.Models;

namespace KidsPiano.Services;

public class KeyboardVisualizerService
{
    private readonly Dictionary<int, Rectangle> _blackKeys = new();
    private readonly Canvas _canvas;
    private readonly List<Ellipse> _playedCircles = new();

    private readonly Dictionary<int, Rectangle> _whiteKeys = new();
    private readonly KeyboardState _state = new();

    public KeyboardVisualizerService(Canvas canvas)
    {
        _canvas = canvas;
    }

    public void InitializeKeyboard()
    {
        _canvas.Children.Clear();
        _whiteKeys.Clear();
        _blackKeys.Clear();
        DrawFullWidthKeyboard();
    }

    private void DrawFullWidthKeyboard()
    {
        // Make canvas fill the available space
        _canvas.Width = double.NaN; // Stretch
        _canvas.Height = double.NaN;

        // Calculate how many white keys to show
        var visibleWhiteKeys = GetVisibleWhiteKeys();

        var whiteKeyWidth = _canvas.ActualWidth / visibleWhiteKeys;
        if (whiteKeyWidth < 20) whiteKeyWidth = 20; // minimum size

        var whiteKeyHeight = _canvas.ActualHeight - 10;
        var blackKeyWidth = whiteKeyWidth * 0.65;
        var blackKeyHeight = whiteKeyHeight * 0.68;

        _canvas.Children.Clear();

        var startMidi = _state.StartOctave * 12;
        double x = 0;

        for (var i = 0; i < visibleWhiteKeys; i++)
        {
            var currentMidi = startMidi + i;

            // White key
            var whiteKey = new Rectangle
            {
                Width = whiteKeyWidth - 1, // small gap for separation
                Height = whiteKeyHeight,
                Fill = Brushes.White,
                Stroke = Brushes.Black,
                StrokeThickness = 3,
                RadiusX = 6,
                RadiusY = 6
            };

            Canvas.SetLeft(whiteKey, x);
            Canvas.SetTop(whiteKey, 5);
            _canvas.Children.Add(whiteKey);
            _whiteKeys[currentMidi] = whiteKey;

            // Black keys (between white keys)
            if (ShouldDrawBlackKey(i))
            {
                var blackKey = new Rectangle
                {
                    Width = blackKeyWidth,
                    Height = blackKeyHeight,
                    Fill = Brushes.Black,
                    Stroke = Brushes.Gray,
                    StrokeThickness = 2,
                    RadiusX = 4,
                    RadiusY = 4
                };

                Canvas.SetLeft(blackKey, x + whiteKeyWidth * 0.68);
                Canvas.SetTop(blackKey, 5);
                _canvas.Children.Add(blackKey);
                _blackKeys[currentMidi + 1] = blackKey; // black note is +1 semitone
            }

            x += whiteKeyWidth;
        }
    }

    private int GetVisibleWhiteKeys()
    {
        if (_state.ZoomLevel == 88) return 52; // full keyboard approx
        return _state.ZoomLevel * 7; // 7 white keys per octave
    }

    private bool ShouldDrawBlackKey(int whiteIndex)
    {
        var pos = whiteIndex % 7;
        return pos == 0 || pos == 1 || pos == 3 || pos == 4 || pos == 5; // C# D# F# G# A#
    }

    public void SetZoom(int zoomLevel)
    {
        _state.ZoomLevel = zoomLevel == 88 ? 88 : Math.Clamp(zoomLevel, 1, 4);
        InitializeKeyboard();
    }

    // Temporary method - we'll connect real expected notes soon
    public void TestExpectedNotes()
    {
        var testNotes = new List<int> { 60, 64, 67 }; // Middle C, E, G
        UpdateExpectedNotes(testNotes);
    }

    public void UpdateExpectedNotes(IEnumerable<int> expectedPitches)
    {
        _state.ExpectedNotes.Clear();
        foreach (var p in expectedPitches)
            _state.ExpectedNotes.Add(p);

        HighlightExpectedKeys();
    }

    private void HighlightExpectedKeys()
    {
        foreach (var kvp in _whiteKeys)
            kvp.Value.Fill = _state.ExpectedNotes.Contains(kvp.Key)
                ? new SolidColorBrush(Color.FromRgb(144, 238, 144)) // light green
                : Brushes.White;

        foreach (var kvp in _blackKeys)
            kvp.Value.Fill = _state.ExpectedNotes.Contains(kvp.Key)
                ? new SolidColorBrush(Color.FromRgb(0, 200, 0))
                : Brushes.Black;
    }

    public void UpdatePlayedNotes(Dictionary<int, string> played)
    {
        // Clear old circles first
        foreach (var circle in _playedCircles.ToList())   // ToList to avoid modification during enumeration
            _canvas.Children.Remove(circle);
        _playedCircles.Clear();

        if (played == null || played.Count == 0) return;

        foreach (var kvp in played)
        {
            int pitch = kvp.Key;
            string colorName = kvp.Value;

            Brush fill = colorName switch
            {
                "red" => Brushes.Red,
                "orange" => Brushes.Orange,
                _ => Brushes.DarkGreen
            };

            var circle = new Ellipse
            {
                Width = 32,
                Height = 32,
                Fill = fill,
                Stroke = Brushes.White,
                StrokeThickness = 5
            };

            if (_whiteKeys.TryGetValue(pitch, out var white) && white != null)
            {
                Canvas.SetLeft(circle, Canvas.GetLeft(white) + white.Width / 2 - 16);
                Canvas.SetTop(circle, white.Height - 55);
            }
            else if (_blackKeys.TryGetValue(pitch, out var black) && black != null)
            {
                Canvas.SetLeft(circle, Canvas.GetLeft(black) + black.Width / 2 - 16);
                Canvas.SetTop(circle, 35);
            }
            else
            {
                continue; // skip if key not visible in current zoom
            }

            _canvas.Children.Add(circle);
            _playedCircles.Add(circle);
        }
    }
}