using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KidsPiano.Models;
using KidsPiano.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace KidsPiano;

public partial class MainWindow : Window
{
    private readonly KeyboardVisualizerService _keyboardService;

    // ── Services ───────────────────────────────────────────────────────────────
    private readonly MusicXmlParserService _parser = new();
    private readonly PitchDetectorService _pitchDetector;
    private readonly PlaybackService _playback;

    private readonly Dictionary<(int Midi, double Start), byte> _soundingNotes = new();
    private readonly TrackingService _tracking = new();
    private int _currentMeasureIndex;

    // ── State ──────────────────────────────────────────────────────────────────
    private string _currentMusicXmlContent = string.Empty;
    private Piece? _currentPiece;
    private double _currentSpeed = 1.0;
    private bool _isPlaying;
    private string _lastFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    private int _onsetIndex; // index into distinct ActualStart values of current measure
    private bool _webViewReady;


    public MainWindow()
    {
        InitializeComponent();

        _keyboardService = new KeyboardVisualizerService(canvasKeyboard);
        _pitchDetector = new PitchDetectorService(OnNotesDetected);
        _playback = new PlaybackService();

        // Playback events
        _playback.OnMeasureStarted += OnPlaybackMeasureStarted;
        _playback.OnPlaybackFinished += OnPlaybackFinished;
        _playback.OnNoteChanged += OnPlaybackNoteChange;

        // Tracking events
        _tracking.OnChordAccepted += OnChordAccepted;
        _tracking.OnRepeatSegment += OnRepeatSegment;
        _tracking.OnNoteColorsChanged += OnTrackingNoteColorsChanged;

        Loaded += MainWindow_Loaded;
        Closing += (_, __) =>
        {
            _pitchDetector.Dispose();
            _playback.Dispose();
        };
    }

    // Whether we are in player-led mode (false = app-led)
    private bool IsPlayerLed => cmbMode.SelectedIndex == 1;


    private List<double> CurrentOnsetStarts()
    {
        if (_currentPiece == null || _currentPiece.Measures.Count == 0)
            return new List<double>();
        var measure = _currentPiece.Measures[_currentMeasureIndex];
        return measure.Notes.Select(n => n.ActualStart).Distinct().OrderBy(x => x).ToList();
    }

    private List<Note> CurrentExpectedOnset()
    {
        if (_currentPiece == null || _currentPiece.Measures.Count == 0)
            return new List<Note>();
        var measure = _currentPiece.Measures[_currentMeasureIndex];
        var starts = CurrentOnsetStarts();
        if (starts.Count == 0) return new List<Note>();
        _onsetIndex = Math.Clamp(_onsetIndex, 0, starts.Count - 1);
        return OnsetGroup(measure, starts[_onsetIndex]);
    }

    private List<Note> OnsetGroup(Measure m, double start)
    {
        return m.Notes.Where(n => Math.Abs(n.ActualStart - start) < 0.02).ToList();
    }

    // ── Init ───────────────────────────────────────────────────────────────────
    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await webViewScore.EnsureCoreWebView2Async(null);
        webViewScore.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
        _keyboardService.InitializeKeyboard();
        cmbZoom.SelectedIndex = 2; // 3 octaves default
        UpdateSpeedButtonHighlights();
    }

    private void CoreWebView2_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var json = JsonDocument.Parse(e.TryGetWebMessageAsString());
            if (json.RootElement.TryGetProperty("type", out var t) && t.GetString() == "ready")
                _webViewReady = true;
        }
        catch
        {
        }
    }

    // ── File open ──────────────────────────────────────────────────────────────
    private void btnOpen_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "MusicXML files (*.musicxml;*.xml)|*.musicxml;*.xml|MIDI files (*.mid)|*.mid|All files (*.*)|*.*",
            InitialDirectory = _lastFolder
        };

        if (dlg.ShowDialog() == true)
        {
            _lastFolder = Path.GetDirectoryName(dlg.FileName) ?? _lastFolder;
            txtSongName.Text = "Loading...";
            LoadMusicFile(dlg.FileName);
        }
    }

    private async void LoadMusicFile(string filePath)
    {
        var (piece, warning) = _parser.Parse(filePath);

        if (!string.IsNullOrEmpty(warning))
        {
            if (warning.StartsWith("Invalid"))
                MessageBox.Show(warning, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            else
                MessageBox.Show(warning, "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        txtSongName.Text = piece.Title;
        _currentPiece = piece;
        _currentMeasureIndex = 0;
        _onsetIndex = 0;
        _currentMusicXmlContent = File.ReadAllText(filePath);

        // Load into OSMD
        if (webViewScore.CoreWebView2 != null)
            await LoadScoreIntoWebView();
        else
            webViewScore.CoreWebView2InitializationCompleted += async (s, ev) =>
            {
                if (ev.IsSuccess) await LoadScoreIntoWebView();
            };

        RefreshCurrentMeasure();
    }

    private async Task LoadScoreIntoWebView()
    {
        if (string.IsNullOrEmpty(_currentMusicXmlContent)) return;

        var htmlPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot", "index.html");
        if (!File.Exists(htmlPath))
        {
            MessageBox.Show("wwwroot/index.html not found.", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        webViewScore.Source = new Uri("file:///" + htmlPath.Replace("\\", "/"));
        await Task.Delay(1500); // Wait for OSMD JS to load

        // Escape the XML so it can be passed as a JS template literal
        var escaped = _currentMusicXmlContent
            .Replace("\\", "\\\\")
            .Replace("`", "\\`")
            .Replace("$", "\\$");

        await webViewScore.CoreWebView2.ExecuteScriptAsync($"loadScore(`{escaped}`)");
    }

    // ── Measure navigation helpers ─────────────────────────────────────────────
    private void RefreshCurrentMeasure()
    {
        if (_currentPiece == null || _currentPiece.Measures.Count == 0) return;
        if (_currentMeasureIndex >= _currentPiece.Measures.Count) _currentMeasureIndex = 0;

        _ = HighlightMeasureInScore(_currentMeasureIndex);

        if (!IsPlayerLed) return;

        var expectedOnset = CurrentExpectedOnset();
        _tracking.SetExpectedPitches(expectedOnset.Select(n => n.MidiPitch));
        _keyboardService.UpdateExpectedNotes(set =>
        {
            set.Clear();
            foreach (var n in expectedOnset) set.Add(n.MidiPitch);
        });
        _ = PushExpectedHighlights(expectedOnset, "blue"); // waiting for the child
    }

    private async Task HighlightMeasureInScore(int measureIndex)
    {
        if (webViewScore.CoreWebView2 == null) return;
        try
        {
            await webViewScore.CoreWebView2.ExecuteScriptAsync(
                $"highlightMeasure({measureIndex})");
        }
        catch
        {
        }
    }

    private void PushSoundingHighlights()
    {
        var payload = _soundingNotes.Keys
            .Select(k => new { midi = k.Midi, start = k.Start })
            .ToList();
        _ = SetSoundingNotesInScore(_currentMeasureIndex, payload);
    }

    private async Task SetSoundingNotesInScore(int measureIndex, object payload)
    {
        if (webViewScore.CoreWebView2 == null) return;
        try
        {
            var json = JsonSerializer.Serialize(payload);
            await webViewScore.CoreWebView2.ExecuteScriptAsync(
                $"setSoundingNotes({measureIndex}, {json})");
        }
        catch
        {
        }
    }

    private void ClearSoundingNotes()
    {
        _soundingNotes.Clear();
        _ = SetSoundingNotesInScore(_currentMeasureIndex, Array.Empty<object>());
    }

    // ── Pitch detection callback ───────────────────────────────────────────────
    private void OnNotesDetected(List<int> detectedPitches)
    {
        Dispatcher.Invoke(() =>
        {
            if (!IsPlayerLed || !_isPlaying)
            {
                // In app-led mode or paused: just visualise what's being played
                var playedColors = detectedPitches.ToDictionary(p => p, _ => "green");
                _keyboardService.UpdatePlayedNotes(playedColors);
                return;
            }

            // Player-led mode: run tracking logic
            var colors = _tracking.ProcessDetectedNotes(detectedPitches);
            _keyboardService.UpdatePlayedNotes(colors);
        });
    }

    // ── Tracking callbacks ─────────────────────────────────────────────────────
    private void OnChordAccepted()
    {
        // Advance to next note/chord within the measure, or next measure
        // For v1: advance one whole measure at a time (simplification)
        Dispatcher.Invoke(() =>
        {
            var starts = CurrentOnsetStarts();
            if (_onsetIndex + 1 < starts.Count)
            {
                _onsetIndex++;
                RefreshCurrentMeasure();
            }
            else
            {
                AdvanceToNextMeasure();
            }
        });
    }

    private void OnRepeatSegment()
    {
        Dispatcher.Invoke(() =>
        {
            _onsetIndex = 0;
            RefreshCurrentMeasure();
        });
    }

    private void OnTrackingNoteColorsChanged(Dictionary<int, string> playedColors)
    {
        if (_currentPiece == null || !IsPlayerLed) return;

        var expectedOnset = CurrentExpectedOnset();
        var expected = expectedOnset.Select(n => n.MidiPitch).ToList();
        var played = playedColors.Keys.ToList();
        var scoreMap = TrackingService.BuildScoreColorMap(played, expected, _tracking.Tolerance);

        var payload = expectedOnset.Select(n => new
        {
            midi = n.MidiPitch,
            start = n.ActualStart,
            status = scoreMap.GetValueOrDefault(n.MidiPitch.ToString(), "blue")
        });

        Dispatcher.Invoke(async () =>
        {
            if (webViewScore.CoreWebView2 == null) return;
            await webViewScore.CoreWebView2.ExecuteScriptAsync(
                $"updateNoteColors({_currentMeasureIndex}, {JsonSerializer.Serialize(payload)})");
        });
    }

    private async Task PushExpectedHighlights(IEnumerable<Note> expectedOnset, string status)
    {
        if (webViewScore.CoreWebView2 == null) return;
        var payload = expectedOnset.Select(n => new { midi = n.MidiPitch, start = n.ActualStart, status });
        await webViewScore.CoreWebView2.ExecuteScriptAsync(
            $"updateNoteColors({_currentMeasureIndex}, {JsonSerializer.Serialize(payload)})");
    }


    private void AdvanceToNextMeasure()
    {
        if (_currentPiece == null) return;
        _onsetIndex = 0;
        if (_currentMeasureIndex < _currentPiece.Measures.Count - 1)
        {
            _currentMeasureIndex++;
            Dispatcher.Invoke(RefreshCurrentMeasure);
        }
        else
        {
            // End of piece
            _isPlaying = false;
            Dispatcher.Invoke(() => btnPlayPause.Content = "▶ Play");
        }
    }

    // ── Playback callbacks ─────────────────────────────────────────────────────
    private void OnPlaybackMeasureStarted(int measureIndex)
    {
        Dispatcher.Invoke(() =>
        {
            _onsetIndex = 0;
            _currentMeasureIndex = measureIndex;
            ClearSoundingNotes();
            RefreshCurrentMeasure();

            // Although it's a good practice to clear the expectedKeys here, we leave it to note off.
        });
    }

    private void OnPlaybackNoteChange(int midiPitch, bool on, double actualStart)
    {
        Dispatcher.Invoke(() =>
        {
            var key = (midiPitch, Math.Round(actualStart, 4));
            if (on) _soundingNotes[key] = 1;
            else _soundingNotes.Remove(key);

            _keyboardService.UpdateExpectedNotes(expectedNotes =>
            {
                if (on) expectedNotes.Add(midiPitch);
                else expectedNotes.Remove(midiPitch);

                if (expectedNotes.Count > 0)
                    _keyboardService.CenterOnNotes(expectedNotes);
            });

            PushSoundingHighlights();
        });
    }


    private void OnPlaybackFinished()
    {
        Dispatcher.Invoke(() =>
        {
            _isPlaying = false;
            btnPlayPause.Content = "▶ Play";
            ClearSoundingNotes();

            _keyboardService.UpdateExpectedNotes(expectedNotes => { expectedNotes.Clear(); });
        });
    }

    // ── UI event handlers ──────────────────────────────────────────────────────

    private void cmbZoom_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (cmbZoom.SelectedItem is ComboBoxItem item &&
            int.TryParse(item.Tag?.ToString(), out var zoom))
            _keyboardService?.SetZoom(zoom);
    }

    private void SpeedButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && double.TryParse(btn.Tag?.ToString(), out var speed))
        {
            _currentSpeed = speed;
            UpdateSpeedButtonHighlights(); // ← This makes it highlight
            if (!IsPlayerLed && _isPlaying) _playback.SetSpeed(speed);
        }
    }

    // Add this helper method
    private void UpdateSpeedButtonHighlights()
    {
        // Reset all to default (kid-friendly light blue / white-ish)
        btnSpeed01.Background = new SolidColorBrush(Color.FromRgb(100, 180, 255));
        btnSpeed025.Background = new SolidColorBrush(Color.FromRgb(100, 180, 255));
        btnSpeed05.Background = new SolidColorBrush(Color.FromRgb(100, 180, 255));
        btnSpeed1x.Background = new SolidColorBrush(Color.FromRgb(100, 180, 255));
        // Highlight the active one in nice green
        if (Math.Abs(_currentSpeed - 0.1) < 0.01)
            btnSpeed01.Background = new SolidColorBrush(Color.FromRgb(76, 175, 80));
        else if (Math.Abs(_currentSpeed - 0.25) < 0.01)
            btnSpeed025.Background = new SolidColorBrush(Color.FromRgb(76, 175, 80));
        else if (Math.Abs(_currentSpeed - 0.5) < 0.01)
            btnSpeed05.Background = new SolidColorBrush(Color.FromRgb(76, 175, 80));
        else if (Math.Abs(_currentSpeed - 1.0) < 0.01)
            btnSpeed1x.Background = new SolidColorBrush(Color.FromRgb(76, 175, 80));
    }


    private void btnPlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPiece == null) return;

        _isPlaying = !_isPlaying;
        btnPlayPause.Content = _isPlaying ? "⏸ Pause" : "▶ Play";

        if (IsPlayerLed)
        {
            // Player-led: pause/resume tracking
            if (_isPlaying)
            {
                RefreshCurrentMeasure(); // sets expected onset + blue heads
                _tracking.Resume();
            }
            else
            {
                _tracking.Pause();
            }
        }
        else
        {
            // App-led: play/pause MIDI playback
            if (_isPlaying)
                _playback.Play(_currentPiece, _currentMeasureIndex, _currentSpeed);
            else
                _playback.Stop();
        }
    }

    private void btnRestart_Click(object sender, RoutedEventArgs e)
    {
        _isPlaying = false;
        btnPlayPause.Content = "▶ Play";
        _playback.Stop();

        _onsetIndex = 0;
        _currentMeasureIndex = 0;
        ClearSoundingNotes();
        RefreshCurrentMeasure();
    }

    private void btnNext_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPiece == null || _currentMeasureIndex >= _currentPiece.Measures.Count - 1) return;
        _onsetIndex = 0;
        _currentMeasureIndex++;
        ClearSoundingNotes();
        RefreshCurrentMeasure();
    }

    private void btnPrev_Click(object sender, RoutedEventArgs e)
    {
        if (_currentMeasureIndex <= 0) return;
        _onsetIndex = 0;
        _currentMeasureIndex--;
        ClearSoundingNotes();
        RefreshCurrentMeasure();
    }

    private void btnMicToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_pitchDetector.IsListening)
        {
            _pitchDetector.StopListening();
            ((Button)sender).Content = "🎤 Start Mic";
        }
        else
        {
            _pitchDetector.StartListening();
            ((Button)sender).Content = "⏹ Stop Mic";
        }
    }

    // Tolerance dropdown
    private void cmbTolerance_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _tracking.Tolerance = cmbTolerance.SelectedIndex switch
        {
            1 => TrackingService.ToleranceLevel.Medium,
            2 => TrackingService.ToleranceLevel.Easy,
            _ => TrackingService.ToleranceLevel.Strict
        };
    }

    // Wrong notes dropdown
    private void cmbWrongHandling_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _tracking.WrongMode = cmbWrongHandling.SelectedIndex == 1
            ? TrackingService.WrongNotesMode.RepeatSegment
            : TrackingService.WrongNotesMode.UntilCorrected;
    }

    // Volume slider
    private void sldVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_playback != null)
            _playback.Volume = (int)sldVolume.Value;
    }

    // Metronome slider
    private void sldMetronome_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_playback != null)
            _playback.MetronomeVolume = (int)sldMetronome.Value;
    }

    private void cmbMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Stop any active playback/tracking when mode changes
        if (_isPlaying)
        {
            _isPlaying = false;
            btnPlayPause.Content = "▶";
            _playback.Stop();
            _tracking.Pause();
        }
        if (_isPlaying)
        {
            _isPlaying = false;
            btnPlayPause.Content = "▶ Play";
            _playback.Stop();
            _tracking.Pause();
        }

        _onsetIndex = 0;
        if (_currentPiece != null)
            RefreshCurrentMeasure();
    }
}