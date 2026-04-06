using System.IO;
using System.Windows;
using System.Windows.Controls;
using KidsPiano.Models;
using KidsPiano.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace KidsPiano;

public partial class MainWindow : Window
{
    private readonly MusicXmlParserService _parser = new();
    private string _currentMusicXmlContent = string.Empty;
    private Piece? _currentPiece;
    private double _currentSpeed = 1.0;
    private bool _isPlaying;
    private string _lastFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    private readonly KeyboardVisualizerService _keyboardService;
    private int _currentMeasureIndex = 0;   // Start at first measure

    public MainWindow()
    {
        InitializeComponent();
        _keyboardService = new KeyboardVisualizerService(canvasKeyboard);
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // Initialize WebView2
        await webViewScore.EnsureCoreWebView2Async(null);
        // TODO: Later we will load OpenSheetMusicDisplay here
        webViewScore.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;

        _keyboardService.InitializeKeyboard();

        // Default zoom
        cmbZoom.SelectedIndex = 2; // 3 octaves
    }

    private void cmbZoom_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (cmbZoom.SelectedItem is ComboBoxItem item &&
            int.TryParse(item.Tag?.ToString(), out int zoom) && _keyboardService is not null)
        {
            _keyboardService.SetZoom(zoom);
        }
    }

    private void btnOpen_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "MusicXML files (*.musicxml;*.xml)|*.musicxml;*.xml|MIDI files (*.mid)|*.mid|All files (*.*)|*.*",
            InitialDirectory = _lastFolder
        };

        if (dlg.ShowDialog() == true)
        {
            _lastFolder = Path.GetDirectoryName(dlg.FileName);
            txtSongName.Text = "Loading..."; // Will extract real title later
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

        // Read the raw MusicXML content for OSMD
        _currentMusicXmlContent = File.ReadAllText(filePath);

        // Load into WebView2
        if (webViewScore.CoreWebView2 != null)
            await LoadScoreIntoWebView();
        else
            // Wait until WebView2 is ready
            webViewScore.CoreWebView2InitializationCompleted += async (s, e) =>
            {
                if (e.IsSuccess)
                    await LoadScoreIntoWebView();
            };

        // Show expected notes on keyboard for the first measure
        ShowExpectedNotesForCurrentMeasure();
    }

    private void ShowExpectedNotesForCurrentMeasure()
    {
        if (_currentPiece == null || _currentPiece.Measures.Count == 0) return;

        var currentMeasure = _currentPiece.Measures[_currentMeasureIndex];

        var expectedPitches = currentMeasure.Notes.Select(n => n.MidiPitch).ToList();

        _keyboardService.UpdateExpectedNotes(expectedPitches);

        // Optional: Show a small message for testing
        // MessageBox.Show($"Showing measure {_currentMeasureIndex + 1} with {expectedPitches.Count} notes");
    }

    private async Task LoadScoreIntoWebView()
    {
        if (string.IsNullOrEmpty(_currentMusicXmlContent) || _currentPiece == null)
            return;

        string htmlPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot", "index.html");

        if (!System.IO.File.Exists(htmlPath))
        {
            MessageBox.Show("wwwroot/index.html not found.\nPlease make sure the file exists and is set to Copy if newer.",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // Navigate to our local HTML page
        webViewScore.Source = new Uri("file:///" + htmlPath.Replace("\\", "/"));

        // Wait for the page and OSMD to fully initialize
        await Task.Delay(1200);

        // Escape backticks and inject the MusicXML content
        string escapedXml = _currentMusicXmlContent.Replace("`", "\\`").Replace("\\", "\\\\");

        string script = $"loadScore(`{escapedXml}`)";

        string result = await webViewScore.CoreWebView2.ExecuteScriptAsync(script);

        // OSMD loadScore returns true on success, false on failure
        bool success = result?.Trim().Equals("{}", StringComparison.OrdinalIgnoreCase) == true;

        if (success)
        {
            MessageBox.Show($"Score loaded successfully!\n\n" +
                            $"Title: {_currentPiece.Title}\n" +
                            $"Measures: {_currentPiece.TotalMeasures}",
                "Kids Piano 🎹", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show("Failed to render the score.\nCheck the MusicXML file or console (F12).",
                "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // Speed buttons
    private void SpeedButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && double.TryParse(btn.Tag?.ToString(), out var speed)) _currentSpeed = speed;
        // Highlight selected button later
    }

    private void btnPlayPause_Click(object sender, RoutedEventArgs e)
    {
        _isPlaying = !_isPlaying;
        btnPlayPause.Content = _isPlaying ? "⏸ Pause" : "▶ Play";
        // TODO: Start/stop playback or tracking
    }

    private void btnRestart_Click(object sender, RoutedEventArgs e)
    {
        _currentMeasureIndex = 0;
        ShowExpectedNotesForCurrentMeasure();

        // Later we will also reset the score position
        MessageBox.Show("Restarted from the beginning! 🎵", "Kids Piano");
    }

    private void btnNext_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPiece == null || _currentMeasureIndex >= _currentPiece.Measures.Count - 1)
            return;

        _currentMeasureIndex++;
        ShowExpectedNotesForCurrentMeasure();
    }

    private void btnPrev_Click(object sender, RoutedEventArgs e)
    {
        if (_currentMeasureIndex <= 0) return;

        _currentMeasureIndex--;
        ShowExpectedNotesForCurrentMeasure();
    }

    private void CoreWebView2_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // Will handle JS → C# messages for note detection feedback later
    }
}