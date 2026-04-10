namespace KidsPiano.Models;

public class Measure
{
    public int Number { get; set; }
    public List<Note> Notes { get; set; } = new();

    // For future: time signature, tempo, etc.
    public int BeatsPerMeasure { get; set; } = 4;
    public int BeatType { get; set; } = 4;

    public double Tempo { get; set; } = 0; // BPM; 0 = not set (caller fills forward)

    public bool IsEmpty => Notes.Count == 0;
}