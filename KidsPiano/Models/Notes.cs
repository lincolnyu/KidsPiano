namespace KidsPiano.Models
{
    public class Note
    {
        public int MidiPitch { get; set; }           // e.g. 60 = Middle C
        public string Step { get; set; } = string.Empty;   // C, D, E...
        public int Octave { get; set; }

        public double Start { get; set; }

        public double ActualStart { get; set; }

        public double ActualDuration => Start + Duration - ActualStart;

        public int? RawDuration { get; set; }

        public double Duration { get; set; }         // in beats

        // TODO Maybe not needed...
        public bool IsChordMember { get; set; }      // true if part of a chord

        public bool IsGrace { get; set; }

        public string Type { get; set; }

        public override string ToString() => $"Note {MidiPitch} ({Step}{Octave})";
    }
}