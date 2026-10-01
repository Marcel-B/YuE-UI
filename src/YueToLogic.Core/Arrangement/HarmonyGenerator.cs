using YueToLogic.Core.Diagnostics;
using YueToLogic.Core.Harmony;
using YueToLogic.Core.Model;

namespace YueToLogic.Core.Arrangement;

/// <summary>
/// Backing vocals from a melody, the way an arranger writes them by rule. Every part sings the melody's rhythm, so
/// the backing singers share the lead's syllables.
/// <list type="bullet">
/// <item>The parallel parts move with the melody by scale steps (a third is two, a sixth five), so their
/// intervals are major or minor as the key has them. Where the melody sits on a chord tone and that interval
/// would leave the chord, the part takes the nearest chord tone on its side instead (above C over F that is F,
/// not E); passing notes stay parallel.</item>
/// <item>The choir parts voice each melody note as a four-part chord with the melody on top: every combination of
/// chord tones below it is scored for how far each voice moves, how close it stays to its register, which chord
/// tones it leaves out (the third least of all), a doubled third and parallel fifths or octaves, and the cheapest
/// one wins. A melody note outside the chord is a passing note: the alto follows it a third below while tenor
/// and bass hold.</item>
/// <item>The drone holds the tonic under each phrase.</item>
/// </list>
/// The chords are the score's own; a score without chord symbols gets them from <see cref="ChordGuesser"/>.
/// </summary>
internal static class HarmonyGenerator
{
    /// <summary>What every part's track name starts with, which the way back from MIDI uses to leave them out.</summary>
    public const string TrackPrefix = "Harmony";

    private static readonly (int Interval, double Cost)[] MissingToneCosts = [(0, 6), (3, 8), (4, 8), (7, 1)];

    public static IReadOnlyList<VoiceTrack> Generate(
        ScoreDocument score,
        IReadOnlyList<VoiceTrack> tracks,
        HarmonyOptions options,
        DiagnosticBag diagnostics)
    {
        var parts = options.Parts.Distinct().ToList();
        if (parts.Count == 0)
        {
            return [];
        }

        var source = tracks.FirstOrDefault(track =>
            track.Kind == TrackKind.Melody && track.Id.Equals(options.VoiceId, StringComparison.OrdinalIgnoreCase));
        if (source is null || source.Notes.Count == 0)
        {
            // A MIDI file read back puts a lone melody on Vocal, but a score may have its melody on Ins only.
            var fallback = tracks.FirstOrDefault(track => track.Kind == TrackKind.Melody && track.Notes.Count > 0);
            if (fallback is null)
            {
                diagnostics.Warning(DiagnosticCodes.UnknownVoice, "Backing vocals were asked for, but the score has no melody to derive them from.");
                return [];
            }

            if (source is null)
            {
                diagnostics.Warning(DiagnosticCodes.UnknownVoice, $"The score has no voice '{options.VoiceId}'; the backing vocals follow '{fallback.Id}'.");
            }

            source = fallback;
        }

        var melody = source.Notes.OrderBy(n => n.StartTicks).ThenByDescending(n => n.NoteNumber).ToList();
        var keys = Keys(score, melody, options, diagnostics);
        IReadOnlyList<ChordEvent> chords = score.Chords.Where(c => c.Symbol is not null).ToList();
        if (chords.Count == 0)
        {
            chords = ChordGuesser.Guess(score, melody, keys[0].Key);
            diagnostics.Info(
                DiagnosticCodes.HarmonyChords,
                $"The score has no chord symbols; the backing vocals follow chords guessed from the melody: {string.Join(" ", chords.Select(c => c.Text))}.");
        }

        var selected = Selected(score, melody, options.Sections, diagnostics);
        var velocity = Math.Clamp(options.Velocity, 1, 127);
        var result = new Dictionary<HarmonyPart, List<NoteEvent>>();
        foreach (var part in parts)
        {
            result[part] = [];
        }

        var choir = parts.Any(p => p is HarmonyPart.Alto or HarmonyPart.Tenor or HarmonyPart.Bass);
        Voicing? previous = null;
        foreach (var note in selected)
        {
            var key = KeyAt(keys, note.StartTicks);
            var chord = ChordAt(chords, note.StartTicks);
            foreach (var part in parts)
            {
                var steps = part switch
                {
                    HarmonyPart.ThirdAbove => 2,
                    HarmonyPart.ThirdBelow => -2,
                    HarmonyPart.SixthBelow => -5,
                    _ => 0,
                };
                if (steps != 0)
                {
                    Add(result[part], note, Parallel(note.NoteNumber, steps, key, chord), velocity);
                }
            }

            if (choir)
            {
                var voicing = Voice(note.NoteNumber, key, chord, previous);
                previous = voicing;
                Add(result, HarmonyPart.Alto, note, voicing.Alto, velocity);
                Add(result, HarmonyPart.Tenor, note, voicing.Tenor, velocity);
                Add(result, HarmonyPart.Bass, note, voicing.Bass, velocity);
            }
        }

        if (result.TryGetValue(HarmonyPart.Drone, out var drone))
        {
            drone.AddRange(Drone(score, selected, keys, velocity, held: false));
        }

        if (result.TryGetValue(HarmonyPart.DroneHeld, out var held))
        {
            held.AddRange(Drone(score, selected, keys, velocity, held: true));
        }

        return [.. parts.Select(part => new VoiceTrack(TrackId(part), TrackId(part), result[part], TrackKind.Harmony))];
    }

    /// <summary>
    /// The tonic under each phrase, one pitch per phrase at least a minor third below its lowest note: sung in the
    /// melody's rhythm like the other parts, or <paramref name="held"/> from the start of the phrase to its end. A phrase
    /// ends at a rest of more than a beat, where a singer breathes, or where the key changes.
    /// </summary>
    private static IEnumerable<NoteEvent> Drone(
        ScoreDocument score,
        IReadOnlyList<NoteEvent> melody,
        List<(long Start, ScaleKey Key)> keys,
        int velocity,
        bool held)
    {
        var phrase = new List<NoteEvent>();
        foreach (var note in melody.Cast<NoteEvent?>().Append(null))
        {
            if (phrase.Count > 0 && (note is null
                || note.StartTicks - phrase.Max(n => n.StartTicks + n.DurationTicks) > score.TicksPerQuarterNote
                || KeyAt(keys, note.StartTicks) != KeyAt(keys, phrase[0].StartTicks)))
            {
                var tonic = KeyAt(keys, phrase[0].StartTicks).TonicPitchClass;
                var ceiling = phrase.Min(n => n.NoteNumber) - 3;
                var pitch = ceiling - ScaleKey.Mod12(ceiling - tonic);
                var start = phrase[0].StartTicks;
                var end = phrase.Max(n => n.StartTicks + n.DurationTicks);
                if (pitch >= 0 && held)
                {
                    yield return new NoteEvent(start, end - start, pitch, velocity);
                }
                else if (pitch >= 0)
                {
                    foreach (var sung in phrase)
                    {
                        yield return new NoteEvent(sung.StartTicks, sung.DurationTicks, pitch, velocity);
                    }
                }

                phrase.Clear();
            }

            if (note is not null)
            {
                phrase.Add(note);
            }
        }
    }

    public static string TrackId(HarmonyPart part) => part switch
    {
        HarmonyPart.ThirdAbove => $"{TrackPrefix} 3rd up",
        HarmonyPart.ThirdBelow => $"{TrackPrefix} 3rd down",
        HarmonyPart.SixthBelow => $"{TrackPrefix} 6th down",
        HarmonyPart.DroneHeld => $"{TrackPrefix} Drone held",
        _ => $"{TrackPrefix} {part}",
    };

    // ---- Parallel parts ----------------------------------------------------------------------------

    private static int Parallel(int pitch, int steps, ScaleKey key, ChordEvent? chord)
    {
        var target = key.Step(pitch, steps);
        if (chord?.Symbol is not { } symbol)
        {
            return target;
        }

        var tones = ChordTones(symbol);
        if (!tones.Contains(ScaleKey.Mod12(pitch)) || tones.Contains(ScaleKey.Mod12(target)))
        {
            return target;
        }

        // The chord tone nearest to the parallel note, at most a whole tone from it and on the same side of the melody.
        var candidates = Enumerable.Range(target - 2, 5)
            .Where(candidate => steps > 0 ? candidate >= pitch + 2 : candidate <= pitch - 2)
            .Where(candidate => tones.Contains(ScaleKey.Mod12(candidate)))
            .ToList();
        return candidates.Count == 0 ? target : candidates.MinBy(candidate => Math.Abs(candidate - target));
    }

    // ---- Choir parts -------------------------------------------------------------------------------

    private sealed record Voicing(int Soprano, int Alto, int Tenor, int Bass);

    private static Voicing Voice(int soprano, ScaleKey key, ChordEvent? chord, Voicing? previous)
    {
        if (chord?.Symbol is not { } symbol || !ChordTones(symbol).Contains(ScaleKey.Mod12(soprano)))
        {
            // A passing note: the alto moves along a third below, tenor and bass hold where they still fit.
            var alto = key.Step(soprano, -2);
            var tenor = previous is not null && previous.Tenor < alto ? previous.Tenor : key.Step(soprano, -5);
            var bass = previous is not null && previous.Bass < tenor ? previous.Bass : key.Step(soprano, -9);
            return new Voicing(soprano, alto, tenor, bass);
        }

        var tones = ChordTones(symbol);
        var bassClass = symbol.BassPitchClass ?? symbol.RootPitchClass;
        Voicing? best = null;
        var bestCost = double.PositiveInfinity;
        foreach (var alto in Pitches(tones, soprano - 9, soprano - 1))
        {
            foreach (var tenor in Pitches(tones, alto - 12, alto - 1))
            {
                foreach (var bass in Pitches([bassClass], Math.Max(28, tenor - 19), tenor - 1))
                {
                    var candidate = new Voicing(soprano, alto, tenor, bass);
                    var cost = Cost(candidate, symbol, previous);
                    if (cost < bestCost)
                    {
                        bestCost = cost;
                        best = candidate;
                    }
                }
            }
        }

        // Only a melody too low to fit three voices below it (under E1) gets here.
        return best ?? new Voicing(soprano, key.Step(soprano, -2), key.Step(soprano, -5), key.Step(soprano, -9));
    }

    private static double Cost(Voicing voicing, ChordSymbol chord, Voicing? previous)
    {
        var soprano = voicing.Soprano;
        var cost = 0.0;

        // Stay near the spacing of a quartet: alto a fourth, tenor an octave and bass an octave and a sixth under the
        // tune. Strong enough that small steps do not walk a voice out of its register over a long song.
        cost += 0.4 * (Math.Abs(voicing.Alto - (soprano - 5)) + Math.Abs(voicing.Tenor - (soprano - 11)) + Math.Abs(voicing.Bass - (soprano - 17)));
        if (previous is not null)
        {
            cost += Math.Abs(voicing.Alto - previous.Alto) + Math.Abs(voicing.Tenor - previous.Tenor) + (0.5 * Math.Abs(voicing.Bass - previous.Bass));
            cost += ParallelPerfects(previous, voicing) * 5;
        }

        var sounding = new[] { voicing.Soprano, voicing.Alto, voicing.Tenor, voicing.Bass }
            .Select(p => ScaleKey.Mod12(p - chord.RootPitchClass))
            .ToList();
        foreach (var interval in ChordVoicing.GetIntervals(chord.Quality))
        {
            if (!sounding.Contains(interval))
            {
                cost += MissingToneCosts.FirstOrDefault(m => m.Interval == interval, (interval, 3)).Cost;
            }
        }

        // A doubled third sounds thick; the root is the note to double.
        if (sounding.Count(i => i is 3 or 4) > 1)
        {
            cost += 3;
        }

        if (soprano - voicing.Alto < 3)
        {
            cost += 2;
        }

        return cost;
    }

    /// <summary>Pairs of voices that move from a fifth or an octave to the same interval again, both having moved.</summary>
    private static int ParallelPerfects(Voicing before, Voicing after)
    {
        int[] from = [before.Soprano, before.Alto, before.Tenor, before.Bass];
        int[] to = [after.Soprano, after.Alto, after.Tenor, after.Bass];
        var count = 0;
        for (var upper = 0; upper < 4; upper++)
        {
            for (var lower = upper + 1; lower < 4; lower++)
            {
                var was = ScaleKey.Mod12(from[upper] - from[lower]);
                var now = ScaleKey.Mod12(to[upper] - to[lower]);
                if (was == now && was is 0 or 7 && from[upper] != to[upper] && from[lower] != to[lower])
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static IEnumerable<int> Pitches(IReadOnlyCollection<int> pitchClasses, int lowest, int highest)
    {
        for (var pitch = Math.Max(0, lowest); pitch <= Math.Min(127, highest); pitch++)
        {
            if (pitchClasses.Contains(ScaleKey.Mod12(pitch)))
            {
                yield return pitch;
            }
        }
    }

    // ---- Key, chords and sections ------------------------------------------------------------------

    private static HashSet<int> ChordTones(ChordSymbol symbol)
    {
        var tones = ChordVoicing.GetIntervals(symbol.Quality).Select(i => ScaleKey.Mod12(symbol.RootPitchClass + i)).ToHashSet();
        if (symbol.BassPitchClass is { } bass)
        {
            tones.Add(bass);
        }

        return tones;
    }

    private static ChordEvent? ChordAt(IReadOnlyList<ChordEvent> chords, long ticks) =>
        chords.LastOrDefault(c => c.StartTicks <= ticks && ticks < c.StartTicks + Math.Max(1, c.DurationTicks));

    private static ScaleKey KeyAt(List<(long Start, ScaleKey Key)> keys, long ticks) =>
        keys.LastOrDefault(k => k.Start <= ticks, keys[0]).Key;

    /// <summary>
    /// The key per stretch of the score: the one asked for, else each key signature where the melody fits it about
    /// as well as the key it suggests (every MIDI file without a signature reads as C major), else that key.
    /// </summary>
    private static List<(long Start, ScaleKey Key)> Keys(
        ScoreDocument score,
        IReadOnlyList<NoteEvent> melody,
        HarmonyOptions options,
        DiagnosticBag diagnostics)
    {
        if (!string.IsNullOrWhiteSpace(options.Key) && ScaleKey.TryParse(options.Key, out var chosen))
        {
            diagnostics.Info(DiagnosticCodes.HarmonyKey, $"The backing vocals stay in {chosen.Name}, as chosen.");
            return [(0, chosen)];
        }

        var signatures = score.KeySignatures.Count > 0 ? score.KeySignatures : [new KeySignatureChange(0, "C", 0, false)];
        var keys = new List<(long, ScaleKey)>();
        for (var i = 0; i < signatures.Count; i++)
        {
            var start = i == 0 ? 0 : signatures[i].StartTicks;
            var end = i + 1 < signatures.Count ? signatures[i + 1].StartTicks : long.MaxValue;
            var notes = melody.Where(n => n.StartTicks >= start && n.StartTicks < end).ToList();
            var written = ScaleKey.FromSignature(signatures[i].Sharps, signatures[i].IsMinor);
            var found = KeyFinder.Find(notes);
            var key = found is null || KeyFinder.Fit(written, notes) >= KeyFinder.Fit(found, notes) - 0.02 ? written : found;
            keys.Add((start, key));
            diagnostics.Info(
                DiagnosticCodes.HarmonyKey,
                key == written
                    ? $"The backing vocals stay in {key.Name}, the key of the score."
                    : $"The backing vocals stay in {key.Name}, the key the melody suggests (the score says {written.Name}).");
        }

        return keys;
    }

    /// <summary>The melody notes that get backing vocals: one note at a time, in the chosen sections only.</summary>
    private static List<NoteEvent> Selected(
        ScoreDocument score,
        IReadOnlyList<NoteEvent> melody,
        IReadOnlyList<string> sections,
        DiagnosticBag diagnostics)
    {
        // Of notes starting together the highest is the tune.
        var notes = melody.GroupBy(n => n.StartTicks).Select(g => g.First()).ToList();
        var wanted = sections.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
        if (wanted.Count == 0)
        {
            return notes;
        }

        var markers = score.Sections.OrderBy(s => s.StartTicks).ToList();
        bool Chosen(NoteEvent note)
        {
            var section = markers.LastOrDefault(s => s.StartTicks <= note.StartTicks);
            return section is not null && wanted.Exists(w => section.Name.Contains(w, StringComparison.OrdinalIgnoreCase));
        }

        var selected = notes.Where(Chosen).ToList();
        if (selected.Count == 0)
        {
            diagnostics.Warning(
                DiagnosticCodes.HarmonySections,
                markers.Count == 0
                    ? "The score names no sections, so no notes are in the chosen ones and the backing vocals stay empty."
                    : $"No section is named like {string.Join(" or ", wanted.Select(w => $"'{w}'"))} (the score has {string.Join(", ", markers.Select(m => m.Name).Distinct())}), so the backing vocals stay empty.");
        }

        return selected;
    }

    private static void Add(Dictionary<HarmonyPart, List<NoteEvent>> result, HarmonyPart part, NoteEvent note, int pitch, int velocity)
    {
        if (result.TryGetValue(part, out var notes))
        {
            Add(notes, note, pitch, velocity);
        }
    }

    private static void Add(List<NoteEvent> notes, NoteEvent note, int pitch, int velocity)
    {
        if (pitch is >= 0 and <= 127)
        {
            notes.Add(new NoteEvent(note.StartTicks, note.DurationTicks, pitch, velocity));
        }
    }
}
