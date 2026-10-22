using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

namespace RhythmGameUI
{
    /// <summary>Everything derived from the .mid file: notes, rests, barlines, chord zones and timing helpers.</summary>
    class Chart
    {
        public TempoMap Tempo = TempoMap.Default;
        public bool HasMidi;
        public List<MusicSymbol> Symbols = new();
        public List<float> Barlines = new();
        public List<float> BeatTimes = new();
        public List<CompingZone> Zones = new();
        public List<BeamGroup> Beams = new();
        public int TimeSigNum = 4, TimeSigDen = 4;
        public int KeySignature;            // +sharps / -flats
        public float LeadIn;                // seconds of count-in before bar 1
        public float LastMusicTime;
        public int PlayableCount;

        // ---------------- Time conversion ----------------
        public float TicksToSeconds(long ticks) =>
            (float)(TimeConverter.ConvertTo<MetricTimeSpan>(Math.Max(0, ticks), Tempo).TotalMicroseconds / 1_000_000.0);

        public long SecondsToTicks(float seconds) =>
            TimeConverter.ConvertFrom(new MetricTimeSpan((long)(Math.Max(0f, seconds) * 1_000_000L)), Tempo);

        /// <summary>Bar and beat are 1-based, exactly as written in the JSON.</summary>
        public float BarBeatToSeconds(int bar, double beat) =>
            TicksToSeconds(BarBeatToTicks(bar, beat));

        public long BarBeatToTicks(int bar, double beat) =>
            TimeConverter.ConvertFrom(new BarBeatFractionTimeSpan(Math.Max(0, bar - 1), Math.Max(0.0, beat - 1.0)), Tempo);

        public (int bar, double beat) SecondsToBarBeat(float seconds)
        {
            var bb = TimeConverter.ConvertTo<BarBeatFractionTimeSpan>(SecondsToTicks(seconds), Tempo);
            return ((int)bb.Bars + 1, bb.Beats + 1.0);
        }

        public float BeatsAfter(float startSeconds, double beats)
        {
            if (beats <= 0) return 0f;
            long start = SecondsToTicks(startSeconds);
            long len = TimeConverter.ConvertFrom(new MusicalTimeSpan((long)Math.Round(beats * 1000), TimeSigDen * 1000L), Tempo);
            return TicksToSeconds(start + len) - TicksToSeconds(start);
        }

        public float SecondsPerBeat(float atSeconds) => BeatsAfter(Math.Max(0f, atSeconds), 1.0);

        /// <summary>Continuous beat position (0 at bar 1 beat 1, negative during the count-in).</summary>
        public double BeatPosition(float t)
        {
            if (BeatTimes.Count < 2) return t / 0.5;
            if (t < BeatTimes[0]) return (t - BeatTimes[0]) / (BeatTimes[1] - BeatTimes[0]);
            int i = BeatTimes.BinarySearch(t);
            if (i < 0) i = ~i - 1;
            if (i >= BeatTimes.Count - 1)
            {
                float last = BeatTimes[^1] - BeatTimes[^2];
                return BeatTimes.Count - 1 + (t - BeatTimes[^1]) / last;
            }
            return i + (t - BeatTimes[i]) / (BeatTimes[i + 1] - BeatTimes[i]);
        }

        public void ResetRuntimeState()
        {
            foreach (var s in Symbols)
            {
                s.IsHit = s.IsMissed = s.Skipped = s.EnemyResolved = false;
                s.CustomColor = null;
            }
            foreach (var z in Zones)
            {
                z.AccumulatedHoldTime = 0f;
                z.HasStruck = z.MissedInitialStrike = z.IsScored = z.EnemyResolved = z.IsBeingHeld = false;
                z.InitialStrikeDiff = 999f;
                z.StrikeFlashTimer = 0f;
            }
        }
    }

    /// <summary>Everything derived from the .json file.</summary>
    class LevelData
    {
        public LevelConfig Config = new();
        public List<GameEvent> Events = new();
        public List<Decoration> Decorations = new();
        public List<string> Warnings = new();
        public float LastEventTime;
    }

    static class StaffMath
    {
        static readonly int[] SharpLetters = { 0, 0, 1, 1, 2, 3, 3, 4, 4, 5, 5, 6 };
        static readonly int[] SharpAccs = { 0, 1, 0, 1, 0, 0, 1, 0, 1, 0, 1, 0 };
        static readonly int[] FlatLetters = { 0, 1, 1, 2, 2, 3, 4, 4, 5, 5, 6, 6 };
        static readonly int[] FlatAccs = { 0, -1, 0, -1, 0, 0, -1, 0, -1, 0, -1, 0 };

        /// <summary>Letter (0=C..6=B) and accidental for a pitch, spelled to suit the key.</summary>
        public static (int letter, int accidental) Spell(int pitch, int keySig)
        {
            int pc = ((pitch % 12) + 12) % 12;
            return keySig < 0 ? (FlatLetters[pc], FlatAccs[pc]) : (SharpLetters[pc], SharpAccs[pc]);
        }

        /// <summary>Staff steps from the top line: treble top line = F5 = 0, bass top line = A3 = 0.</summary>
        public static (int treble, int bass) Steps(int pitch, int keySig)
        {
            var (letter, _) = Spell(pitch, keySig);
            int octave = pitch / 12 - 1;
            int absolute = octave * 7 + letter;
            return (38 - absolute, 26 - absolute);
        }

        // Treble staff steps for key signature accidentals (bass = +2)
        public static readonly int[] SharpSigSteps = { 0, 3, -1, 2, 5, 1, 4 };
        public static readonly int[] FlatSigSteps = { 4, 1, 5, 2, 6, 3, 7 };
    }

    static class LevelLoader
    {
        // ------------------------------------------------------------------
        //  MIDI -> sheet music
        // ------------------------------------------------------------------
        public static Chart LoadChart(string midiPath, List<string> warnings)
        {
            var chart = new Chart();
            MidiFile? midi = null;
            if (File.Exists(midiPath))
            {
                try { midi = MidiFile.Read(midiPath); }
                catch (Exception ex) { warnings.Add($"Could not read {Path.GetFileName(midiPath)}: {ex.Message}"); }
            }
            else warnings.Add($"No MIDI file at {midiPath}");

            List<Note> notes = new();
            if (midi != null)
            {
                chart.HasMidi = true;
                chart.Tempo = midi.GetTempoMap();
                notes = midi.GetNotes().OrderBy(n => n.Time).ThenBy(n => n.NoteNumber).ToList();
                var key = midi.GetTrackChunks().SelectMany(c => c.Events).OfType<KeySignatureEvent>().FirstOrDefault();
                if (key != null) chart.KeySignature = Math.Clamp((int)key.Key, -7, 7);
            }

            var ts = chart.Tempo.GetTimeSignatureAtTime(new MidiTimeSpan(0));
            chart.TimeSigNum = ts.Numerator;
            chart.TimeSigDen = ts.Denominator;

            // Two bars of count-in before the music starts
            chart.LeadIn = chart.TicksToSeconds(chart.BarBeatToTicks(3, 1));

            long lastTick = notes.Count > 0 ? notes.Max(n => n.Time + n.Length) : chart.BarBeatToTicks(9, 1);
            for (int bar = 0; ; bar++)
            {
                long tick = chart.BarBeatToTicks(bar + 1, 1);
                if (tick > lastTick + 1000) break;
                chart.Barlines.Add(chart.TicksToSeconds(tick));
                for (int b = 0; b < chart.TimeSigNum; b++)
                    chart.BeatTimes.Add(chart.BarBeatToSeconds(bar + 1, b + 1));
            }
            chart.LastMusicTime = chart.TicksToSeconds(lastTick);

            BuildSymbols(chart, notes);
            return chart;
        }

        static void GetSymbolDetails(double fraction, out SymbolType type, out bool dotted)
        {
            dotted = false;
            if (fraction >= 0.875) type = SymbolType.Whole;
            else if (fraction >= 0.625) { type = SymbolType.Half; dotted = true; }
            else if (fraction >= 0.4375) type = SymbolType.Half;
            else if (fraction >= 0.3125) { type = SymbolType.Quarter; dotted = true; }
            else if (fraction >= 0.21875) type = SymbolType.Quarter;
            else if (fraction >= 0.15625) { type = SymbolType.Eighth; dotted = true; }
            else if (fraction >= 0.09375) type = SymbolType.Eighth;
            else type = SymbolType.Sixteenth;
        }

        // MIDI channels as shown in a DAW (1-based):
        //   1 = treble voice, 2 = treble chord zones, 3 = bass chord zones, 4 = bass voice.
        // DryWetMidi numbers channels from 0, so these are 0, 1, 2, 3 in code.
        const int TrebleVoiceChannel = 0, TrebleZoneChannel = 1, BassZoneChannel = 2, BassVoiceChannel = 3;

        /// <summary>Which stave a MIDI note belongs to. Other channels keep the old rule (below middle C = bass).</summary>
        static bool IsBassStaff(Note n) => (int)n.Channel switch
        {
            TrebleZoneChannel => false,
            BassZoneChannel or BassVoiceChannel => true,
            _ => n.NoteNumber < 60,
        };

        static void BuildSymbols(Chart chart, List<Note> notes)
        {
            var tempo = chart.Tempo;
            int keySig = chart.KeySignature;
            long ticksPerWhole = 4L * ((TicksPerQuarterNoteTimeDivision)tempo.TimeDivision).TicksPerQuarterNote;

            int[] keySigAccs = new int[7];
            if (keySig > 0) { int[] order = { 3, 0, 4, 1, 5, 2, 6 }; for (int i = 0; i < keySig; i++) keySigAccs[order[i]] = 1; }
            else if (keySig < 0) { int[] order = { 6, 2, 5, 1, 4, 0, 3 }; for (int i = 0; i < -keySig; i++) keySigAccs[order[i]] = -1; }

            // Everything below is tracked separately for each stave [0 = treble, 1 = bass]:
            // where its last note ends (for rests) and which accidentals are active in the current bar.
            long[] staffTick = { 0, 0 };
            bool[] staffUsed = { false, false };
            int[][] activeAccs = { (int[])keySigAccs.Clone(), (int[])keySigAccs.Clone() };
            int currentBar = 0;

            foreach (var note in notes)
            {
                int staff = IsBassStaff(note) ? 1 : 0;
                bool onBass = staff == 1;
                float t = chart.TicksToSeconds(note.Time);
                while (currentBar < chart.Barlines.Count && t >= chart.Barlines[currentBar] - 0.01f)
                {
                    activeAccs[0] = (int[])keySigAccs.Clone();
                    activeAccs[1] = (int[])keySigAccs.Clone();
                    currentBar++;
                }

                // Rest between this stave's previous note and this one (notes on the other stave don't fill the gap)
                if (note.Time > staffTick[staff])
                    AddRests(chart, staffTick[staff], note.Time, onBass, ticksPerWhole);
                staffTick[staff] = Math.Max(staffTick[staff], note.Time + note.Length);
                staffUsed[staff] = true;

                int pitch = note.NoteNumber;
                int pitchClass = pitch % 12;
                var (letter, absAcc) = StaffMath.Spell(pitch, keySig);
                int accToDraw = 0;
                var accs = activeAccs[staff];   // accidentals carry through the bar on their own stave only
                if (accs[letter] != absAcc)
                {
                    accToDraw = absAcc == 0 ? 2 : absAcc;
                    accs[letter] = absAcc;
                }
                var (trebleStep, bassStep) = StaffMath.Steps(pitch, keySig);

                var bb = TimeConverter.ConvertTo<BarBeatFractionTimeSpan>(note.Time, tempo);
                long beatId = bb.Bars * 1000 + (long)bb.Beats;

                float lenSec = (float)(note.LengthAs<MetricTimeSpan>(tempo).TotalMicroseconds / 1_000_000.0);
                var musical = note.LengthAs<MusicalTimeSpan>(tempo);
                double wholeNotes = (double)musical.Numerator / musical.Denominator;
                GetSymbolDetails(wholeNotes, out var nType, out bool nDotted);

                // Channels 2 and 3 (index 1 and 2) are chord ("comping") zones, shown as ghost notes
                if (note.Channel == TrebleZoneChannel || note.Channel == BassZoneChannel)
                {
                    bool isBass = note.Channel == BassZoneChannel;
                    var zone = chart.Zones.FirstOrDefault(z => z.IsBass == isBass && Math.Abs(z.StartTime - t) < 0.1f);
                    if (zone != null)
                    {
                        zone.PitchClasses.Add(pitchClass);
                        zone.EndTime = Math.Max(zone.EndTime, t + lenSec);
                    }
                    else
                    {
                        chart.Zones.Add(new CompingZone { StartTime = t, EndTime = t + lenSec, IsBass = isBass, PitchClasses = { pitchClass } });
                    }

                    if (wholeNotes >= 1.8)
                    {
                        float half = lenSec / 2f;
                        chart.Symbols.Add(new MusicSymbol
                        {
                            TimeSeconds = t, DurationSeconds = half, MidiPitch = pitch, Type = SymbolType.Whole, OnBassStaff = onBass,
                            IsGhost = true, HasTie = true, TieDurationSeconds = half,
                            TrebleStep = trebleStep, BassStep = bassStep, AccidentalToDraw = accToDraw, BeatID = beatId
                        });
                        chart.Symbols.Add(new MusicSymbol
                        {
                            TimeSeconds = t + half, DurationSeconds = half, MidiPitch = pitch, Type = SymbolType.Whole, OnBassStaff = onBass,
                            IsGhost = true, TrebleStep = trebleStep, BassStep = bassStep, BeatID = beatId + 1000
                        });
                    }
                    else
                    {
                        chart.Symbols.Add(new MusicSymbol
                        {
                            TimeSeconds = t, DurationSeconds = lenSec, MidiPitch = pitch, Type = nType, IsDotted = nDotted, OnBassStaff = onBass,
                            IsGhost = true, TrebleStep = trebleStep, BassStep = bassStep, AccidentalToDraw = accToDraw, BeatID = beatId
                        });
                    }
                    continue;
                }

                chart.Symbols.Add(new MusicSymbol
                {
                    TimeSeconds = t, DurationSeconds = lenSec, MidiPitch = pitch, Type = nType, IsDotted = nDotted, OnBassStaff = onBass,
                    TrebleStep = trebleStep, BassStep = bassStep, AccidentalToDraw = accToDraw, BeatID = beatId
                });
            }

            // Fill each stave with rests up to the end of the last bar, so both staves finish together.
            long songEnd = Math.Max(staffTick[0], staffTick[1]);
            for (int bar = 1; ; bar++)
            {
                long tick = chart.BarBeatToTicks(bar, 1);
                if (tick >= songEnd) { songEnd = tick; break; }
            }
            for (int staff = 0; staff < 2; staff++)
                if (staffUsed[staff] && staffTick[staff] < songEnd)
                    AddRests(chart, staffTick[staff], songEnd, staff == 1, ticksPerWhole);

            // Rests and tied ghost notes were added out of order, so sort once (stable).
            chart.Symbols = chart.Symbols.OrderBy(s => s.TimeSeconds).ToList();
            foreach (var zone in chart.Zones)
            {
                zone.ChordName = IdentifyChord(zone.PitchClasses);
                // The notes that make up the chord, lowest first, with their spelled names for the zone's note pills
                zone.Tones = chart.Symbols
                    .Where(s => s.IsGhost && s.OnBassStaff == zone.IsBass && Math.Abs(s.TimeSeconds - zone.StartTime) < 0.1f)
                    .GroupBy(s => s.MidiPitch).Select(g => g.First())
                    .OrderBy(s => s.MidiPitch)
                    .Select(s => (s, NoteName(s.MidiPitch, keySig)))
                    .ToList();
            }
            chart.PlayableCount = chart.Symbols.Count(s => s.IsPlayable);

            // A treble rest that happens while bass notes are playing would sit on top of them when there's only one
            // stave, so it only fades in once the bass clef (second stave) is showing.
            var bassNotes = chart.Symbols.Where(s => s.OnBassStaff && !s.IsRest).ToList();
            foreach (var r in chart.Symbols.Where(s => s.IsRest && !s.OnBassStaff))
            {
                float end = r.TimeSeconds + r.DurationSeconds - 0.01f;
                r.HideWhenSingleStave = bassNotes.Any(n => n.TimeSeconds < end && n.TimeSeconds + n.DurationSeconds > r.TimeSeconds + 0.01f);
            }

            // Chords and beams are grouped per stave, so a treble note and a bass note on the same beat never share a
            // stem, and eighth notes are never beamed across the two staves. Ghost notes are never beamed.
            int chordId = 0, beamId = 0;
            float[] lastTime = { -999f, -999f };
            long[] lastBeat = { -1, -1 };
            int[] chordOf = { 0, 0 }, beamOf = { 0, 0 };
            foreach (var s in chart.Symbols.Where(s => s.IsPlayable))
            {
                int k = s.OnBassStaff ? 1 : 0;
                if (Math.Abs(s.TimeSeconds - lastTime[k]) > 0.02f) { chordOf[k] = ++chordId; lastTime[k] = s.TimeSeconds; }
                s.ChordID = chordOf[k];

                if (s.Type is SymbolType.Eighth or SymbolType.Sixteenth)
                {
                    if (s.BeatID != lastBeat[k]) { beamOf[k] = ++beamId; lastBeat[k] = s.BeatID; }
                    s.BeamGroupID = beamOf[k];
                }
                else lastBeat[k] = -1;
            }

            // Default stems for a single stave; the game redoes this once it knows when the bass clef is on.
            LayoutStems(chart, _ => false);
        }

        /// <summary>
        /// Adds rests to one stave between two ticks. Rests are split at barlines (an empty bar is one whole rest),
        /// and what's left inside a bar is broken into the largest standard rests that fit.
        /// </summary>
        static void AddRests(Chart chart, long from, long to, bool onBass, long ticksPerWhole)
        {
            long minRest = ticksPerWhole / 16;   // anything shorter is just MIDI timing slop
            // Rest values in whole notes, largest first (dotted values included)
            double[] values = { 1, 0.75, 0.5, 0.375, 0.25, 0.1875, 0.125, 0.09375, 0.0625 };

            // Snap both ends to the nearest semiquaver, so a note released a few ticks early (very common in
            // recorded or exported MIDI) still gives a rest that starts on the beat.
            long snap = Math.Max(1, ticksPerWhole / 16);
            from = (long)Math.Round(from / (double)snap) * snap;
            to = (long)Math.Round(to / (double)snap) * snap;

            long pos = from;
            while (to - pos >= minRest)
            {
                // End of the bar containing pos
                var bb = TimeConverter.ConvertTo<BarBeatTicksTimeSpan>(pos, chart.Tempo);
                long barEnd = chart.BarBeatToTicks((int)bb.Bars + 2, 1);
                long barStart = chart.BarBeatToTicks((int)bb.Bars + 1, 1);
                long segEnd = Math.Min(to, barEnd);

                if (pos == barStart && segEnd == barEnd)
                {
                    // A whole empty bar is always a whole rest, whatever the time signature
                    AddRest(chart, pos, barEnd, SymbolType.Whole, false, onBass);
                    pos = barEnd;
                    continue;
                }

                long remaining = segEnd - pos;
                if (remaining < minRest) { pos = segEnd; continue; }
                // Rests line up with the beat, like printed music: a half rest only starts on beat 1 or 3 (in 4/4),
                // a dotted rest only where it doesn't hide the next strong beat. So 3 beats of rest from beat 2 is
                // written quarter + half, not one dotted half.
                long offset = pos - barStart;
                long len = 0; double value = 0.0625;
                foreach (bool strict in new[] { true, false })   // second pass: MIDI slightly off the grid, just fit
                {
                    foreach (double v in values)
                    {
                        long ticks = (long)Math.Round(v * ticksPerWhole);
                        if (ticks > remaining) continue;
                        bool dottedValue = v is 0.75 or 0.375 or 0.1875 or 0.09375;
                        long grid = dottedValue ? ticks / 3 * 4 : ticks;   // dotted: aligned to twice the undotted value
                        if (strict && grid > 0 && offset % grid != 0) continue;
                        len = ticks; value = v; break;
                    }
                    if (len > 0) break;
                }
                if (len == 0) { pos = segEnd; continue; }
                GetSymbolDetails(value, out var type, out bool dotted);
                AddRest(chart, pos, pos + len, type, dotted, onBass);
                pos += len;
            }
        }

        static void AddRest(Chart chart, long from, long to, SymbolType type, bool dotted, bool onBass)
        {
            float start = chart.TicksToSeconds(from);
            chart.Symbols.Add(new MusicSymbol
            {
                TimeSeconds = start, DurationSeconds = chart.TicksToSeconds(to) - start,
                IsRest = true, Type = type, IsDotted = dotted, OnBassStaff = onBass
            });
        }

        /// <summary>
        /// Works out stem directions and builds the beams.
        /// With one stave, the usual rule applies: notes above the middle line point down, below it point up.
        /// While the bass clef is showing, every treble stem points up and every bass stem points down, so stems
        /// never reach into the gap between the staves and you can always tell which stave a note belongs to.
        /// Each chord gets a single stem drawn from its far note through to the note nearest the stem's tip.
        /// </summary>
        public static void LayoutStems(Chart chart, Func<float, bool> bassClefOnAt)
        {
            bool StemDown(IEnumerable<MusicSymbol> notes, MusicSymbol first) =>
                bassClefOnAt(first.TimeSeconds) ? first.OnBassStaff : notes.Average(s => s.TrebleStep) < 4;

            foreach (var chord in chart.Symbols.Where(s => s.IsPlayable).GroupBy(s => s.ChordID))
            {
                var list = chord.ToList();
                bool down = StemDown(list, list[0]);
                // The stem tip end: lowest note for stems down, highest for stems up. The stem starts at the other end.
                var near = down ? list.MinBy(s => s.MidiPitch)! : list.MaxBy(s => s.MidiPitch)!;
                var far = down ? list.MaxBy(s => s.MidiPitch)! : list.MinBy(s => s.MidiPitch)!;
                foreach (var s in list)
                {
                    s.IsStemDown = down;
                    s.DrawsStem = s == near;
                    s.StemFrom = s == near ? far : null;
                }
            }

            chart.Beams.Clear();
            foreach (var group in chart.Symbols.Where(s => s.BeamGroupID != -1).GroupBy(s => s.BeamGroupID))
            {
                var list = group.ToList();
                // A beam needs at least two different note times; otherwise it's a flagged note.
                if (list.Select(s => s.TimeSeconds).Distinct().Count() < 2) { foreach (var s in list) s.BeamGroupID = -1; continue; }
                bool down = StemDown(list, list[0]);
                foreach (var s in list) { s.IsStemDown = down; s.DrawsStem = false; }
                // For each chord: the note nearest the beam sets the beam height, the far note is where the stem starts.
                var chords = list.GroupBy(s => s.ChordID).OrderBy(g => g.First().TimeSeconds).ToList();
                chart.Beams.Add(new BeamGroup
                {
                    Notes = chords.Select(g => down ? g.MinBy(s => s.MidiPitch)! : g.MaxBy(s => s.MidiPitch)!).ToList(),
                    FarNotes = chords.Select(g => down ? g.MaxBy(s => s.MidiPitch)! : g.MinBy(s => s.MidiPitch)!).ToList(),
                    HasSixteenths = list.Any(s => s.Type == SymbolType.Sixteenth),
                    StemDown = down
                });
            }
        }

        /// <summary>Letter name with accidental, spelled for the key (e.g. "F#" in D major, "Bb" in F major).</summary>
        static string NoteName(int pitch, int keySig)
        {
            var (letter, acc) = StaffMath.Spell(pitch, keySig);
            return "CDEFGAB"[letter] + (acc > 0 ? "#" : acc < 0 ? "b" : "");
        }

        public static string IdentifyChord(HashSet<int> pitches)
        {
            if (pitches.Count == 0) return "FREESTYLE";
            string[] names = { "C", "C#", "D", "Eb", "E", "F", "F#", "G", "Ab", "A", "Bb", "B" };
            if (pitches.Count == 1) return names[pitches.First()];

            (int[] shape, string suffix)[] shapes =
            {
                (new[] { 0, 4, 7 }, ""), (new[] { 0, 3, 7 }, "m"), (new[] { 0, 4, 7, 10 }, "7"),
                (new[] { 0, 3, 7, 10 }, "m7"), (new[] { 0, 4, 7, 11 }, "maj7"), (new[] { 0, 2, 4, 7 }, "add9"),
                (new[] { 0, 5, 7 }, "sus4"), (new[] { 0, 2, 7 }, "sus2"), (new[] { 0, 7 }, "5"),
                (new[] { 0, 3, 6 }, "dim"), (new[] { 0, 4, 8 }, "aug"), (new[] { 0, 3, 6, 9 }, "dim7"),
                (new[] { 0, 3, 6, 10 }, "m7b5"),
            };
            foreach (int root in pitches.OrderBy(p => p))
            {
                var intervals = pitches.Select(p => (p - root + 12) % 12).OrderBy(i => i).ToArray();
                foreach (var (shape, suffix) in shapes)
                    if (intervals.SequenceEqual(shape)) return names[root] + suffix;
            }
            return "FREESTYLE";
        }

        // ------------------------------------------------------------------
        //  JSON -> events and decorations
        // ------------------------------------------------------------------
        public static LevelData LoadLevel(string? json, Chart chart, Func<string?, Raylib_cs.Texture2D> textureLookup)
        {
            var data = new LevelData();
            if (string.IsNullOrWhiteSpace(json)) return data;

            JsonObject? root;
            try { root = JsonNode.Parse(json, null, LevelJson.Document) as JsonObject; }
            catch (Exception ex) { data.Warnings.Add("Level JSON is invalid: " + ex.Message); return data; }
            if (root == null) { data.Warnings.Add("Level JSON must be an object."); return data; }

            // Read the header without the events, then each event on its own so one bad event can't break the level.
            var header = (JsonObject)root.DeepClone();
            header.Remove("Events");
            try { data.Config = header.Deserialize<LevelConfig>(LevelJson.Read) ?? new LevelConfig(); }
            catch (Exception ex) { data.Warnings.Add("Level header: " + ex.Message); }

            var events = new List<LevelEvent>();
            if (root["Events"] is JsonArray arr)
            {
                for (int i = 0; i < arr.Count; i++)
                {
                    try
                    {
                        var evt = arr[i]?.Deserialize<LevelEvent>(LevelJson.Read);
                        if (evt == null) continue;
                        evt.SourceIndex = i;
                        events.Add(evt);
                    }
                    catch (Exception ex) { data.Warnings.Add($"Event {i}: {ex.Message}"); }
                }
            }
            data.Config.Events = events;

            foreach (var evt in events)
            {
                try { ResolveTiming(evt, chart); }
                catch (Exception ex) { data.Warnings.Add($"Event {evt.SourceIndex} timing: {ex.Message}"); continue; }

                switch (evt.EventType)
                {
                    case "SpawnDecoration":
                    case "SpawnArrow":
                    case "SpawnText":
                        data.Decorations.Add(CreateDecoration(evt, textureLookup));
                        break;
                    case "AnimateDecoration":
                    case "AnimateColor":
                    case "PulseColor":
                    case "ToggleBassClef":
                        data.Events.Add(new GameEvent { Data = evt });
                        break;
                    default:
                        data.Warnings.Add($"Event {evt.SourceIndex}: unknown EventType '{evt.EventType}'");
                        break;
                }
                data.LastEventTime = Math.Max(data.LastEventTime, evt.TimeSeconds + evt.FadeInSec + evt.HoldSec + evt.FadeOutSec);
            }

            // Stable sort keeps the JSON order for events on the same beat.
            data.Events = data.Events.OrderBy(e => e.Data.TimeSeconds).ToList();
            return data;
        }

        static void ResolveTiming(LevelEvent evt, Chart chart)
        {
            if (evt.Bar.HasValue)
                evt.TimeSeconds = chart.BarBeatToSeconds(evt.Bar.Value, evt.Beat ?? 1.0);

            if (evt.TargetBar.HasValue)
                evt.TargetTimeSeconds = chart.BarBeatToSeconds(evt.TargetBar.Value, evt.TargetBeat ?? 1.0);

            double fi = evt.FadeInBeats ?? 0, ho = evt.HoldBeats ?? 0, fo = evt.FadeOutBeats ?? 0;
            if (fi > 0 || ho > 0 || fo > 0)
            {
                float t0 = evt.TimeSeconds;
                evt.FadeInSec = chart.BeatsAfter(t0, fi);
                evt.HoldSec = chart.BeatsAfter(t0, fi + ho) - evt.FadeInSec;
                evt.FadeOutSec = chart.BeatsAfter(t0, fi + ho + fo) - evt.FadeInSec - evt.HoldSec;
            }
            if (evt.DurationBeats is > 0)
                evt.Duration = chart.BeatsAfter(evt.TimeSeconds, evt.DurationBeats.Value);

            if (evt.LeaveBar.HasValue)
                evt.DespawnSec = chart.BarBeatToSeconds(evt.LeaveBar.Value, evt.LeaveBeat ?? 1.0);
        }

        /// <summary>StaffStep is converted to a Y offset exactly as before (16px per step, -40 so it hovers above the note).</summary>
        public static float StaffStepToYOffset(int step) => step * 16f - 40f;

        static Decoration CreateDecoration(LevelEvent evt, Func<string?, Raylib_cs.Texture2D> textureLookup)
        {
            bool isText = evt.EventType == "SpawnText";
            float y = (evt.YOffset ?? 0f) + (evt.StaffStep.HasValue ? StaffStepToYOffset(evt.StaffStep.Value) : 0f);
            float x = evt.XOffset ?? 0f;
            float scale = evt.Scale ?? 1f, rot = evt.Rotation ?? 0f;
            var tint = Ui.ParseHex(evt.ColorHex, Raylib_cs.Color.White);

            return new Decoration
            {
                ID = string.IsNullOrWhiteSpace(evt.ID) ? Guid.NewGuid().ToString("N")[..8] : evt.ID!,
                SourceIndex = evt.SourceIndex,
                FollowStave = evt.FollowStave ?? false,
                IsStatic = evt.IsStatic ?? false,
                SpawnTime = evt.TimeSeconds,
                DespawnTime = evt.DespawnSec,
                FadeInSec = evt.FadeInSec,
                FadeOutSec = evt.FadeOutSec,
                Texture = isText ? default : textureLookup(evt.TextureName),
                IsText = isText,
                Text = (evt.Text ?? "").Replace("\\n", "\n"),
                FontSize = Math.Max(1, evt.FontSize ?? 30),
                OutlineThickness = Math.Max(0, evt.OutlineThickness ?? 0),
                OutlineColor = Ui.ParseHex(evt.OutlineColorHex, Raylib_cs.Color.Black),
                IsBold = evt.IsBold ?? false,
                IsUnderlined = evt.IsUnderlined ?? false,
                XOffset = x, YOffset = y, Scale = scale, Rotation = rot, Tint = tint,
                SpinSpeed = evt.SpinSpeed ?? 0f, BobSpeed = evt.BobSpeed ?? 0f, BobAmplitude = evt.BobAmplitude ?? 0f,
                StartXOffset = x, TargetXOffset = x, StartYOffset = y, TargetYOffset = y,
                StartScale = scale, TargetScale = scale, StartRot = rot, TargetRot = rot,
                StartColor = tint, TargetColor = tint,
            };
        }

        /// <summary>Reads just the song name/artist/genre for the level preview panel.</summary>
        public static LevelConfig? PeekConfig(string jsonPath)
        {
            if (!File.Exists(jsonPath)) return null;
            try
            {
                var node = JsonNode.Parse(File.ReadAllText(jsonPath), null, LevelJson.Document) as JsonObject;
                if (node == null) return null;
                node.Remove("Events");
                return node.Deserialize<LevelConfig>(LevelJson.Read);
            }
            catch { return null; }
        }
    }
}