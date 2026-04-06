using System.Xml.Linq;
using KidsPiano.Models;

namespace KidsPiano.Services;

public class MusicXmlParserService
{
    public (Piece piece, string warning) Parse(string filePath)
    {
        try
        {
            var doc = XDocument.Load(filePath);
            var piece = new Piece();

            // Extract title - priority as per v1.6
            piece.Title = ExtractTitle(doc);

            // Find parts - we take the first piano part (or first part if no piano)
            var part = FindFirstPianoPart(doc) ?? FindFirstPart(doc);

            if (part == null) return (new Piece(), "No valid part found in the MusicXML file.");

            // Parse measures (each measure = one segment)
            var measures = part.Elements("measure");
            var measureNum = 1;

            foreach (var m in measures)
            {
                var measure = new Measure { Number = measureNum };

                // Parse notes in this measure
                foreach (var noteElem in m.Elements("note"))
                {
                    var note = ParseNote(noteElem);
                    if (note != null)
                        measure.Notes.Add(note);
                }

                if (!measure.IsEmpty)
                    piece.Measures.Add(measure);

                measureNum++;
            }

            var warning = "";
            if (CountParts(doc) > 1) warning = "This is a multipart score. Using the first piano part only.";

            return (piece, warning);
        }
        catch (Exception ex)
        {
            return (new Piece(), $"Invalid MusicXML: {ex.Message}");
        }
    }

    private string ExtractTitle(XDocument doc)
    {
        // Priority: movement-title → work-title → credit-words → Untitled
        var movementTitle = doc.Descendants("movement-title").FirstOrDefault()?.Value?.Trim();
        if (!string.IsNullOrEmpty(movementTitle)) return movementTitle;

        var workTitle = doc.Descendants("work-title").FirstOrDefault()?.Value?.Trim();
        if (!string.IsNullOrEmpty(workTitle)) return workTitle;

        var credit = doc.Descendants("credit-words").FirstOrDefault()?.Value?.Trim();
        if (!string.IsNullOrEmpty(credit)) return credit;

        return "Untitled piece";
    }

    private XElement? FindFirstPianoPart(XDocument doc)
    {
        // Look for part with piano in name or typical piano part-id
        return doc.Descendants("part")
            .FirstOrDefault(p =>
                p.Attribute("id")?.Value?.ToLower().Contains("piano") == true ||
                p.Elements("measure").Elements("note").Any());
    }

    private XElement? FindFirstPart(XDocument doc)
    {
        return doc.Descendants("part").FirstOrDefault();
    }

    private int CountParts(XDocument doc)
    {
        return doc.Descendants("part").Count();
    }

    private Note? ParseNote(XElement noteElem)
    {
        try
        {
            var pitchElem = noteElem.Element("pitch");
            if (pitchElem == null) return null;

            var step = pitchElem.Element("step")?.Value ?? "C";
            var octaveStr = pitchElem.Element("octave")?.Value;
            if (!int.TryParse(octaveStr, out var octave)) return null;

            // Simple MIDI pitch calculation
            string[] steps = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
            var stepIndex = Array.IndexOf(steps, step);
            if (stepIndex == -1) stepIndex = 0;

            var midiPitch = (octave + 1) * 12 + stepIndex;

            var durationStr = noteElem.Element("duration")?.Value;
            var duration = double.TryParse(durationStr, out var d) ? d / 4.0 : 1.0; // quarter note = 1

            return new Note
            {
                MidiPitch = midiPitch,
                Step = step,
                Octave = octave,
                Duration = duration,
                IsChordMember = noteElem.Element("chord") != null
            };
        }
        catch
        {
            return null;
        }
    }
}