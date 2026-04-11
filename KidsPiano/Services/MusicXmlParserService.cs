using System.Runtime.InteropServices.Marshalling;
using System.Windows.Controls;
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
            // divisions = how many MusicXML duration units make one quarter note.
            // Must be read from <attributes><divisions> and carried forward across measures.
            int divisions = 1;

            foreach (var m in measures)
            {
                // Update divisions if this measure redefines them
                var divisionsStr = m.Element("attributes")?.Element("divisions")?.Value;
                if (int.TryParse(divisionsStr, out var d) && d > 0)
                    divisions = d;

                var measure = new Measure { Number = measureNum };

                // Time signature
                var timeElem = m.Element("attributes")?.Element("time");
                if (timeElem != null)
                {
                    if (int.TryParse(timeElem.Element("beats")?.Value, out var beats)) measure.BeatsPerMeasure = beats;
                    if (int.TryParse(timeElem.Element("beat-type")?.Value, out var beatType)) measure.BeatType = beatType;
                }

                // Tempo from <sound tempo="N">
                foreach (var sound in m.Descendants("sound"))
                {
                    if (double.TryParse(sound.Attribute("tempo")?.Value, out var bpm) && bpm > 0)
                        measure.Tempo = bpm;
                }

                Func<string, double?> typeToUnifiedDuration = null;
                Func<string, double?> GetTypeToUnifiedDuration()
                {
                    if (typeToUnifiedDuration is null)
                    {
                        int? typeDenominator = null;
                        int? duration = null;
                        foreach (var elem in m.Elements())
                        {
                            if (elem.Name == "note")
                            {
                                var note = ParseNote(elem, divisions, null);
                                if (note is not null && note.RawDuration is not null && note.Type is not null)
                                {
                                    typeDenominator = TypeToDenominator(note.Type);
                                    duration = note.RawDuration;
                                    break;
                                }
                            }
                        }
                        if (typeDenominator.HasValue && duration.HasValue)
                        {
                            typeToUnifiedDuration = s =>
                            {
                                var denomintatorOfS = TypeToDenominator(s);
                                double? rawDurationOfS = ((double)duration * typeDenominator) / denomintatorOfS;
                                return rawDurationOfS / divisions;
                            };
                        }
                    }
                    return typeToUnifiedDuration;
                }

                double offset = 0;
                double nextOffset = 0;
                double? afterGraceOffset = null;
                List<Note> notes = [];
                List<(double, double)> gracePeriods = [];
                foreach (var elem in m.Elements())
                {
                    if (elem.Name == "note")
                    {
                        var note = ParseNote(elem, divisions, GetTypeToUnifiedDuration());
                        if (note is not null)
                        {
                            if (afterGraceOffset is not null && !note.IsGrace)
                            {
                                nextOffset = afterGraceOffset.Value;
                                offset = nextOffset;
                                afterGraceOffset = null;
                            }

                            if (note.IsChordMember)
                            {
                                note.Start = offset;
                            }
                            else
                            {
                                note.Start = nextOffset;
                                offset = nextOffset;
                            }

                            if (note.IsGrace)
                            {
                                if (afterGraceOffset is null)
                                {
                                    afterGraceOffset = note.Start;
                                }
                                gracePeriods.Add((note.Start, note.Duration));
                            }
                            nextOffset = note.Start + note.Duration;
                            if (note.MidiPitch >= 0)
                            {
                                // NOT to add the rest
                                // Rests are returned to factor in the durations
                                notes.Add(note);
                            }
                        }
                    }
                    else if (elem.Name == "forward")
                    {
                        if (afterGraceOffset is not null)
                        {
                            nextOffset = afterGraceOffset.Value;
                            offset = nextOffset;
                            afterGraceOffset = null;
                        }
                        string? forwardDurationStr = elem.Element("duration")?.Value;
                        if (forwardDurationStr is not null)
                        {
                            var forward = GetUnifiedDuration(forwardDurationStr, divisions);
                            if (forward.HasValue)
                            {
                                nextOffset += forward.Value;
                                offset = nextOffset;
                            }
                        }
                    }
                    else if (elem.Name == "backup")
                    {
                        if (afterGraceOffset is not null)
                        {
                            nextOffset = afterGraceOffset.Value;
                            offset = nextOffset;
                            afterGraceOffset = null;
                        }
                        string? backupDurationStr = elem.Element("duration")?.Value;
                        if (backupDurationStr is not null)
                        {
                            var backup = GetUnifiedDuration(backupDurationStr, divisions);
                            if (backup.HasValue)
                            {
                                nextOffset -= backup.Value;
                                offset = nextOffset;
                            }
                        }
                    }
                }

                gracePeriods.Sort((g1, g2) => g1.CompareTo(g2));
                List<(double, double)> combinedGracePeriods = [];
                foreach (var (gStart, gDuration) in gracePeriods)
                {
                    if (combinedGracePeriods.Count > 0)
                    {
                        var (fStart, fDuration) = combinedGracePeriods[0];
                        var oldEnd = fStart + fDuration;
                        if (gStart <= oldEnd)
                        {
                            var newEnd = gStart + gDuration;
                            if (newEnd > oldEnd)
                            {
                                combinedGracePeriods[0] = (fStart, newEnd);
                            }
                        }
                        else
                        {
                            combinedGracePeriods.Add((gStart, gDuration));
                        }
                    }
                    else
                    {
                        combinedGracePeriods.Add((gStart, gDuration));
                    }
                }

                // Set actual start considering graces
                var ic = 0;
                notes.Sort((n1, n2) => n1.Start.CompareTo(n2.Start));
                foreach (var n in notes)
                {
                    if (n.IsGrace)
                    {
                        n.ActualStart = n.Start;
                    }
                    else
                    {
                        for (; ic < combinedGracePeriods.Count; ic++)
                        {
                            var (cStart, cDuration) = combinedGracePeriods[ic];
                            var cEnd = cStart + cDuration;
                            if (cEnd > n.Start)
                            {
                                break;
                            }
                        }
                        if (ic < combinedGracePeriods.Count)
                        {
                            // cEnd > n.Start
                            var (cStart, cDuration) = combinedGracePeriods[ic];
                            var oldEnd = n.Start + n.Duration;
                            if (cStart < oldEnd)
                            {
                                var actualStart = Math.Min(cStart + cDuration, oldEnd);
                                n.ActualStart = actualStart;
                            }
                            else
                            {
                                n.ActualStart = n.Start;
                            }
                        }
                        else
                        {
                            n.ActualStart = n.Start;
                        }
                    }
                }

                notes.Sort((n1, n2) => n1.ActualStart.CompareTo(n2.ActualStart));
                measure.Notes.AddRange(notes);

                if (!measure.IsEmpty)
                    piece.Measures.Add(measure);

                measureNum++;
            }

            // Propagate tempo forward: if only some measures have a tempo marking,
            // fill the rest with the last seen value.
            double lastTempo = 120;
            foreach (var measure in piece.Measures)
            {
                if (measure.Tempo > 0) lastTempo = measure.Tempo;
                else measure.Tempo = lastTempo;
            }

            var warning = "";
            if (CountParts(doc) > 1) warning = "This is a multipart score. Using the first piano part only.";

            return (piece, warning);
        }
        catch (Exception ex)
        {
            return (new Piece(), $"Invalid MusicXML: {ex.Message}");
        }


        int? TypeToDenominator(string type)
        {
            return type switch
            {
                "whole" => 1,       // TODO verify
                "half" => 2,
                "quarter" => 4,
                "eighth" => 8,         // TODO verify
                "16th" => 16,       // TODO verify
                "32nd" => 32,       // TODO verify
                "64th" => 64,
                _ => null
            };
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
        HashSet<string> pianoParts = [];
        XElement? partlist = doc.Descendants("part-list").FirstOrDefault();
        if (partlist != null)
        {
            pianoParts = partlist.Elements("score-part").Where(scorePart =>
            {
                var partName = scorePart.Element("part-name")?.Value?.ToLower();
                var scoreInstrument = scorePart.Element("score-instrument");
                var instrumentName = scoreInstrument?.Element("instrument-name")?.Value?.ToLower();
                var instrumentSound = scoreInstrument?.Element("instrument-sound")?.Value?.ToLower();
                return partName?.Contains("piano") == true || instrumentName?.Contains("piano") == true || instrumentSound?.Contains("piano") == true;
            }).Select(x=>x.Attribute("id")?.Value).Where(x=>x is not null).Select(x=>x!).ToHashSet();
        }

        return doc.Descendants("part")
            .FirstOrDefault(p =>
            {
                var id = p.Attribute("id")?.Value;
                if (pianoParts.Count > 0 && id is not null)
                {
                    return pianoParts.Contains(id);
                }
                return id?.ToLower().Contains("piano") == true ||
                p.Element("part-name")?.Value?.ToLower().Contains("piano") == true;
            }, doc.Descendants("part").FirstOrDefault(p=> p.Elements("measure").Elements("note").Any()));
    }

    private XElement? FindFirstPart(XDocument doc) =>
        doc.Descendants("part").FirstOrDefault();

    private int CountParts(XDocument doc) =>
        doc.Descendants("part").Count();

    private static double? GetUnifiedDuration(string durationStr, int divisions)
    {
        return double.TryParse(durationStr, out var raw)
                ? raw / divisions   // ← the fix: use actual divisions, not hardcoded 4
                : null;
    }

    private Note? ParseNote(XElement noteElem, int divisions, Func<string, double?> typeToUnitifedDuration)
    {
        try
        {
            var pitchElem = noteElem.Element("pitch");
            //  if (pitchElem == null) return null; // rest or non-pitched

            int midiPitch = -1;
            string? step = null;
            int octave = -1;

            if (pitchElem is not null)
            {
                step = pitchElem.Element("step")?.Value ?? "C";
                if (!int.TryParse(pitchElem.Element("octave")?.Value, out octave)) return null;

                // Accidental from <alter> (semitones, can be -1, 0, 1)
                int alter = 0;
                if (int.TryParse(pitchElem.Element("alter")?.Value, out var a)) alter = a;

                string[] naturalSteps = { "C", "D", "E", "F", "G", "A", "B" };
                int[] naturalOffsets = { 0, 2, 4, 5, 7, 9, 11 };
                var naturalStepIndex = Array.IndexOf(naturalSteps, step);
                if (naturalStepIndex == -1) naturalStepIndex = 0;

                midiPitch = (octave + 1) * 12 + naturalOffsets[naturalStepIndex] + alter;
            }

            var type = noteElem.Element("type")?.Value;
            var isGrace = noteElem.Element("grace") is not null;

            // Duration in quarter-note beats = MusicXML duration / divisions
            var durationStr = noteElem.Element("duration")?.Value;
            int? rawDuration = null;
            if (int.TryParse(durationStr, out var rd))
            {
                rawDuration = rd;
            }

            double? duration = null;
            if (rawDuration.HasValue)
            {
                duration = GetUnifiedDuration(durationStr, divisions);
            }
            else if (type is not null && isGrace)
            {
                // mordant/grace
                duration = typeToUnitifedDuration?.Invoke(type);
            }

            if (duration is null)
            {
                return null;
            }

            var isChord = noteElem.Element("chord") is not null;

            return new Note
            {
                MidiPitch = midiPitch,
                Step = step,
                Octave = octave,
                RawDuration = rawDuration,
                Duration = duration.Value,
                Type = type,
                IsChordMember = isChord,
                IsGrace = isGrace
            };
        }
        catch
        {
            return null;
        }
    }
}
