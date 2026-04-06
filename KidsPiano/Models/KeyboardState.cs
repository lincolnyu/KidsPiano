namespace KidsPiano.Models
{
    public class KeyboardState
    {
        public int ZoomLevel { get; set; } = 3;           // default 3 octaves
        public int StartOctave { get; set; } = 2;         // starts from C of this octave
        public HashSet<int> ExpectedNotes { get; set; } = new HashSet<int>();   // MIDI pitches
        public Dictionary<int, string> PlayedNotes { get; set; } = new Dictionary<int, string>(); // pitch -> color ("green","orange","red")
    }
}