using NAudio.Wave;
using System;
using System.Collections.Generic;

namespace KidsPiano.Services
{
    public class PitchDetectorService : IDisposable
    {
        private WasapiLoopbackCapture? _capture;
        private readonly Action<List<int>> _onNotesDetected;

        public bool IsListening { get; private set; }

        public PitchDetectorService(Action<List<int>> onNotesDetected)
        {
            _onNotesDetected = onNotesDetected;
        }

        public void StartListening()
        {
            if (IsListening) return;

            _capture = new WasapiLoopbackCapture();
            _capture.DataAvailable += OnDataAvailable;
            _capture.StartRecording();
            IsListening = true;
        }

        public void StopListening()
        {
            if (_capture != null)
            {
                _capture.StopRecording();
                _capture.DataAvailable -= OnDataAvailable;
                _capture.Dispose();
                _capture = null;
            }
            IsListening = false;
        }

        private void OnDataAvailable(object sender, WaveInEventArgs e)
        {
            // Placeholder fake detection for testing the pipeline
            var detectedPitches = new List<int>();

            // Simulate piano notes being played (remove later when real detection is added)
            if (DateTime.Now.Second % 4 == 0)        // every 4 seconds
            {
                detectedPitches.Add(60); // C4
                detectedPitches.Add(64); // E4
                detectedPitches.Add(67); // G4
            }

            // Safely call back to UI thread
            _onNotesDetected(detectedPitches);
        }

        public void Dispose()
        {
            StopListening();
        }
    }
}