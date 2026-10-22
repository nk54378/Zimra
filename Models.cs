using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Raylib_cs;

namespace RhythmGameUI
{
    enum GameState { MainMenu, LevelSelect, WorldSelect, LevelList, Playing, EndScreen, Cutscene }
    enum SymbolType { Whole, Half, Quarter, Eighth, Sixteenth }
    enum Judgement { Perfect, Good, Okay, Miss }

    // ------------------------------------------------------------------
    //  JSON level format (unchanged field names, so existing levels load)
    // ------------------------------------------------------------------
    class LevelEvent
    {
        public float TimeSeconds { get; set; }
        public string? EventType { get; set; }
        public string? ID { get; set; }
        public string? TextureName { get; set; }
        public bool? FollowStave { get; set; }

        // Musical position (overrides TimeSeconds when present)
        public int? Bar { get; set; }
        public double? Beat { get; set; }
        public int? StaffStep { get; set; }

        public float? XOffset { get; set; }
        public float? YOffset { get; set; }
        public float? Scale { get; set; }
        public float? Rotation { get; set; }
        public string? ColorHex { get; set; }
        public float Duration { get; set; }
        public bool? ToggleState { get; set; }

        // Static spawning and lifetimes
        public bool? IsStatic { get; set; }
        public int? LeaveBar { get; set; }
        public double? LeaveBeat { get; set; }

        // Beat-synced durations
        public double? DurationBeats { get; set; }
        public double? FadeInBeats { get; set; }
        public double? HoldBeats { get; set; }
        public double? FadeOutBeats { get; set; }

        // Continuous animation
        public float? SpinSpeed { get; set; }
        public float? BobSpeed { get; set; }
        public float? BobAmplitude { get; set; }

        // Text and colour targets
        public string? Target { get; set; }
        public string? Text { get; set; }
        public int? FontSize { get; set; }
        public int? OutlineThickness { get; set; }
        public string? OutlineColorHex { get; set; }
        public bool? IsBold { get; set; }
        public bool? IsUnderlined { get; set; }

        // Individual object targeting
        public int? TargetBar { get; set; }
        public double? TargetBeat { get; set; }
        public int? TargetStaffStep { get; set; }
        public string? TargetID { get; set; }

        // ---- Calculated at load time (never written back to JSON) ----
        [JsonIgnore] public float TargetTimeSeconds;
        [JsonIgnore] public float FadeInSec, HoldSec, FadeOutSec;
        [JsonIgnore] public float DespawnSec = -1f;
        [JsonIgnore] public int SourceIndex = -1;
    }

    class LevelConfig
    {
        public string? SongName { get; set; }
        public string? Author { get; set; }
        public string? Genre { get; set; }
        public string? InitialBackground { get; set; }
        public string? InitialBackgroundColorHex { get; set; }
        public string? InitialStaveColorHex { get; set; }
        public string? InitialNoteColorHex { get; set; }
        public float? AudioSyncOffset { get; set; }
        public List<LevelEvent>? Events { get; set; }
    }

    /// <summary>Lenient JSON settings shared by the game and the editor.</summary>
    static class LevelJson
    {
        public static readonly JsonSerializerOptions Read = new()
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            Converters = { new LenientIntConverter() }
        };

        public static readonly JsonDocumentOptions Document = new()
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        };

        /// <summary>Accepts 2, 2.0 and "2" for integer fields (hand-written JSON often mixes them).</summary>
        sealed class LenientIntConverter : JsonConverter<int>
        {
            public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                if (reader.TokenType == JsonTokenType.String && double.TryParse(reader.GetString(),
                        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double s))
                    return (int)Math.Round(s);
                if (reader.TokenType == JsonTokenType.Number)
                    return reader.TryGetInt32(out int i) ? i : (int)Math.Round(reader.GetDouble());
                throw new JsonException("Expected a number.");
            }

            public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
        }
    }

    class GameEvent
    {
        public LevelEvent Data { get; set; } = new LevelEvent();
        public bool HasTriggered;
    }

    // ------------------------------------------------------------------
    //  Runtime objects
    // ------------------------------------------------------------------
    class Decoration
    {
        public string ID = "";
        public int SourceIndex = -1;            // Index of the event in the JSON "Events" array
        public float SpawnTime;
        public Texture2D Texture;
        public float XOffset, YOffset, Scale = 1f, Rotation;
        public Color Tint = Color.White;
        public bool FollowStave;

        public bool IsStatic;
        public float DespawnTime = -1f;          // -1 = never despawns
        public float FadeInSec, FadeOutSec;

        public float SpinSpeed, BobSpeed, BobAmplitude;

        // AnimateDecoration tween
        public bool IsAnimating;
        public float AnimStartTime, AnimDuration;
        public float StartXOffset, TargetXOffset, StartYOffset, TargetYOffset;
        public float StartScale, TargetScale, StartRot, TargetRot;
        public Color StartColor, TargetColor;

        // Text
        public bool IsText;
        public string Text = "";
        public int FontSize = 30;
        public int OutlineThickness;
        public Color OutlineColor = Color.Black;
        public bool IsBold, IsUnderlined;

        public ColorTracker? CustomColor;

        // Filled in every frame by the renderer so the editor can click on things
        public Rectangle LastBounds;
        public bool WasDrawn;
    }

    /// <summary>
    /// Colour with two independent layers: a base colour (changed by AnimateColor)
    /// and a pulse overlay (PulseColor) that always returns to the live base colour.
    /// All times are absolute song times, so events can be replayed when seeking.
    /// </summary>
    class ColorTracker
    {
        public Color Current { get; private set; }

        Color baseFrom, baseTo;
        float baseStart, baseDuration;

        Color pulseOrigin, pulseColor;
        float pulseStart, fadeIn, hold, fadeOut;
        bool pulsing;

        public ColorTracker(Color initial)
        {
            baseFrom = baseTo = Current = initial;
        }

        public void AnimateTo(Color target, float startTime, float duration)
        {
            baseFrom = BaseAt(startTime);
            baseTo = target;
            baseStart = startTime;
            baseDuration = Math.Max(0f, duration);
        }

        public void Pulse(Color color, float startTime, float inSec, float holdSec, float outSec)
        {
            pulseOrigin = Evaluate(startTime);
            pulseColor = color;
            pulseStart = startTime;
            fadeIn = Math.Max(0f, inSec);
            hold = Math.Max(0f, holdSec);
            fadeOut = Math.Max(0f, outSec);
            pulsing = true;
        }

        public void Update(float time) => Current = Evaluate(time);

        Color BaseAt(float time)
        {
            if (baseDuration <= 0f) return time >= baseStart ? baseTo : baseFrom;
            float t = Math.Clamp((time - baseStart) / baseDuration, 0f, 1f);
            return Lerp(baseFrom, baseTo, t);
        }

        Color Evaluate(float time)
        {
            Color b = BaseAt(time);
            if (!pulsing) return b;

            float e = time - pulseStart;
            if (e < 0f) return b;
            if (e < fadeIn) return Lerp(pulseOrigin, pulseColor, e / fadeIn);
            e -= fadeIn;
            if (e < hold) return pulseColor;
            e -= hold;
            if (e < fadeOut) return Lerp(pulseColor, b, e / fadeOut);
            return b;
        }

        public static Color Lerp(Color a, Color b, float t)
        {
            t = Math.Clamp(t, 0f, 1f);
            return new Color(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t),
                (int)(a.A + (b.A - a.A) * t));
        }
    }

    class CompingZone
    {
        public float StartTime, EndTime;
        public HashSet<int> PitchClasses = new();
        public string ChordName = "FREESTYLE";
        public bool IsBass;
        /// <summary>The chord's notes (one per pitch, from the MIDI) with a label like "F#", used to draw the zone.</summary>
        public List<(MusicSymbol Note, string Label)> Tones = new();

        // Runtime state (reset by Chart.ResetRuntimeState)
        public float AccumulatedHoldTime;
        public bool HasStruck, MissedInitialStrike, IsScored, EnemyResolved;
        public float InitialStrikeDiff = 999f;
        public float StrikeFlashTimer;
        public bool IsBeingHeld;
    }

    class MusicSymbol
    {
        public float TimeSeconds;
        public int MidiPitch;
        public SymbolType Type;
        public bool IsDotted, IsRest;
        public float DurationSeconds;

        public int BeamGroupID = -1;
        public int ChordID = -1;
        public bool IsStemDown;

        public int TrebleStep, BassStep;
        public int AccidentalToDraw;   // 0 none, 1 sharp, -1 flat, 2 natural
        public long BeatID;

        public bool IsGhost, HasTie;

        // Staff layout
        public bool OnBassStaff;            // belongs to the bass stave (MIDI channel 3/4, or below middle C on channel 1)
        public bool HideWhenSingleStave;    // treble rest that overlaps bass notes: only shown once the bass clef is on
        public bool DrawsStem = true;       // false for the other notes of a chord, which share one stem
        public MusicSymbol? StemFrom;       // for the note that draws a chord's stem: the chord note the stem starts at
        public float TieDurationSeconds;

        // Runtime state
        public bool IsHit, IsMissed, Skipped, EnemyResolved;
        public ColorTracker? CustomColor;

        public bool IsPlayable => !IsRest && !IsGhost;
    }

    class BeamGroup
    {
        public List<MusicSymbol> Notes = new();      // note nearest the beam, one per chord
        public List<MusicSymbol> FarNotes = new();   // note furthest from the beam, one per chord
        public bool HasSixteenths;
        public bool StemDown;
    }

    class Particle
    {
        public Vector2 Pos, Vel;
        public float Life, MaxLife, Size, Rot, RotVel, Drag = 2.5f, Gravity;
        public Color Col;
        public bool Square;
    }
}
