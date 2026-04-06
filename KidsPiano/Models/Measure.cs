namespace KidsPiano.Models;

public class Measure
{
    public int Number { get; set; }
    public List<Note> Notes { get; set; } = new();

    // For future: time signature, tempo, etc.
    public int BeatsPerMeasure { get; set; } = 4;
    public int BeatType { get; set; } = 4;

    public bool IsEmpty => Notes.Count == 0;
}