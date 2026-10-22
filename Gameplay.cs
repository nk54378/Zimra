using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Raylib_cs;

namespace RhythmGameUI
{
    /// <summary>Everything that happens while a level is playing.</summary>
    partial class Program
    {
        // ---------------- Level data ----------------
        static Chart chart = new();
        static LevelData level = new();
        static string currentMidiPath = "", currentJsonPath = "", loadedChartPath = "";
        static float levelTimer, levelEndTime;

        // ---------------- Layout ----------------
        const int BaseHitZoneX = 220;
        const int StepSpacing = 16;
        const float PixelsPerBeat = 250f;
        const float EnemySpeedMultiplier = 1.5f;
        const float EnemyCombatOffset = 250f;
        const float NoteScale = 0.7f, RestScale = 0.8f, ClefScale = 0.4f, AccScale = 0.6f;
        static int hitZoneX = BaseHitZoneX;
        static float centerStaffY, trebleY, bassY, floorY;

        // ---------------- Timing windows (seconds) ----------------
        const float HitWindow = 0.25f, PerfectWindow = 0.05f, GoodWindow = 0.12f;

        // ---------------- Score & health ----------------
        static int score, combo, maxCombo;
        static readonly int[] judgeCounts = new int[4];
        static float accuracySum, accuracyCount;
        static float displayScore;
        static float health = 100f, displayHealth = 100f;
        const float MaxHealth = 100f;
        static bool isGameOver, isPaused, autoplay;
        static float overlayAlpha;

        static float Accuracy => accuracyCount > 0 ? accuracySum / accuracyCount * 100f : 100f;

        // ---------------- Staff state ----------------
        static bool bassClefTarget;
        static float bassClefTransition;
        static ColorTracker bgColor = new(Color.DarkGray), staveColor = new(Color.White), noteColor = new(Color.White);
        static Texture2D currentBackground;

        // ---------------- Audio ----------------
        static Music levelMusic;
        static bool hasLevelMusic, musicStarted, musicPaused;
        static string loadedMusicPath = "";
        static float audioSyncOffset;

        // ---------------- Input ----------------
        static readonly HashSet<int> heldMidiPitches = new(), heldLaptopPitches = new();
        static readonly HashSet<int> heldPitches = new();
        static readonly HashSet<int> trebleHeldClasses = new(), bassHeldClasses = new();
        static int currentHandSplit = 60;   // notes below this count as the left hand this frame
        static bool twoHandedNow;           // a bass zone and a treble zone are both active

        /// <summary>
        /// Laptop keyboard layout. Number row and home row are the white keys C4..F5 (as before),
        /// the Z row is C3..B3, and W E T Y U O P are the black keys (same layout as Logic's Musical Typing).
        /// </summary>
        static readonly (KeyboardKey key, int pitch)[] LaptopKeys =
        {
            (KeyboardKey.One, 60), (KeyboardKey.Two, 62), (KeyboardKey.Three, 64), (KeyboardKey.Four, 65), (KeyboardKey.Five, 67),
            (KeyboardKey.Six, 69), (KeyboardKey.Seven, 71), (KeyboardKey.Eight, 72), (KeyboardKey.Nine, 74), (KeyboardKey.Zero, 76), (KeyboardKey.Minus, 77),
            (KeyboardKey.A, 60), (KeyboardKey.S, 62), (KeyboardKey.D, 64), (KeyboardKey.F, 65), (KeyboardKey.G, 67), (KeyboardKey.H, 69),
            (KeyboardKey.J, 71), (KeyboardKey.K, 72), (KeyboardKey.L, 74), (KeyboardKey.Semicolon, 76), (KeyboardKey.Apostrophe, 77),
            (KeyboardKey.W, 61), (KeyboardKey.E, 63), (KeyboardKey.T, 66), (KeyboardKey.Y, 68), (KeyboardKey.U, 70), (KeyboardKey.O, 73), (KeyboardKey.P, 75),
            (KeyboardKey.Z, 48), (KeyboardKey.X, 50), (KeyboardKey.C, 52), (KeyboardKey.V, 53), (KeyboardKey.B, 55), (KeyboardKey.N, 57), (KeyboardKey.M, 59),
        };

        // ---------------- Feedback ----------------
        static string judgementText = "";
        static Color judgementColor;
        static float judgementTimer;
        const float JudgementDuration = 0.7f;
        static float comboPop, comboBreakTimer, hitZoneFlash, edgeFlash;
        static Color hitZoneFlashColor = Color.White;
        static float attackTimer, hurtTimer, gameBeatPulse;
        static float pinkAttackTimer, pinkHurtTimer;   // the bass-stave character
        static readonly List<(Vector2 a, Vector2 b, float life, Color col)> slashes = new();

        // ---------------- Editor access ----------------
        public static float SongTime => levelTimer;
        public static Chart CurrentChart => chart;
        public static LevelData CurrentLevel => level;
        public static int HitZoneX => hitZoneX;
        public static float StaffAnchorY(bool followStave) => followStave ? trebleY : centerStaffY;
        public static float Scroll => PixelsPerBeat / Math.Max(0.01f, chart.SecondsPerBeat(levelTimer));
        public static float TimeToX(float t) => hitZoneX + (float)(chart.BeatPosition(t) - chart.BeatPosition(levelTimer)) * PixelsPerBeat;
        public static float XToTime(float x) => levelTimer + ((x - hitZoneX) / PixelsPerBeat) * chart.SecondsPerBeat(levelTimer);
        public static float LevelEndTime => levelEndTime;
        public static float LeadInTime => chart.LeadIn;
        public static string CurrentJsonPath => currentJsonPath;

        // =====================================================================
        //  Loading
        // =====================================================================
        static void StartLevel(string midiPath, string? previewJson = null, float? startAt = null)
        {
            string jsonPath = Path.ChangeExtension(midiPath, ".json");

            // Restarting while the editor has unsaved edits keeps those edits instead of reloading the file.
            if (previewJson == null && editor.HasLevel && editor.Dirty && PathsEqual(editor.JsonPath, jsonPath))
                previewJson = editor.GetPreviewJson();

            var warnings = new List<string>();
            if (previewJson == null || midiPath != loadedChartPath)
            {
                chart = LevelLoader.LoadChart(midiPath, warnings);
                loadedChartPath = midiPath;
            }
            else chart.ResetRuntimeState();

            currentMidiPath = midiPath;
            currentJsonPath = jsonPath;
            if (previewJson == null) editor.OpenLevel(jsonPath);

            string? json = previewJson ?? (File.Exists(jsonPath) ? File.ReadAllText(jsonPath) : null);
            level = LevelLoader.LoadLevel(json, chart, n => GetTexture(n));
            level.Warnings.InsertRange(0, warnings);
            // Stem directions depend on when the bass clef is showing, which comes from the level's JSON events.
            LevelLoader.LayoutStems(chart, BassClefOnAt);
            foreach (var w in level.Warnings) Console.WriteLine("[Level] " + w);

            // Reset run state
            score = combo = maxCombo = 0;
            Array.Clear(judgeCounts);
            accuracySum = accuracyCount = 0;
            displayScore = 0;
            health = displayHealth = MaxHealth;
            isGameOver = isPaused = false;
            overlayAlpha = 0f;
            judgementTimer = comboPop = comboBreakTimer = hitZoneFlash = edgeFlash = attackTimer = hurtTimer = 0f;
            pinkAttackTimer = pinkHurtTimer = 0f;
            slashes.Clear();
            heldMidiPitches.Clear();
            while (MidiInput.Queue.TryDequeue(out _)) { }
            Fx.Clear();
            bassClefTarget = false;
            bassClefTransition = 0f;

            var cfg = level.Config;
            bgColor = new ColorTracker(Ui.ParseHex(cfg.InitialBackgroundColorHex, Color.DarkGray));
            staveColor = new ColorTracker(Ui.ParseHex(cfg.InitialStaveColorHex, Color.White));
            noteColor = new ColorTracker(Ui.ParseHex(cfg.InitialNoteColorHex, Color.White));
            currentBackground = GetTexture(cfg.InitialBackground ?? "bg_default", false);
            audioSyncOffset = cfg.AudioSyncOffset ?? 0f;

            // Make room for the key signature so it never sits under the hit zone.
            int keyCount = Math.Abs(chart.KeySignature);
            hitZoneX = BaseHitZoneX + (keyCount > 0 ? keyCount * 16 + 10 : 0);

            float lastZone = chart.Zones.Count > 0 ? chart.Zones.Max(z => z.EndTime) : 0f;
            levelEndTime = Math.Max(Math.Max(chart.LastMusicTime, lastZone), level.LastEventTime) + 2f;

            if (previewJson == null || midiPath != loadedMusicPath) LoadLevelMusic(midiPath);
            StopLevelMusic();

            levelTimer = startAt ?? -chart.LeadIn;
            if (startAt.HasValue) FastForwardTo(startAt.Value);
        }

        /// <summary>Whether the bass clef (second stave) is on at time t, according to the level's ToggleBassClef events.</summary>
        static bool BassClefOnAt(float t)
        {
            bool on = false;
            foreach (var e in level.Events)   // sorted by time
            {
                if (e.Data.TimeSeconds > t + 0.01f) break;
                if (e.Data.EventType == "ToggleBassClef") on = e.Data.ToggleState ?? false;
            }
            return on;
        }

        static bool PathsEqual(string a, string b) =>
            string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

        /// <summary>Puts the level into the exact state it would be in at time t (used by the editor to preview from anywhere).</summary>
        static void FastForwardTo(float t)
        {
            foreach (var s in chart.Symbols)
                if (s.IsPlayable && s.TimeSeconds < t - 0.01f) { s.Skipped = true; s.EnemyResolved = true; }
            foreach (var z in chart.Zones)
                if (z.StartTime < t) { z.IsScored = z.HasStruck = z.EnemyResolved = true; }
            foreach (var e in level.Events)
                if (e.Data.TimeSeconds <= t) { e.HasTriggered = true; TriggerEvent(e.Data); }

            bassClefTransition = bassClefTarget ? 1f : 0f;
            bgColor.Update(t); staveColor.Update(t); noteColor.Update(t);
        }

        static void LoadLevelMusic(string midiPath)
        {
            UnloadLevelMusic();
            string? audio = FindLevelAudio(midiPath);
            if (audio != null)
            {
                levelMusic = Raylib.LoadMusicStream(audio);
                levelMusic.Looping = false;
                hasLevelMusic = levelMusic.FrameCount > 0;
                loadedMusicPath = midiPath;
            }
        }

        /// <summary>The song for a level: level1.mid -> level1.mp3 (or .ogg / .wav).</summary>
        static string? FindLevelAudio(string midiPath)
        {
            foreach (var ext in new[] { ".mp3", ".ogg", ".wav" })
            {
                string path = Path.ChangeExtension(midiPath, ext);
                if (File.Exists(path)) return path;
            }
            return null;
        }

        static void UnloadLevelMusic()
        {
            if (hasLevelMusic) Raylib.UnloadMusicStream(levelMusic);
            hasLevelMusic = musicStarted = musicPaused = false;
            loadedMusicPath = "";
        }

        static void StopLevelMusic()
        {
            if (hasLevelMusic) Raylib.StopMusicStream(levelMusic);
            musicStarted = musicPaused = false;
        }

        // =====================================================================
        //  Frame
        // =====================================================================
        static void UpdateAndDrawGameplay(float frameDt)
        {
            // Pause menu
            if (!isGameOver && !isTransitioning && !editor.WantsKeyboard && Raylib.IsKeyPressed(KeyboardKey.Escape))
                isPaused = !isPaused;

            bool editorFrozen = editor.IsOpen && editor.Paused;
            bool running = !isPaused && !isGameOver && !editorFrozen && !isTransitioning;
            float dt = running ? frameDt : 0f;

            levelTimer += dt;
            // The song keeps going through the wipe at the end of the level (it carries on over the results screen)
            bool endingWipe = isTransitioning && !isGameOver && !isPaused && levelTimer > levelEndTime;
            UpdateLevelMusic(running || endingWipe, dt);

            screenLayout();
            gameBeatPulse = BeatPulseAt(levelTimer);

            if (running) HandleInput();
            else DrainInput();

            TriggerDueEvents();
            UpdateNotesAndZones(dt);

            bgColor.Update(levelTimer); staveColor.Update(levelTimer); noteColor.Update(levelTimer);
            bassClefTransition = Ease.Damp(bassClefTransition, bassClefTarget ? 1f : 0f, 4f, dt);

            displayScore = Ease.Damp(displayScore, score, 12f, frameDt);
            displayHealth = Ease.Damp(displayHealth, health, 10f, frameDt);
            if (running) TickFeedback(dt);
            Fx.Update(dt);

            // ---------- Draw ----------
            DrawBackground();
            var cam = new Camera2D { Zoom = 1f, Offset = Fx.ShakeOffset };
            Raylib.BeginMode2D(cam);
            DrawZones(dt);
            DrawStaff();
            DrawDecorations();
            DrawBeams();
            DrawCombatLane();
            DrawSymbols();
            DrawHeldKeys();
            DrawZonePills();   // on top of the hit-zone overlay and held keys, so the note names stay readable
            DrawSlashes();
            Fx.Draw();
            Raylib.EndMode2D();

            DrawHud();
            DrawCountdown();
            DrawOverlays(frameDt);

            if (!editor.IsOpen && !isGameOver && !isTransitioning && levelTimer > levelEndTime)
                BeginTransition(() => FinishLevel(false));
        }

        static void screenLayout()
        {
            centerStaffY = screenHeight / 2f - 40;
            trebleY = centerStaffY - bassClefTransition * 100f;
            bassY = centerStaffY + bassClefTransition * 100f;
            floorY = screenHeight - 120;
        }

        static float BeatPulseAt(float t)
        {
            double b = chart.BeatPosition(t);
            float frac = (float)(b - Math.Floor(b));
            return MathF.Pow(1f - frac, 3f);
        }

        static void UpdateLevelMusic(bool running, float dt)
        {
            if (!hasLevelMusic) return;
            float songPos = levelTimer - audioSyncOffset;

            if (running && !musicStarted && songPos >= 0f)
            {
                Raylib.PlayMusicStream(levelMusic);
                if (songPos > 0.03f) Raylib.SeekMusicStream(levelMusic, songPos);
                musicStarted = true;
                musicPaused = false;
            }
            if (!musicStarted) return;

            if (!running && !musicPaused) { Raylib.PauseMusicStream(levelMusic); musicPaused = true; }
            else if (running && musicPaused) { Raylib.ResumeMusicStream(levelMusic); musicPaused = false; }

            Raylib.UpdateMusicStream(levelMusic);

            // Keep the notes locked to the audio clock so long songs don't drift.
            if (running && Raylib.IsMusicStreamPlaying(levelMusic))
            {
                float audioTime = Raylib.GetMusicTimePlayed(levelMusic) + audioSyncOffset;
                float drift = audioTime - levelTimer;
                if (Math.Abs(drift) > 0.15f) levelTimer = audioTime;
                else levelTimer += drift * Math.Min(1f, dt * 4f);
            }
        }

        // =====================================================================
        //  Input
        // =====================================================================
        static void DrainInput()
        {
            while (MidiInput.Queue.TryDequeue(out var e))
                if (e.pressed) heldMidiPitches.Add(e.pitch); else heldMidiPitches.Remove(e.pitch);
            heldLaptopPitches.Clear();
            RefreshHeld();
        }

        static void RefreshHeld()
        {
            heldPitches.Clear();
            heldPitches.UnionWith(heldMidiPitches);
            heldPitches.UnionWith(heldLaptopPitches);
        }

        static void HandleInput()
        {
            heldLaptopPitches.Clear();

            while (MidiInput.Queue.TryDequeue(out var e))
            {
                if (e.pressed) { heldMidiPitches.Add(e.pitch); OnNotePressed(e.pitch); }
                else heldMidiPitches.Remove(e.pitch);
            }

            if (!editor.WantsKeyboard)
            {
                foreach (var (key, pitch) in LaptopKeys)
                {
                    if (Raylib.IsKeyDown(key)) heldLaptopPitches.Add(pitch);
                    if (Raylib.IsKeyPressed(key)) OnNotePressed(pitch);
                }

                // Spacebar hits whatever note is closest (handy for testing timing)
                if (!editor.IsOpen && Raylib.IsKeyPressed(KeyboardKey.Space))
                {
                    var note = ClosestNote(_ => true);
                    if (note != null) HitNote(note);
                }
            }

            if (autoplay) Autoplay();
            RefreshHeld();
        }

        static void Autoplay()
        {
            foreach (var s in chart.Symbols)
            {
                if (s.TimeSeconds > levelTimer + 0.02f) break;
                if (s.IsPlayable && !s.IsHit && !s.IsMissed && !s.Skipped && s.TimeSeconds <= levelTimer) HitNote(s);
            }
            foreach (var z in chart.Zones)
                if (levelTimer >= z.StartTime && levelTimer <= z.EndTime)
                    foreach (int pc in z.PitchClasses) heldLaptopPitches.Add((z.IsBass ? 48 : 72) + pc); // Force bass low, treble high
        }

        static MusicSymbol? ClosestNote(Func<MusicSymbol, bool> match)
        {
            MusicSymbol? best = null;
            float bestDiff = HitWindow;
            foreach (var s in chart.Symbols)
            {
                if (s.TimeSeconds > levelTimer + HitWindow) break;
                if (!s.IsPlayable || s.IsHit || s.IsMissed || s.Skipped) continue;
                float diff = Math.Abs(s.TimeSeconds - levelTimer);
                if (diff <= bestDiff && match(s)) { best = s; bestDiff = diff; }
            }
            return best;
        }

        // =====================================================================
        //  Two-handed chord zones
        // =====================================================================

        /// <summary>
        /// The zone each hand should be playing right now: the most recent zone that has started (or is about to,
        /// within the hit window) and hasn't finished. Taking the most recent one matters for back-to-back chords,
        /// where the previous zone is still inside its end window when the next one begins.
        /// </summary>
        static CompingZone? ActiveZone(bool bass)
        {
            CompingZone? best = null;
            foreach (var z in chart.Zones)
            {
                if (z.IsBass != bass) continue;
                if (levelTimer < z.StartTime - HitWindow || levelTimer > z.EndTime) continue;
                if (best == null || z.StartTime > best.StartTime) best = z;
            }
            return best;
        }

        static readonly List<int> splitScratch = new();

        /// <summary>
        /// Returns a pitch where notes below it count as the left hand (bass zone) and notes at or above it count as
        /// the right hand (treble zone).
        ///
        /// Rather than guessing from the pitch classes alone (which fails as soon as both chords share a note, e.g. C
        /// in the bass and C major on top), this tries every possible place to split the held notes and keeps the
        /// one where both hands match their own chord best. Ties go to the widest gap between the hands, then to the
        /// split nearest middle C. It doesn't care about octaves, so it also works on the laptop keys.
        /// </summary>
        static int HandSplit(IEnumerable<int> held)
        {
            var bassZone = ActiveZone(true);
            var trebleZone = ActiveZone(false);
            if (bassZone != null && trebleZone == null) return 128;   // everything is left hand
            if (trebleZone != null && bassZone == null) return 0;     // everything is right hand
            if (bassZone == null || trebleZone == null) return 60;

            splitScratch.Clear();
            splitScratch.AddRange(held);
            splitScratch.Sort();
            int n = splitScratch.Count;
            if (n == 0) return 60;

            int bestSplit = 60, bestScore = -1, bestGap = -1, bestDist = int.MaxValue;
            // Split position k means notes [0, k) are left hand and [k, n) are right hand.
            for (int k = 0; k <= n; k++)
            {
                int score = DistinctMatches(0, k, bassZone) + DistinctMatches(k, n, trebleZone);
                int split = k == 0 ? splitScratch[0] : k == n ? splitScratch[n - 1] + 1 : splitScratch[k - 1] + 1;
                int gap = k == 0 || k == n ? 0 : splitScratch[k] - splitScratch[k - 1];
                int dist = Math.Abs(split - 60);
                if (score > bestScore || (score == bestScore && (gap > bestGap || (gap == bestGap && dist < bestDist))))
                {
                    bestScore = score; bestGap = gap; bestDist = dist; bestSplit = split;
                }
            }
            return bestSplit;
        }

        /// <summary>How many different pitch classes of the zone appear in splitScratch[from..to).</summary>
        static int DistinctMatches(int from, int to, CompingZone zone)
        {
            int seen = 0, count = 0;   // 12-bit mask of pitch classes already counted
            for (int i = from; i < to; i++)
            {
                int pc = splitScratch[i] % 12;
                if ((seen & (1 << pc)) != 0 || !zone.PitchClasses.Contains(pc)) continue;
                seen |= 1 << pc;
                count++;
            }
            return count;
        }

        static void OnNotePressed(int pitch)
        {
            bool hitSomething = false;

            // --- FIXED: Always check exact standard notes FIRST! ---
            var note = ClosestNote(s => s.MidiPitch == pitch);
            if (note != null) 
            { 
                HitNote(note); 
                hitSomething = true; 
            }

            // --- Check Chord Zones ---
            // Include the key being pressed: it isn't in heldPitches until the end of this frame.
            int pc = pitch % 12;
            int dynamicSplit = HandSplit(heldPitches.Append(pitch));
            bool isBassHand = pitch < dynamicSplit;

            // Try the hand the split says first. If that hand's chord doesn't contain the note, try the other hand,
            // so the very first key of a two-handed chord still counts even before the split has enough notes.
            var zone = StrikeableZone(isBassHand, pc) ?? StrikeableZone(!isBassHand, pc);
            if (zone != null)
            {
                StrikeZone(zone, Math.Abs(zone.StartTime - levelTimer));
                hitSomething = true;
            }

            if (hitSomething) return;

            // Wrong notes are forgiven anywhere near a chord zone (freestyle).
            bool nearZone = chart.Zones.Any(z => levelTimer >= z.StartTime - HitWindow && levelTimer <= z.EndTime + HitWindow);
            if (!nearZone)
            {
                BreakCombo();
                ShowJudgement("WRONG KEY", Theme.Miss);
            }
        }

        // =====================================================================
        //  Judging
        // =====================================================================
        static Judgement Grade(float diff) => diff <= PerfectWindow ? Judgement.Perfect : diff <= GoodWindow ? Judgement.Good : Judgement.Okay;

        static void HitNote(MusicSymbol note)
        {
            float diff = Math.Abs(note.TimeSeconds - levelTimer);
            var j = Grade(diff);
            note.IsHit = true;
            int[] points = { 300, 100, 50 };
            float[] heal = { 8f, 5f, 2f };
            Register(j, points[(int)j], heal[(int)j], j.ToString().ToUpper());

            // Effects at the note and on the enemy it belonged to
            Color c = Theme.For(j);
            var notePos = new Vector2(hitZoneX, NoteY(note));
            Fx.Burst(notePos, c, j == Judgement.Perfect ? 22 : 14, 700, 8);
            Fx.Ring(notePos, c, 20, 90, 0.35f, 8);
            hitZoneFlash = 1f; hitZoneFlashColor = c;

            if (!note.EnemyResolved)
            {
                note.EnemyResolved = true;
                var enemyPos = new Vector2(EnemyX(note.TimeSeconds), EnemyY(note) - 24);
                KillEnemy(enemyPos, c, 1f, BassHand(note.OnBassStaff));
            }
        }

        static CompingZone? StrikeableZone(bool bass, int pc)
        {
            foreach (var z in chart.Zones)
                if (z.IsBass == bass && !z.HasStruck && !z.MissedInitialStrike &&
                    Math.Abs(z.StartTime - levelTimer) <= HitWindow && z.PitchClasses.Contains(pc))
                    return z;
            return null;
        }

        static void StrikeZone(CompingZone zone, float diff)
        {
            zone.HasStruck = true;
            zone.InitialStrikeDiff = diff;
            zone.StrikeFlashTimer = 0.6f;
            Color c = Theme.For(Grade(diff));
            hitZoneFlash = 1f; hitZoneFlashColor = c;
            Fx.Ring(new Vector2(hitZoneX, (zone.IsBass ? bassY : trebleY) + 4 * StepSpacing), c, 40, 220, 0.45f, 12);
            if (!zone.EnemyResolved)
            {
                zone.EnemyResolved = true;
                KillEnemy(new Vector2(ZoneEnemyX(zone), floorY - 46), c, 1.8f, BassHand(zone.IsBass));
            }
        }

        /// <summary>
        /// The pink character handles the bass stave, but only while it's on screen (bass clef showing).
        /// On a single stave, the blue character plays everything.
        /// </summary>
        static bool BassHand(bool onBassStaff) => onBassStaff && bassClefTransition > 0.5f;

        static float PinkX => hitZoneX - 55;

        static void KillEnemy(Vector2 pos, Color c, float size, bool bassHand = false)
        {
            Fx.Burst(pos, c, (int)(18 * size), 650 * size, 9 * size, 0.6f, 900);
            Fx.Burst(pos, Color.White, (int)(8 * size), 400, 5, 0.4f);
            Fx.Ring(pos, c, 10, 70 * size, 0.3f, 6);
            if (bassHand)
            {
                // The pink slash starts a little higher so it arcs over the blue character
                slashes.Add((new Vector2(PinkX + 18, floorY - 40), pos, 0.14f, ColorTracker.Lerp(c, BassZoneTint, 0.5f)));
                pinkAttackTimer = 0.18f;
            }
            else
            {
                slashes.Add((new Vector2(hitZoneX + 18, floorY - 30), pos, 0.14f, c));
                attackTimer = 0.18f;
            }
        }

        static void Register(Judgement j, int points, float heal, string label)
        {
            judgeCounts[(int)j]++;
            accuracyCount++;
            accuracySum += j switch { Judgement.Perfect => 1f, Judgement.Good => 1f / 3f, Judgement.Okay => 1f / 6f, _ => 0f };

            if (j == Judgement.Miss)
            {
                BreakCombo();
            }
            else
            {
                score += points;
                combo++;
                maxCombo = Math.Max(maxCombo, combo);
                comboPop = 1f;
                health = Math.Min(MaxHealth, health + heal);
                if (combo % 50 == 0) { edgeFlash = 1f; Fx.Ring(new Vector2(110, screenHeight - 60), Theme.Accent, 30, 260, 0.6f, 10); }
            }
            ShowJudgement(label, Theme.For(j));
        }

        static void BreakCombo()
        {
            if (combo >= 10) { comboBreakTimer = 0.5f; Fx.Shake(6); }
            combo = 0;
        }

        static void ShowJudgement(string text, Color color)
        {
            judgementText = text;
            judgementColor = color;
            judgementTimer = JudgementDuration;
        }

        static void Damage(float amount)
        {
            if (editor.IsOpen || autoplay) return;   // no-fail while editing or watching autoplay
            health -= amount;
            if (health <= 0f && !isGameOver)
            {
                health = 0f;
                isGameOver = true;
                overlayAlpha = 0f;
                Fx.Shake(16);
            }
        }

        static void Hurt(bool bassHand = false)
        {
            if (bassHand) pinkHurtTimer = 0.35f; else hurtTimer = 0.35f;
            Fx.Shake(8);
            Fx.Burst(new Vector2(bassHand ? PinkX : hitZoneX, floorY - 25), Theme.Miss, 12, 400, 6, 0.4f);
        }

        // =====================================================================
        //  Timeline
        // =====================================================================
        static void TriggerDueEvents()
        {
            foreach (var e in level.Events)
            {
                if (e.HasTriggered) continue;
                if (levelTimer < e.Data.TimeSeconds) break;   // events are sorted by time
                e.HasTriggered = true;
                TriggerEvent(e.Data);
            }
        }

        static ColorTracker? FindColorTarget(LevelEvent e)
        {
            string target = (e.Target ?? "Background").Trim().ToLowerInvariant();
            switch (target)
            {
                case "background": return bgColor;
                case "stave": case "staff": return staveColor;
                case "notes": return noteColor;
                case "note":
                    if (!e.TargetBar.HasValue) return noteColor;
                    var note = chart.Symbols.FirstOrDefault(n => !n.IsRest && Math.Abs(n.TimeSeconds - e.TargetTimeSeconds) < 0.02f &&
                                                                 (!e.TargetStaffStep.HasValue || n.TrebleStep == e.TargetStaffStep.Value));
                    if (note == null) return null;
                    return note.CustomColor ??= new ColorTracker(noteColor.Current);
                case "decoration":
                    var dec = level.Decorations.FirstOrDefault(d => d.ID == e.TargetID);
                    if (dec == null) return null;
                    return dec.CustomColor ??= new ColorTracker(dec.Tint);
                default: return null;
            }
        }

        static void TriggerEvent(LevelEvent e)
        {
            switch (e.EventType)
            {
                case "ToggleBassClef":
                    bassClefTarget = e.ToggleState ?? false;
                    break;

                case "AnimateColor":
                case "PulseColor":
                    var tracker = FindColorTarget(e);
                    if (tracker == null) break;
                    var color = Ui.ParseHex(e.ColorHex, Color.White);
                    if (e.EventType == "PulseColor") tracker.Pulse(color, e.TimeSeconds, e.FadeInSec, e.HoldSec, e.FadeOutSec);
                    else tracker.AnimateTo(color, e.TimeSeconds, e.Duration);
                    break;

                case "AnimateDecoration":
                    var d = level.Decorations.FirstOrDefault(x => x.ID == (e.TargetID ?? e.ID));
                    if (d == null) break;
                    d.IsAnimating = true;
                    d.AnimStartTime = e.TimeSeconds;
                    d.AnimDuration = Math.Max(0.01f, e.Duration);
                    d.StartXOffset = d.XOffset; d.StartYOffset = d.YOffset;
                    d.StartScale = d.Scale; d.StartRot = d.Rotation; d.StartColor = d.Tint;
                    d.TargetXOffset = e.XOffset ?? d.XOffset;
                    d.TargetYOffset = e.YOffset ?? d.YOffset;
                    d.TargetScale = e.Scale ?? d.Scale;
                    d.TargetRot = e.Rotation ?? d.Rotation;
                    d.TargetColor = Ui.ParseHex(e.ColorHex, d.Tint);
                    break;
            }
        }

        static void UpdateNotesAndZones(float dt)
        {
            // Missed notes
            foreach (var s in chart.Symbols)
            {
                if (s.TimeSeconds > levelTimer) break;
                if (!s.IsPlayable || s.IsHit || s.IsMissed || s.Skipped) continue;
                if (levelTimer > s.TimeSeconds + HitWindow)
                {
                    s.IsMissed = true;
                    Register(Judgement.Miss, 0, 0, "MISS");
                    Damage(4f);
                }
            }

            // Enemies of missed notes reach the player
            foreach (var s in chart.Symbols)
            {
                if (s.TimeSeconds > levelTimer) break;
                if (s.IsMissed && !s.EnemyResolved && EnemyX(s.TimeSeconds) <= hitZoneX + 24)
                {
                    s.EnemyResolved = true;
                    Hurt(BassHand(s.OnBassStaff));
                }
            }

            // Chord zones: split the held notes between the hands, then check each zone against its own hand.
            int dynamicSplit = HandSplit(heldPitches);
            currentHandSplit = dynamicSplit;
            twoHandedNow = ActiveZone(true) != null && ActiveZone(false) != null;
            trebleHeldClasses.Clear(); bassHeldClasses.Clear();
            foreach (int p in heldPitches) (p < dynamicSplit ? bassHeldClasses : trebleHeldClasses).Add(p % 12);

            foreach (var z in chart.Zones)
            {
                var handClasses = z.IsBass ? bassHeldClasses : trebleHeldClasses;
                int required = Math.Min(z.PitchClasses.Count, 2);
                int matches = z.PitchClasses.Count(handClasses.Contains);
                float duration = z.EndTime - z.StartTime;

                // Only auto-strike long sustained chords (>= 1.2s). Short off-beat chops must be freshly struck!
                if (duration >= 1.2f && !z.HasStruck && !z.MissedInitialStrike && levelTimer >= z.StartTime && matches >= required)
                    StrikeZone(z, 0f);

                if (!z.HasStruck && !z.MissedInitialStrike && levelTimer > z.StartTime + HitWindow)
                {
                    z.MissedInitialStrike = true;
                    Damage(4f);
                }

                // For short chops, require HasStruck so holding keys down through rests doesn't count
                bool canHold = duration >= 1.2f || z.HasStruck;
                z.IsBeingHeld = canHold && levelTimer >= z.StartTime && levelTimer <= z.EndTime && matches >= required;
                if (z.IsBeingHeld) z.AccumulatedHoldTime += dt;
                if (z.StrikeFlashTimer > 0) z.StrikeFlashTimer -= dt;

                if (levelTimer > z.EndTime && !z.IsScored)
                {
                    z.IsScored = true;
                    float required_hold = duration * 0.3f;
                    if (z.MissedInitialStrike || z.AccumulatedHoldTime < required_hold)
                    {
                        Register(Judgement.Miss, 0, 0, "MISSED CHORD");
                        Damage(10f);
                        if (!z.EnemyResolved) { z.EnemyResolved = true; Hurt(BassHand(z.IsBass)); }
                    }
                    else
                    {
                        var j = Grade(z.InitialStrikeDiff);
                        int[] points = { 500, 200, 100 };
                        float[] heal = { 15f, 8f, 4f };
                        Register(j, points[(int)j], heal[(int)j], j.ToString().ToUpper() + " CHORD");
                    }
                }
            }
        }

        static void TickFeedback(float dt)
        {
            judgementTimer = Math.Max(0, judgementTimer - dt);
            comboPop = Math.Max(0, comboPop - dt * 6f);
            comboBreakTimer = Math.Max(0, comboBreakTimer - dt);
            hitZoneFlash = Math.Max(0, hitZoneFlash - dt * 5f);
            edgeFlash = Math.Max(0, edgeFlash - dt * 2.5f);
            attackTimer = Math.Max(0, attackTimer - dt);
            hurtTimer = Math.Max(0, hurtTimer - dt);
            pinkAttackTimer = Math.Max(0, pinkAttackTimer - dt);
            pinkHurtTimer = Math.Max(0, pinkHurtTimer - dt);
            for (int i = slashes.Count - 1; i >= 0; i--)
            {
                var s = slashes[i];
                s.life -= dt;
                if (s.life <= 0) slashes.RemoveAt(i); else slashes[i] = s;
            }
        }

        // =====================================================================
        //  Geometry helpers
        // =====================================================================
        static float NoteX(float t) => hitZoneX + (float)(chart.BeatPosition(t) - chart.BeatPosition(levelTimer)) * PixelsPerBeat;
        /// <summary>Bass and treble zones that start together get their enemies side by side instead of stacked.</summary>
        static float ZoneEnemyX(CompingZone z) => EnemyX(z.StartTime) + (z.IsBass ? 56f : 0f);

        static float EnemyX(float t) => hitZoneX + EnemyCombatOffset + (float)(chart.BeatPosition(t) - chart.BeatPosition(levelTimer)) * PixelsPerBeat * EnemySpeedMultiplier;
        static float EnemyY(MusicSymbol s) => floorY - (s.MidiPitch % 12) * 4f;
        static float BassBlend(MusicSymbol s) => s.OnBassStaff ? bassClefTransition : 0f;
        static float NoteY(MusicSymbol s) => Lerp(trebleY + s.TrebleStep * StepSpacing, bassY + s.BassStep * StepSpacing, BassBlend(s));
        static float Lerp(float a, float b, float t) => a + (b - a) * t;

        /// <summary>Notes fade in at the right edge and fade out just past the hit zone.</summary>
        static int Opacity(float x)
        {
            float left = hitZoneX - 90;
            if (x <= left || x >= screenWidth) return 0;
            float a = 255f;
            if (x < hitZoneX) a = 255f * (x - left) / 90f;
            else if (x > screenWidth - 100) a = 255f * (screenWidth - x) / 100f;
            return (int)Math.Clamp(a, 0, 255);
        }

        static Color Faded(Color c, float alpha01) => new(c.R, c.G, c.B, (int)Math.Clamp(c.A * alpha01, 0, 255));

        // =====================================================================
        //  Drawing
        // =====================================================================
        static void DrawBackground()
        {
            Raylib.ClearBackground(bgColor.Current);
            if (currentBackground.Id == 0) return;
            float scale = screenHeight / (float)currentBackground.Height;
            float w = currentBackground.Width * scale;
            float scroll = ((levelTimer * 100f) % w + w) % w;
            for (int i = 0; i < (int)(screenWidth / w) + 2; i++)
                Raylib.DrawTextureEx(currentBackground, new Vector2(-scroll + i * w, 0), 0f, scale, bgColor.Current);
        }

        static readonly Color TrebleZoneTint = new(100, 200, 255, 255), BassZoneTint = new(255, 110, 210, 255);

        /// <summary>
        /// Chord zones, drawn like a piano roll:
        ///  - a solid strike bar at the moment the chord should be played,
        ///  - a pill for each chord note at its stave position, labelled with its name,
        ///  - a sustain tail from each pill to the release point, which lights up as you hold it.
        /// Once struck, the pills ride along the hit line (like an osu! slider head) until the chord ends.
        /// </summary>
        static void DrawZones(float dt)
        {
            foreach (var z in chart.Zones)
            {
                float startX = NoteX(z.StartTime), endX = NoteX(z.EndTime);
                if (endX < -50 || startX > screenWidth + 50) continue;

                float visX = Math.Min(Math.Max(startX, hitZoneX), endX);
                float a = Opacity(visX) / 255f;
                if (z.IsBass) a *= Math.Max(0.25f, bassClefTransition);
                if (a <= 0.01f) continue;

                Color tint = z.IsBass ? BassZoneTint : TrebleZoneTint;
                bool missed = z.MissedInitialStrike;
                if (missed) tint = new Color(150, 150, 165, 255);
                Color strikeCol = z.StrikeFlashTimer > 0
                    ? ColorTracker.Lerp(tint, Theme.For(Grade(z.InitialStrikeDiff)), Math.Clamp(z.StrikeFlashTimer / 0.6f, 0f, 1f))
                    : tint;

                float staffTop = z.IsBass ? bassY : trebleY;
                float top = staffTop - 30, height = StepSpacing * 8 + 60;
                // Once the start has gone past the hit line, the box is cut off there (nothing to see behind you)
                float boxLeft = Math.Max(startX - 60, hitZoneX - 60);
                var rect = new Rectangle(boxLeft, top, Math.Max(endX - boxLeft - 60, 36), height);

                // Faint backing block so the zone still reads as one region
                Raylib.DrawRectangleRec(rect, Faded(tint, a * (z.IsBeingHeld ? 0.16f + gameBeatPulse * 0.06f : 0.09f)));
                Raylib.DrawRectangleLinesEx(rect, 2, Faded(tint, a * 0.45f));

                // Strike bar: exactly when to play. It brightens as it approaches the hit line.
                float approach = Math.Clamp(1f - (startX - hitZoneX) / 600f, 0f, 1f);
                if (!z.HasStruck || z.StrikeFlashTimer > 0)
                {
                    var bar = new Rectangle(startX - 5, top, 10, height);
                    Raylib.DrawRectangleRec(bar, Faded(strikeCol, a * (0.55f + 0.45f * approach)));
                    if (approach > 0.6f) Raylib.DrawRectangleRec(Grow(bar, 4 + gameBeatPulse * 4), Faded(strikeCol, a * 0.25f * approach));
                    // Little arrowheads above and below pointing at the bar
                    Raylib.DrawTriangle(new Vector2(startX - 12, top - 12), new Vector2(startX, top), new Vector2(startX + 12, top - 12), Faded(strikeCol, a));
                    Raylib.DrawTriangle(new Vector2(startX + 12, top + height + 12), new Vector2(startX, top + height), new Vector2(startX - 12, top + height + 12), Faded(strikeCol, a));
                }

                // Release bar: when you can let go
                Raylib.DrawRectangleRec(new Rectangle(endX - 3 - 60, top + 8, 6, height - 16), Faded(tint, a * 0.8f));

                // Note pills and sustain tails
                bool riding = z.HasStruck && !missed && levelTimer < z.EndTime;
                float headX = riding ? Math.Max(startX, hitZoneX) : startX;
                int? prevStep = null;   // no previous note yet
                bool shifted = false;
                foreach (var (note, label) in z.Tones)
                {
                    float y = NoteY(note);
                    int step = z.IsBass ? note.BassStep : note.TrebleStep;
                    // Notes a step apart (seconds) would overlap, so nudge every other one sideways like printed chords
                    shifted = prevStep.HasValue && Math.Abs(step - prevStep.Value) == 1 && !shifted;
                    prevStep = step;
                    float px = headX + (shifted ? 46 : 0);

                    // Sustain tail to the release point. Bright while you're holding the chord.
                    float tailEnd = Math.Max(endX - 60, px - 60);
                    Color tailCol = riding && z.IsBeingHeld ? Faded(Color.White, 0.85f) : Faded(tint, 0.6f);
                    Raylib.DrawRectangleRounded(new Rectangle(px, y - 5, Math.Max(0, tailEnd - px), 10), 1f, 6, Faded(tailCol, a));

                    // Ledger lines for pills above or below the stave, so the position still reads as a pitch
                    for (int ls = step <= -2 ? -2 : step >= 10 ? 10 : 0; ls != 0 && (step <= -2 ? ls >= step : ls <= step); ls += step <= -2 ? -2 : 2)
                    {
                        float ly = staffTop + ls * StepSpacing;
                        Raylib.DrawLineEx(new Vector2(px - 32, ly), new Vector2(px + 32, ly), 2, Faded(staveColor.Current, a));
                    }

                    // Pill with the note name
                    float pulse = riding && z.IsBeingHeld ? gameBeatPulse * 3 : 0;
                    var pill = new Rectangle(px - 27 - pulse, y - 15 - pulse * 0.5f, 54 + pulse * 2, 30 + pulse);
                    Color pillCol = riding && z.IsBeingHeld ? Color.White : strikeCol;
                    float pa = a; string text = label; float tx = px, ty = y;
                    zonePills.Add(() =>
                    {
                        Color ink = new(18, 18, 32, 255);
                        Raylib.DrawRectangleRounded(Grow(pill, 3), 1f, 12, Faded(ink, pa));   // outline
                        Raylib.DrawRectangleRounded(pill, 1f, 12, Faded(pillCol, pa));
                        var size = Raylib.MeasureTextEx(Ui.Body, text, 26, 1);
                        Raylib.DrawTextEx(Ui.Body, text, new Vector2(tx - size.X / 2, ty - size.Y / 2), 26, 1, Faded(ink, pa));
                    });
                }

                // Hold progress along the bottom (fills when you've held long enough)
                float len = z.EndTime - z.StartTime;
                if (len > 0 && z.AccumulatedHoldTime > 0)
                {
                    float p = Math.Clamp(z.AccumulatedHoldTime / (len * 0.3f), 0f, 1f);
                    Raylib.DrawRectangle((int)rect.X + 6, (int)(rect.Y + rect.Height - 10), (int)((rect.Width - 12) * p), 5, Faded(p >= 1 ? Theme.Good : Color.White, a));
                }

                // Chord name above the treble zone / below the bass zone so they never collide
                float labelY = z.IsBass ? rect.Y + rect.Height + 14 : rect.Y - 58;
                if (startX > -50) Ui.Text(z.ChordName, Math.Max(startX, riding ? hitZoneX : startX) - 10, labelY, 44, Faded(strikeCol, a), Ui.Body);
            }
        }

        /// <summary>Where a rest is drawn across the bar (see DrawSymbols).</summary>
        static float RestX(MusicSymbol s) => s.Type switch
        {
            SymbolType.Whole => NoteX(s.TimeSeconds + s.DurationSeconds / 2f) - 60f,
            SymbolType.Half => NoteX(s.TimeSeconds + s.DurationSeconds / 4f),
            _ => NoteX(s.TimeSeconds)
        };

        static readonly List<Action> zonePills = new();

        static void DrawZonePills()
        {
            foreach (var draw in zonePills) draw();
            zonePills.Clear();
        }

        static Rectangle Grow(Rectangle r, float by) => new(r.X - by, r.Y - by, r.Width + by * 2, r.Height + by * 2);

        static void DrawStaff()
        {
            Color stave = staveColor.Current;
            for (int i = 0; i < 5; i++)
            {
                int y = (int)trebleY + i * StepSpacing * 2;
                Raylib.DrawLineEx(new Vector2(50, y), new Vector2(screenWidth - 50, y), 2, stave);
                if (bassClefTransition > 0.01f)
                {
                    int by = (int)bassY + i * StepSpacing * 2;
                    Raylib.DrawLineEx(new Vector2(50, by), new Vector2(screenWidth - 50, by), 2, Faded(stave, bassClefTransition));
                }
            }

            // Hit zone: translucent column that flashes in the judgement colour and breathes on the beat
            int hzW = 80;
            float top = trebleY - 20, bottom = bassY + StepSpacing * 8 + 20;
            Color col = ColorTracker.Lerp(new Color(255, 255, 255, (int)(70 + gameBeatPulse * 30)), Faded(hitZoneFlashColor, 0.6f), hitZoneFlash);
            Raylib.DrawRectangle(hitZoneX - hzW / 2, (int)top, hzW, (int)(bottom - top), col);
            if (hitZoneFlash > 0)
                Raylib.DrawRectangle(hitZoneX - hzW / 2 - (int)(hitZoneFlash * 14), (int)top, hzW + (int)(hitZoneFlash * 28), (int)(bottom - top), Faded(hitZoneFlashColor, hitZoneFlash * 0.25f));
            Raylib.DrawLineEx(new Vector2(hitZoneX, top), new Vector2(hitZoneX, bottom), 3, new Color(255, 255, 255, 220));

            Ui.TextureCentered(texTrebleClef, 90, trebleY + 4 * StepSpacing, ClefScale, Color.White);
            if (bassClefTransition > 0.01f)
                Ui.TextureCentered(texBassClef, 90, bassY + 3 * StepSpacing, ClefScale, Faded(Color.White, bassClefTransition));

            // Key signature (it was never drawn before, so notes in e.g. G major looked wrong)
            int keySig = chart.KeySignature;
            int n = Math.Abs(keySig);
            for (int i = 0; i < n; i++)
            {
                int step = keySig > 0 ? StaffMath.SharpSigSteps[i] : StaffMath.FlatSigSteps[i];
                var tex = keySig > 0 ? texSharp : texFlat;
                float x = 140 + i * 16;
                Ui.TextureCentered(tex, x, trebleY + step * StepSpacing, AccScale * 0.8f, Color.White);
                if (bassClefTransition > 0.01f)
                    Ui.TextureCentered(tex, x, bassY + (step + 2) * StepSpacing, AccScale * 0.8f, Faded(Color.White, bassClefTransition));
            }

            float tsX = 150 + (n > 0 ? n * 16 + 4 : 0);
            Ui.TextCentered(chart.TimeSigNum.ToString(), tsX + 12, trebleY - 6, 46, Color.White, Ui.Body);
            Ui.TextCentered(chart.TimeSigDen.ToString(), tsX + 12, trebleY + StepSpacing * 4 - 4, 46, Color.White, Ui.Body);

            // Barlines
            Color nc = noteColor.Current;
            foreach (float bt in chart.Barlines)
            {
                float x = NoteX(bt) - 60;
                if (x < 50 || x > screenWidth + 20) continue;
                Raylib.DrawLineEx(new Vector2(x, trebleY), new Vector2(x, bassY + 8 * StepSpacing), 2, new Color(nc.R, nc.G, nc.B, Opacity(x)));
            }
        }

        static void DrawDecorations()
        {
            foreach (var d in level.Decorations)
            {
                d.WasDrawn = false;
                if (d.DespawnTime > 0 && levelTimer > d.DespawnTime) continue;
                // Static decorations wait for their spawn time; scrolling ones are visible while they travel in.
                if (d.IsStatic && levelTimer < d.SpawnTime) continue;

                if (d.IsAnimating)
                {
                    float t = Math.Clamp((levelTimer - d.AnimStartTime) / d.AnimDuration, 0f, 1f);
                    d.XOffset = Lerp(d.StartXOffset, d.TargetXOffset, t);
                    d.YOffset = Lerp(d.StartYOffset, d.TargetYOffset, t);
                    d.Scale = Lerp(d.StartScale, d.TargetScale, t);
                    d.Rotation = Lerp(d.StartRot, d.TargetRot, t);
                    d.Tint = ColorTracker.Lerp(d.StartColor, d.TargetColor, t);
                    if (t >= 1f) d.IsAnimating = false;
                }

                float rot = d.Rotation + d.SpinSpeed * levelTimer;
                float bob = MathF.Sin(levelTimer * d.BobSpeed) * d.BobAmplitude;
                float x = d.IsStatic ? screenWidth / 2f + d.XOffset : NoteX(d.SpawnTime) + d.XOffset;
                float y = StaffAnchorY(d.FollowStave) + d.YOffset + bob;

                float alpha = 1f;
                if (d.IsStatic && d.FadeInSec > 0)
                    alpha = Math.Clamp((levelTimer - d.SpawnTime) / d.FadeInSec, 0f, 1f);
                if (d.DespawnTime > 0 && d.FadeOutSec > 0)
                    alpha *= Math.Clamp((d.DespawnTime - levelTimer) / d.FadeOutSec, 0f, 1f);
                if (!d.IsStatic)
                {
                    if (x < -300 || x > screenWidth + 300) continue;
                    alpha *= Opacity(x) / 255f;
                }
                if (alpha <= 0.01f) continue;

                d.CustomColor?.Update(levelTimer);
                Color baseCol = d.CustomColor?.Current ?? d.Tint;
                Color col = Faded(baseCol, alpha);

                if (d.IsText)
                {
                    var size = Ui.Measure(d.Text, d.FontSize, Ui.Body);
                    float tx = x - size.X / 2f;
                    var outline = Faded(d.OutlineColor, alpha);
                    Ui.TextOutlined(d.Text, tx, y, d.FontSize, col, outline, d.OutlineThickness, Ui.Body);
                    if (d.IsBold) Ui.Text(d.Text, tx + 1.5f, y, d.FontSize, col, Ui.Body);
                    if (d.IsUnderlined) Raylib.DrawRectangle((int)tx, (int)(y + size.Y + 2), (int)size.X, 3, col);
                    d.LastBounds = new Rectangle(tx, y, size.X, size.Y);
                }
                else if (d.Texture.Id == 0)
                {
                    Raylib.DrawRectangle((int)x - 10, (int)y - 10, 20, 20, col);
                    d.LastBounds = new Rectangle(x - 10, y - 10, 20, 20);
                }
                else
                {
                    var dest = new Rectangle(x, y, d.Texture.Width * d.Scale, d.Texture.Height * d.Scale);
                    Raylib.DrawTexturePro(d.Texture, new Rectangle(0, 0, d.Texture.Width, d.Texture.Height), dest,
                                          new Vector2(dest.Width / 2, dest.Height / 2), rot, col);
                    d.LastBounds = new Rectangle(x - dest.Width / 2, y - dest.Height / 2, dest.Width, dest.Height);
                }
                d.WasDrawn = true;
            }
        }

        static void DrawBeams()
        {
            foreach (var g in chart.Beams)
            {
                var first = g.Notes[0];
                var last = g.Notes[^1];
                if (first.IsHit && last.IsHit && g.Notes.All(n => n.IsHit)) continue;

                int stemDX = g.StemDown ? -18 : 15;
                float firstX = NoteX(first.TimeSeconds) + stemDX, lastX = NoteX(last.TimeSeconds) + stemDX;
                if (lastX < 50 || firstX > screenWidth + 50) continue;

                int stemDY = g.StemDown ? 110 : -110;
                float firstBeamY = NoteY(first) + stemDY, lastBeamY = NoteY(last) + stemDY;
                float span = lastX - firstX;
                float BeamY(float x) => span == 0 ? firstBeamY : firstBeamY + (x - firstX) / span * (lastBeamY - firstBeamY);
                int secondary = g.StemDown ? -10 : 10;
                bool IsSemi(int i) => g.Notes[i].Type == SymbolType.Sixteenth;

                for (int i = 0; i < g.Notes.Count - 1; i++)
                {
                    float sx = NoteX(g.Notes[i].TimeSeconds) + stemDX, ex = NoteX(g.Notes[i + 1].TimeSeconds) + stemDX;
                    Color c1 = g.Notes[i].CustomColor?.Current ?? noteColor.Current;
                    Color c2 = g.Notes[i + 1].CustomColor?.Current ?? noteColor.Current;
                    Color c = ColorTracker.Lerp(c1, c2, 0.5f);
                    float a = (Opacity(sx) + Opacity(ex)) / 2f / 255f;
                    Raylib.DrawLineEx(new Vector2(sx, BeamY(sx)), new Vector2(ex, BeamY(ex)), 6, Faded(c, a));
                    // Second beam only between two semiquavers, so a quaver + semiquaver gets one full beam
                    // and a short stub for the semiquaver.
                    if (IsSemi(i) && IsSemi(i + 1))
                        Raylib.DrawLineEx(new Vector2(sx, BeamY(sx) + secondary), new Vector2(ex, BeamY(ex) + secondary), 6, Faded(c, a));
                }

                // Stubs ("beamlets") for semiquavers with no semiquaver beside them. Standard engraving: the stub
                // points into the group, i.e. right on the first note, left on the last (e.g. dotted quaver + semiquaver),
                // and left in the middle of a group.
                for (int i = 0; i < g.Notes.Count; i++)
                {
                    if (!IsSemi(i)) continue;
                    bool leftSemi = i > 0 && IsSemi(i - 1), rightSemi = i < g.Notes.Count - 1 && IsSemi(i + 1);
                    if (leftSemi || rightSemi) continue;
                    float sx = NoteX(g.Notes[i].TimeSeconds) + stemDX;
                    float neighbourX = i == 0 ? NoteX(g.Notes[1].TimeSeconds) + stemDX : NoteX(g.Notes[i - 1].TimeSeconds) + stemDX;
                    float stub = Math.Min(28f, Math.Abs(neighbourX - sx) * 0.45f) * (i == 0 ? 1 : -1);
                    Color c = g.Notes[i].CustomColor?.Current ?? noteColor.Current;
                    float ex = sx + stub;
                    Raylib.DrawLineEx(new Vector2(sx, BeamY(sx) + secondary), new Vector2(ex, BeamY(ex) + secondary), 6, Faded(c, Opacity(sx) / 255f));
                }

                for (int i = 0; i < g.Notes.Count; i++)
                {
                    float x = NoteX(g.Notes[i].TimeSeconds);
                    float sx = x + stemDX;
                    float fromY = NoteY(g.FarNotes[i]);
                    Color c = g.Notes[i].CustomColor?.Current ?? noteColor.Current;
                    Raylib.DrawLineEx(new Vector2(sx, fromY), new Vector2(sx, BeamY(sx)), 4, Faded(c, Opacity(x) / 255f));
                }
            }
        }

        static void DrawCombatLane()
        {
            Color stave = staveColor.Current;
            Raylib.DrawRectangleGradientV(0, (int)floorY, screenWidth, 14, Faded(stave, 0.25f), new Color(0, 0, 0, 0));
            Raylib.DrawLineEx(new Vector2(0, floorY), new Vector2(screenWidth, floorY), 2, Faded(stave, 0.5f));

            // Scrolling ground ticks so the lane feels like it's moving
            float spacing = 90f;
            float off = (((float)chart.BeatPosition(levelTimer) * PixelsPerBeat * EnemySpeedMultiplier) % spacing + spacing) % spacing;
            for (float x = -off; x < screenWidth; x += spacing)
                Raylib.DrawLineEx(new Vector2(x, floorY + 6), new Vector2(x - 18, floorY + 18), 2, Faded(stave, 0.18f));

            // Player: still a blue quadrilateral, but it breathes on the beat, lunges when attacking and flinches when hit.
            float squash = gameBeatPulse * 0.08f;
            float lunge = attackTimer > 0 ? MathF.Sin(attackTimer / 0.18f * MathF.PI) * 26f : 0f;
            float knock = hurtTimer > 0 ? -MathF.Sin(hurtTimer / 0.35f * MathF.PI) * 18f : 0f;
            float w = 30 * (1 + squash), h = 50 * (1 - squash);
            float px = hitZoneX + lunge + knock;
            Color body = hurtTimer > 0 && (int)(hurtTimer * 30) % 2 == 0 ? Theme.Miss : Color.SkyBlue;
            if (attackTimer > 0) Raylib.DrawRectangleRec(new Rectangle(px - w / 2 - lunge * 0.6f, floorY - h, w, h), Faded(Color.SkyBlue, 0.3f));
            Raylib.DrawRectangleRec(new Rectangle(px - w / 2, floorY - h, w, h), body);
            Raylib.DrawRectangleLinesEx(new Rectangle(px - w / 2, floorY - h, w, h), 2, new Color(255, 255, 255, 120));

            // Pink character: the bass stave's fighter. Same animations as the blue one, and it drops in with the bass clef.
            if (bassClefTransition > 0.01f)
            {
                float pLunge = pinkAttackTimer > 0 ? MathF.Sin(pinkAttackTimer / 0.18f * MathF.PI) * 26f : 0f;
                float pKnock = pinkHurtTimer > 0 ? -MathF.Sin(pinkHurtTimer / 0.35f * MathF.PI) * 18f : 0f;
                float pSquash = gameBeatPulse * 0.08f;
                float pw = 30 * (1 + pSquash), ph = 50 * (1 - pSquash);
                float ppx = PinkX + 15 + pLunge + pKnock;
                float drop = (1f - bassClefTransition) * 20;
                Color pink = new(255, 100, 200, 255);
                Color pBody = pinkHurtTimer > 0 && (int)(pinkHurtTimer * 30) % 2 == 0 ? Theme.Miss : pink;
                if (pinkAttackTimer > 0) Raylib.DrawRectangleRec(new Rectangle(ppx - pw / 2 - pLunge * 0.6f, floorY - ph - drop, pw, ph), Faded(pink, 0.3f * bassClefTransition));
                var pr = new Rectangle(ppx - pw / 2, floorY - ph - drop, pw, ph);
                Raylib.DrawRectangleRec(pr, Faded(pBody, bassClefTransition));
                Raylib.DrawRectangleLinesEx(pr, 2, new Color(255, 255, 255, (int)(120 * bassClefTransition)));
            }

            // Enemies for every playable note (ghost notes don't spawn enemies any more)
            foreach (var s in chart.Symbols)
            {
                if (!s.IsPlayable || s.EnemyResolved) continue;
                float ex = EnemyX(s.TimeSeconds);
                if (ex > screenWidth + 60) break;
                if (ex < -60) continue;
                float ey = EnemyY(s) + MathF.Sin((float)Raylib.GetTime() * 8 + s.TimeSeconds * 5) * 3;
                // Bass-stave enemies are tinted pink so you can see they belong to the pink character
                Color baseTint = s.IsMissed ? new Color(255, 160, 160, 255) : BassHand(s.OnBassStaff) ? new Color(255, 190, 235, 255) : Color.White;
                Color tint = Faded(baseTint, Opacity(ex) / 255f);
                if (BassHand(s.OnBassStaff) && !s.IsMissed)   // pink glow underneath: "this one's the pink character's"
                    Raylib.DrawEllipse((int)ex, (int)ey - 2, 26 + gameBeatPulse * 4, 7, Faded(BassZoneTint, 0.75f * Opacity(ex) / 255f));
                DrawEnemy(ex, ey, 1f, tint);
            }

            // One big enemy per chord zone
            foreach (var z in chart.Zones)
            {
                if (z.EnemyResolved) continue;
                float ex = ZoneEnemyX(z);
                if (ex < -80 || ex > screenWidth + 80) continue;
                Color zoneTint = z.IsBass ? new Color(255, 170, 225, 255) : new Color(190, 220, 255, 255);
                DrawEnemy(ex, floorY, 1.6f, Faded(zoneTint, Opacity(ex) / 255f));
            }
        }

        static void DrawEnemy(float x, float footY, float scale, Color tint)
        {
            if (texEnemy.Id == 0)
            {
                float s = 30 * scale;
                Raylib.DrawRectangleRec(new Rectangle(x - s / 2, footY - s, s, s), tint);
                return;
            }
            float h = texEnemy.Height * NoteScale * scale;
            Ui.TextureCentered(texEnemy, x, footY - h / 2f, NoteScale * scale, tint);
        }

        static void DrawSymbols()
        {
            foreach (var s in chart.Symbols)
            {
                if (s.IsHit || s.IsGhost) continue;   // chord-zone notes are drawn by DrawZones
                float x = s.IsRest ? RestX(s) : NoteX(s.TimeSeconds);
                if (x > screenWidth + 50) { if (s.IsRest) continue; break; }   // rests are drawn offset, so don't stop at them
                if (x < -50) continue;

                float a = Opacity(x) / 255f;
                if (a <= 0) continue;
                s.CustomColor?.Update(levelTimer);
                Color baseCol = s.CustomColor?.Current ?? noteColor.Current;
                Color col = s.IsGhost ? new Color(baseCol.R, baseCol.G, baseCol.B, (int)(100 * a))
                          : s.IsMissed ? new Color(255, 40, 40, (int)(255 * a))
                          : Faded(baseCol, a);

                if (s.IsRest)
                {
                    Texture2D tex = s.Type switch
                    {
                        SymbolType.Whole => texRestWhole, SymbolType.Half => texRestHalf,
                        SymbolType.Eighth => texRestEighth,
                        SymbolType.Sixteenth => texRestSixteenth.Id != 0 ? texRestSixteenth : texRestEighth,
                        _ => texRestQuarter
                    };
                    // Where the rest sits across the bar. A whole-bar rest goes in the middle of the bar (between the
                    // barlines, which are drawn 60px before the first beat). A half rest sits halfway between its two
                    // beats, so it reads as covering both and never lines up with a note on the other stave.
                    // Shorter rests stay on their beat.
                    float rx = x;
                    // Bass rests live on the bass stave and only show with it. Treble rests that overlap bass notes
                    // also wait for the bass stave, otherwise they'd sit on top of those notes on a single stave.
                    // Each sprite carries a short line segment: the whole rest hangs from the 4th line, the half rest
                    // sits on the middle line. The offsets line that segment up exactly with the stave line.
                    float ry = (s.OnBassStaff ? bassY : trebleY) + 4 * StepSpacing
                             + (s.Type == SymbolType.Whole ? 2.4f : s.Type == SymbolType.Half ? 2.4f : 0f);
                    if (s.OnBassStaff || s.HideWhenSingleStave)
                    {
                        if (bassClefTransition < 0.01f) continue;
                        col = Faded(col, bassClefTransition);
                    }
                    Ui.TextureCentered(tex, rx, ry, RestScale, col);
                    if (s.IsDotted) Raylib.DrawCircleV(new Vector2(rx + 25, ry), 4, col);
                    continue;
                }

                float blend = BassBlend(s);
                float y = NoteY(s);
                float step = Lerp(s.TrebleStep, s.BassStep, blend);
                float staffTop = Lerp(trebleY, bassY, blend);

                // Ledger lines
                int vs = (int)MathF.Round(step);
                for (int l = 10; l <= vs; l += 2) Raylib.DrawLineEx(new Vector2(x - 25, staffTop + l * StepSpacing), new Vector2(x + 25, staffTop + l * StepSpacing), 2, col);
                for (int l = -2; l >= vs; l -= 2) Raylib.DrawLineEx(new Vector2(x - 25, staffTop + l * StepSpacing), new Vector2(x + 25, staffTop + l * StepSpacing), 2, col);

                if (s.AccidentalToDraw != 0)
                {
                    var acc = s.AccidentalToDraw == 1 ? texSharp : s.AccidentalToDraw == -1 ? texFlat : texNatural;
                    Ui.TextureCentered(acc, x - 35, y, AccScale, col);
                }

                // Approach ring: shrinks onto the note as it reaches the hit line
                float until = s.TimeSeconds - levelTimer;
                if (s.IsPlayable && !s.IsMissed && !s.Skipped && until > 0 && until < 0.5f)
                {
                    float k = until / 0.5f;
                    float r = 22 + 60 * k;
                    Raylib.DrawRing(new Vector2(x, y), r - 3, r, 0, 360, 40, Faded(Color.White, (1 - k) * 0.7f * a));
                }

                var head = s.Type == SymbolType.Whole ? texHeadSemibreve : s.Type == SymbolType.Half ? texHeadMinim : texHeadCrotchet;
                Ui.TextureCentered(head, x, y, NoteScale, col);

                if (s.HasTie)
                {
                    float endX = NoteX(s.TimeSeconds + s.TieDurationSeconds);
                    Vector2 p1 = new(x + 15, y + 15), p3 = new(endX - 15, y + 15), p2 = new((p1.X + p3.X) / 2, y + 35);
                    Raylib.DrawSplineSegmentBezierQuadratic(p1, p2, p3, 3, col);
                }

                // One stem per chord: drawn by the note nearest the tip, starting from the chord's far note.
                if (s.Type != SymbolType.Whole && s.BeamGroupID == -1 && !s.IsGhost && s.DrawsStem)
                {
                    int dy = s.IsStemDown ? 110 : -110, dx = s.IsStemDown ? -18 : 15;
                    float fromY = s.StemFrom != null ? NoteY(s.StemFrom) : y;
                    Vector2 stemBase = new(x + dx, fromY), tip = new(x + dx, y + dy);
                    Raylib.DrawLineEx(stemBase, tip, 4, col);
                    if (s.Type is SymbolType.Eighth or SymbolType.Sixteenth)
                    {
                        int fy = s.IsStemDown ? 25 : -25, off = s.IsStemDown ? -10 : 10;
                        Raylib.DrawLineEx(tip, new Vector2(tip.X + 15, tip.Y + fy), 8, col);
                        if (s.Type == SymbolType.Sixteenth)
                            Raylib.DrawLineEx(new Vector2(tip.X, tip.Y + off), new Vector2(tip.X + 15, tip.Y + fy + off), 8, col);
                    }
                }
                if (s.IsDotted) Raylib.DrawCircleV(new Vector2(x + 25, y), 4, col);
            }
        }

        /// <summary>Shows which notes you're holding right on the hit line.</summary>
        static void DrawHeldKeys()
        {
            foreach (int p in heldPitches)
            {
                var (tStep, bStep) = StaffMath.Steps(p, chart.KeySignature);
                bool bass = p < 60 && bassClefTransition > 0.5f;
                float y = bass ? bassY + bStep * StepSpacing : trebleY + tStep * StepSpacing;
                // During two-handed chords, show which hand each note was given to (pink = left/bass, cyan = right/treble)
                Color fill = !twoHandedNow ? Theme.Accent : p < currentHandSplit ? new Color(255, 110, 210, 255) : new Color(100, 200, 255, 255);
                Raylib.DrawRectangleRounded(new Rectangle(hitZoneX - 34, y - 9, 68, 18), 1f, 8, Faded(fill, 0.55f));
                Raylib.DrawRectangleRoundedLinesEx(new Rectangle(hitZoneX - 34, y - 9, 68, 18), 1f, 8, 2, Faded(Color.White, 0.7f));
            }
        }

        static void DrawSlashes()
        {
            Raylib.BeginBlendMode(BlendMode.Additive);
            foreach (var (a, b, life, col) in slashes)
            {
                float t = life / 0.14f;
                Raylib.DrawLineEx(a, b, 10 * t + 2, Faded(col, t));
                Raylib.DrawLineEx(a, b, 3, Faded(Color.White, t));
            }
            Raylib.EndBlendMode();
        }

        // =====================================================================
        //  HUD
        // =====================================================================
        static void DrawHud()
        {
            bool editorOpen = editor.IsOpen;
            int right = editorOpen ? screenWidth - editor.PanelWidth - 24 : screenWidth - 30;

            // Song progress along the very top
            float progress = Math.Clamp((levelTimer + chart.LeadIn) / Math.Max(0.1f, levelEndTime + chart.LeadIn), 0f, 1f);
            Raylib.DrawRectangle(0, 0, screenWidth, 6, new Color(0, 0, 0, 120));
            Raylib.DrawRectangle(0, 0, (int)(screenWidth * progress), 6, Theme.Accent);

            // Health bar
            var hb = new Rectangle(30, 26, 520, 20);
            float ratio = Math.Clamp(displayHealth / MaxHealth, 0f, 1f);
            Color hcol = ratio > 0.5f ? Theme.Good : ratio > 0.2f ? Theme.Okay : Theme.Miss;
            Raylib.DrawRectangleRounded(hb, 1f, 10, new Color(0, 0, 0, 150));
            if (ratio > 0.01f)
                Raylib.DrawRectangleRounded(new Rectangle(hb.X, hb.Y, hb.Width * ratio, hb.Height), 1f, 10, hcol);
            Raylib.DrawRectangleRoundedLinesEx(hb, 1f, 10, 2, new Color(255, 255, 255, 160));

            if (Ui.Button(new Rectangle(30, 62, 64, 48), "II", true, 26, "pause")) isPaused = true;
            if (autoplay) Ui.Text("AUTOPLAY", 110, 72, 26, Theme.Gold);

            // Score + accuracy (top right, osu! style)
            Ui.TextRight(((int)MathF.Round(displayScore)).ToString("D8"), right, 18, 64, Theme.Text);
            Ui.TextRight($"{Accuracy:0.00}%", right, 88, 32, Theme.TextDim);

            // Combo (bottom left) with a pop on every hit
            if (combo > 0 || comboBreakTimer > 0)
            {
                float pop = 1f + comboPop * 0.28f;
                string text = combo > 0 ? $"{combo}x" : "0x";
                Color c = comboBreakTimer > 0 ? Theme.Miss : Theme.Text;
                var m = Ui.Measure(text, 84);
                Ui.TextPop(text, new Vector2(30 + m.X / 2, screenHeight - 58), 84, pop, 0, Faded(Theme.Accent, 0.35f * comboPop + 0.15f));
                Ui.TextPop(text, new Vector2(30 + m.X / 2, screenHeight - 58), 84, 1f + comboPop * 0.12f, 0, c);
            }

            // Judgement popup
            if (judgementTimer > 0)
            {
                float t = 1f - judgementTimer / JudgementDuration;
                float scale = Ease.OutBack(t * 4f) * (1f - t * 0.15f);
                float alpha = judgementTimer < 0.25f ? judgementTimer / 0.25f : 1f;
                var pos = new Vector2(hitZoneX + 10, trebleY - 80 - t * 26);
                Ui.TextPop(judgementText, pos, 44, scale, 0, Faded(Color.Black, alpha * 0.5f));
                Ui.TextPop(judgementText, pos - new Vector2(2, 2), 44, scale, 0, Faded(judgementColor, alpha));
            }

            // Low health vignette and combo milestone flash
            if (ratio < 0.3f && !isGameOver)
            {
                float v = (0.3f - ratio) / 0.3f * (0.6f + 0.4f * gameBeatPulse);
                Raylib.DrawRectangleGradientH(0, 0, 220, screenHeight, Faded(Theme.Miss, 0.45f * v), new Color(0, 0, 0, 0));
                Raylib.DrawRectangleGradientH(screenWidth - 220, 0, 220, screenHeight, new Color(0, 0, 0, 0), Faded(Theme.Miss, 0.45f * v));
            }
            if (edgeFlash > 0)
            {
                Raylib.DrawRectangleGradientV(0, 0, screenWidth, 160, Faded(Theme.Accent, 0.5f * edgeFlash), new Color(0, 0, 0, 0));
                Raylib.DrawRectangleGradientV(0, screenHeight - 160, screenWidth, 160, new Color(0, 0, 0, 0), Faded(Theme.Accent, 0.5f * edgeFlash));
            }

            if (level.Warnings.Count > 0 && editorOpen)
                Ui.Text("! " + level.Warnings[0], 30, 120, 22, Theme.Okay, Ui.Body);
        }

        static void DrawCountdown()
        {
            if (levelTimer >= 0 && levelTimer > chart.BeatTimes.ElementAtOrDefault(1)) return;
            double beat = chart.BeatPosition(levelTimer);
            if (beat < -chart.TimeSigNum || beat >= 1) return;

            string text;
            float since;
            if (beat < 0)
            {
                double k = -beat;
                int n = (int)Math.Ceiling(k);
                since = (float)(n - k);
                text = n.ToString();
            }
            else { text = "GO!"; since = (float)beat; }

            float scale = Ease.OutBack(since * 4f) * (1.15f - since * 0.15f);
            float alpha = 1f - since * 0.85f;
            var centre = new Vector2((hitZoneX + screenWidth) / 2f, centerStaffY - 190);
            Ui.TextPop(text, centre, 150, scale, 0, Faded(Theme.Accent, alpha * 0.4f));
            Ui.TextPop(text, centre, 140, scale, 0, Faded(Theme.Text, alpha));
        }

        static void DrawOverlays(float frameDt)
        {
            if (!isPaused && !isGameOver) { overlayAlpha = 0; return; }
            overlayAlpha = Math.Min(1f, overlayAlpha + frameDt * (isGameOver ? 2f : 6f));
            Raylib.DrawRectangle(0, 0, screenWidth, screenHeight, new Color(5, 6, 14, (int)(190 * overlayAlpha)));
            int cx = screenWidth / 2, cy = screenHeight / 2;

            string title = isGameOver ? "GAME OVER" : "PAUSED";
            Color tc = isGameOver ? Theme.Miss : Theme.Text;
            Ui.TextPop(title, new Vector2(cx, cy - 170), 110, Ease.OutBack(overlayAlpha), 0, Faded(tc, overlayAlpha));
            if (overlayAlpha < 0.6f) return;

            int y = cy - 60;
            if (!isGameOver)
            {
                if (Ui.Button(new Rectangle(cx - 220, y, 440, 80), "Continue")) isPaused = false;
                y += 104;
            }
            if (Ui.Button(new Rectangle(cx - 220, y, 440, 80), "Retry") || (isGameOver && Raylib.IsKeyPressed(KeyboardKey.Enter)))
                BeginTransition(() => StartLevel(currentMidiPath));
            if (Ui.Button(new Rectangle(cx - 220, y + 104, 440, 80), "Back to Map"))
                BeginTransition(() => SetState(GameState.LevelList));
        }
    }
}