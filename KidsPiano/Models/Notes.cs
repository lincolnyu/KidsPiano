namespace KidsPiano.Models
{
    public class Note
    {
        public int MidiPitch { get; set; }           // e.g. 60 = Middle C
        public string Step { get; set; } = string.Empty;   // C, D, E...
        public int Octave { get; set; }
        public double Duration { get; set; }         // in beats (quarter note = 1.0)
        public bool IsChordMember { get; set; }      // true if part of a chord

        public override string ToString() => $"Note {MidiPitch} ({Step}{Octave})";
    }
}