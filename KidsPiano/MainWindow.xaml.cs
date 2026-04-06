using System.IO;
using System.Windows;
using System.Windows.Controls;
using KidsPiano.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace KidsPiano;

public partial class MainWindow : Window
{
    private readonly MusicXmlParserService _parser = new();
    private double _currentSpeed = 1.0;
    private bool _isPlaying;
    private string _lastFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // Initialize WebView2
        await webViewScore.EnsureCoreWebView2Async(null);
        // TODO: Later we will load OpenSheetMusicDisplay here
        webViewScore.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
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

    private void LoadMusicFile(string filePath)
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

        // TODO: Later we will send this Piece to WebView2 for rendering
        MessageBox.Show($"Successfully loaded!\nTitle: {piece.Title}\nMeasures: {piece.TotalMeasures}",
            "Kids Piano 🎹", MessageBoxButton.OK, MessageBoxImage.Information);
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
        // TODO: Reset to beginning
        MessageBox.Show("Restarting from the beginning! 🎵", "Kids Piano");
    }

    private void btnNext_Click(object sender, RoutedEventArgs e)
    {
        /* TODO */
    }

    private void btnPrev_Click(object sender, RoutedEventArgs e)
    {
        /* TODO */
    }

    private void CoreWebView2_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // Will handle JS → C# messages for note detection feedback later
    }
}