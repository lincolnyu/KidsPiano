using System.Collections.Generic;

namespace KidsPiano.Models
{
    public class Piece
    {
        public string Title { get; set; } = "Untitled piece";
        public List<Measure> Measures { get; set; } = new List<Measure>();

        public int TotalMeasures => Measures.Count;

        // Helper for current position
        public Measure? GetMeasure(int measureNumber)
        {
            return Measures.FirstOrDefault(m => m.Number == measureNumber);
        }
    }
}