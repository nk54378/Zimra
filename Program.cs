using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Multimedia;
using Raylib_cs;

namespace RhythmGameUI
{
    /// <summary>Main entry point, menus, audio and screen flow. Gameplay lives in Gameplay.cs.</summary>
    partial class Program
    {
        // ---------------- Window & flow ----------------
        static int screenWidth, screenHeight;
        static GameState currentState = GameState.MainMenu;
        static bool quitRequested;
        static float stateTime;                 // seconds since the current screen opened

        // ---------------- Worlds ----------------
        class WorldInfo
        {
            public string Name = "", Theme = "", Description = "", BackgroundImage = "";
            public Vector2[] Map = Array.Empty<Vector2>();
        }

        static readonly WorldInfo[] worlds =
        {
            new() {
                Name = "Auvora", Theme = "EDM Classical", BackgroundImage = "bg_auvora",
                Description = "When Tehilo was forced to flee Zimra,\nhe came across this quiet, dreamy planet.",
                Map = new Vector2[] { new(0.10f, 0.10f), new(0.25f, 0.40f), new(0.50f, 0.20f), new(0.75f, 0.20f), new(0.60f, 0.50f),
                                      new(0.35f, 0.65f), new(0.10f, 0.80f), new(0.25f, 0.95f), new(0.65f, 0.90f), new(0.85f, 0.55f) }
            },
            new() {
                Name = "Dubrath", Theme = "Dubstep / Bass House", BackgroundImage = "bg_dubrath",
                Description = "A volcanic planet with eruptions\ntimed to bass drops.",
                Map = new Vector2[] { new(0.80f, 0.90f), new(0.50f, 0.80f), new(0.20f, 0.90f), new(0.20f, 0.60f), new(0.50f, 0.50f),
                                      new(0.80f, 0.60f), new(0.80f, 0.30f), new(0.50f, 0.20f), new(0.20f, 0.30f), new(0.50f, 0.05f) }
            },
            new() {
                Name = "Melumi", Theme = "Gospel / Jazz", BackgroundImage = "bg_mbelumi",
                Description = "Originally a tropical rhythmic environment, but was\nforced to adopt chord structures to survive.",
                Map = new Vector2[] { new(0.10f, 0.10f), new(0.10f, 0.50f), new(0.20f, 0.90f), new(0.50f, 0.95f), new(0.80f, 0.90f),
                                      new(0.90f, 0.50f), new(0.80f, 0.20f), new(0.50f, 0.15f), new(0.35f, 0.40f), new(0.50f, 0.60f) }
            },
        };
        static int selectedWorld = 1;
        static float worldSlide;                // animates the world card when switching

        // ---------------- Level preview ----------------
        static string selectedLevelPath = "";
        static int selectedLevelIndex = -1;
        static LevelConfig? previewConfig;
        static float previewSlide;

        // ---------------- Shared assets ----------------
        static readonly LevelEditor editor = new();
        static readonly Dictionary<string, Texture2D> textureCache = new(StringComparer.OrdinalIgnoreCase);
        static readonly TriangleField triangles = new();

        static Texture2D texHeadCrotchet, texHeadMinim, texHeadSemibreve;
        static Texture2D texRestWhole, texRestHalf, texRestQuarter, texRestEighth, texRestSixteenth;
        static Texture2D texTrebleClef, texBassClef, texArrow, texSharp, texFlat, texNatural, texEnemy;

        // ---------------- Audio ----------------
        const float MenuBpm = 128f;             // BEWM by Slushii x Tokyo Machine
        static Music menuMusic;
        static bool hasMenuMusic;

        // ---------------- Transitions ----------------
        static bool isTransitioning;
        static float transitionProgress;
        static Action? pendingTransitionAction;

        // ---------------- Scores ----------------
        class BestScore { public int Score { get; set; } public float Accuracy { get; set; } public string Rank { get; set; } = "F"; public int MaxCombo { get; set; } }
        static Dictionary<string, BestScore> bestScores = new();
        const string ScoresFile = "scores.json";

        // =====================================================================
        /// <summary>Set when the Assets folder couldn't be found, so the menu can say where it looked.</summary>
        static string? assetWarning;

        /// <summary>
        /// The game loads "Assets/..." and "Levels/..." relative to the working directory, which changes depending on
        /// how it's launched (dotnet run, the VS Code debugger, double-clicking the build). Walk up from the working
        /// directory and the executable until a folder containing Assets/ is found, and run from there. Running from
        /// the real project folder also means the editor saves into your real Levels/ folder, not a copy in bin/.
        /// </summary>
        static void FindGameFolder(string[] args)
        {
            int i = Array.IndexOf(args, "--root");
            var starts = new List<string>();
            if (i >= 0 && i + 1 < args.Length) starts.Add(args[i + 1]);
            // The executable's own folder comes first: it always lives inside the project (bin/Debug/net10.0),
            // whereas the working directory depends on how VS Code, a terminal or Finder launched the game.
            starts.Add(AppContext.BaseDirectory);
            starts.Add(Directory.GetCurrentDirectory());

            foreach (var start in starts)
            {
                var dir = new DirectoryInfo(Path.GetFullPath(start));
                for (int depth = 0; dir != null && depth < 6; depth++, dir = dir.Parent)
                {
                    if (Directory.Exists(Path.Combine(dir.FullName, "Assets")) && Directory.Exists(Path.Combine(dir.FullName, "Levels")))
                    {
                        Directory.SetCurrentDirectory(dir.FullName);
                        Console.WriteLine("[Zimra] Game folder: " + dir.FullName);
                        if (!Directory.Exists(Path.Combine("Assets", "Fonts")))
                            assetWarning = "No Assets/Fonts folder in " + dir.FullName + ", using the default font.";
                        return;
                    }
                }
            }
            assetWarning = "Couldn't find the Assets and Levels folders. Put them next to Zimra.csproj. Looked from: " + AppContext.BaseDirectory;
            Console.WriteLine("[Zimra] " + assetWarning);
        }

        static void Main(string[] args)
        {
            FindGameFolder(args);
            Raylib.SetConfigFlags(ConfigFlags.Msaa4xHint | ConfigFlags.VSyncHint);
            Raylib.InitWindow(1920, 1080, "Zimra");
            Raylib.InitAudioDevice();
            MidiInput.Start();

            bool windowed = args.Contains("--windowed");
            if (!windowed)
            {
                int monitor = Raylib.GetCurrentMonitor();
                Raylib.SetWindowSize(Raylib.GetMonitorWidth(monitor), Raylib.GetMonitorHeight(monitor));
                Raylib.ToggleFullscreen();
            }
            int refresh = Raylib.GetMonitorRefreshRate(Raylib.GetCurrentMonitor());
            Raylib.SetTargetFPS(Math.Clamp(refresh, 60, 240));
            Raylib.SetExitKey(KeyboardKey.F10);
            Rlgl.DisableBackfaceCulling();

            LoadAssets();
            Ui.LoadFonts();
            Ui.MouseBlocked = m => currentState == GameState.Playing && editor.OwnsMouse(m);
            editor.Init();
            LoadScores();
            LoadStoryProgress();
            ApplyCommandLine(args);

            while (!Raylib.WindowShouldClose() && !quitRequested)
            {
                screenWidth = Raylib.GetScreenWidth();
                screenHeight = Raylib.GetScreenHeight();
                float dt = Math.Min(Raylib.GetFrameTime(), 0.1f);
                stateTime += dt;

                if (Raylib.IsKeyPressed(KeyboardKey.F11)) Raylib.ToggleFullscreen();
                if (currentState == GameState.Playing && Raylib.IsKeyPressed(KeyboardKey.F1)) editor.Toggle();

                UpdateMenuMusic(dt);

                Raylib.BeginDrawing();
                Raylib.ClearBackground(Theme.Background);

                switch (currentState)
                {
                    case GameState.MainMenu: DrawMainMenu(dt); break;
                    case GameState.LevelSelect: DrawLevelSelect(dt); break;
                    case GameState.WorldSelect: DrawWorldSelect(dt); break;
                    case GameState.LevelList: DrawLevelList(dt); break;
                    case GameState.Playing: UpdateAndDrawGameplay(dt); break;
                    case GameState.EndScreen: DrawEndScreen(dt); break;
                    case GameState.Cutscene: DrawCutscene(dt); break;
                }

                if (currentState == GameState.Playing) editor.Draw();
                DrawTransition(dt);
                Raylib.EndDrawing();

                // Rebuilding the level is done outside the draw pass.
                if (currentState == GameState.Playing && editor.TakeRebuildRequest(out string json, out float at))
                    StartLevel(currentMidiPath, json, at);
            }

            UnloadAssets();
            Ui.UnloadFonts();
            MidiInput.Stop();
            Raylib.CloseAudioDevice();
            Raylib.CloseWindow();
        }

        /// <summary>
        /// Developer shortcuts:  --level path/to/level.mid  jumps straight into a level,
        /// --editor also opens the editor, --autoplay lets the game play itself, --windowed skips fullscreen.
        /// </summary>
        static void ApplyCommandLine(string[] args)
        {
            autoplay = args.Contains("--autoplay");
            if (args.Contains("--reset-story")) { storyProgress = new(); SaveStoryProgress(); }
            int cs = Array.IndexOf(args, "--cutscene");   // --cutscene world1_intro plays one straight away
            if (cs >= 0 && cs + 1 < args.Length && CutsceneExists(args[cs + 1]))
                StartCutscene(new List<string> { args[cs + 1] }, () => SetState(GameState.MainMenu));
            int i = Array.IndexOf(args, "--level");
            if (i >= 0 && i + 1 < args.Length)
            {
                StartLevel(args[i + 1]);
                SetState(GameState.Playing);
                if (args.Contains("--editor")) editor.Toggle();
                int s = Array.IndexOf(args, "--seek");
                if (s >= 0 && s + 1 < args.Length && float.TryParse(args[s + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float t))
                    StartLevel(currentMidiPath, editor.HasLevel ? editor.GetPreviewJson() : null, t);
            }
            int sc = Array.IndexOf(args, "--screen");
            if (sc >= 0 && sc + 1 < args.Length)
            {
                switch (args[sc + 1])
                {
                    case "play": SetState(GameState.LevelSelect); break;
                    case "worlds": SetState(GameState.WorldSelect); break;
                    case "levels": SetState(GameState.LevelList); SelectLevel(0); break;
                    case "end":
                        StartLevel("Levels/World1/level1.mid");
                        score = 48250; maxCombo = 41; combo = 12;
                        judgeCounts[0] = 30; judgeCounts[1] = 8; judgeCounts[2] = 2; judgeCounts[3] = 3;
                        accuracySum = 30 + 8 / 3f + 2 / 6f; accuracyCount = 43;
                        FinishLevel(false);
                        break;
                }
            }
        }

        static void SetState(GameState state)
        {
            // Finishing a level lets the song play on over the results screen; failing or quitting stops it.
            if (currentState == GameState.Playing && state != GameState.Playing)
            {
                if (state == GameState.EndScreen && !resultFailed && hasLevelMusic && musicStarted && !musicPaused) levelMusicTail = true;
                else StopLevelMusic();
            }
            // Leaving the results screen (replay, back to the map) cuts the song off.
            if (currentState == GameState.EndScreen && state != GameState.EndScreen && levelMusicTail)
            {
                levelMusicTail = false;
                StopLevelMusic();
            }
            // Starting a level stops any preview straight away
            if (state == GameState.Playing) StopPreview();
            currentState = state;
            stateTime = 0f;
            if (state != GameState.Playing) editor.IsOpen = false;
        }

        // =====================================================================
        //  Assets
        // =====================================================================
        static Texture2D LoadTex(params string[] candidates)
        {
            foreach (var path in candidates)
            {
                if (!File.Exists(path)) continue;
                var tex = Raylib.LoadTexture(path);
                Raylib.SetTextureFilter(tex, TextureFilter.Bilinear);
                return tex;
            }
            Console.WriteLine($"[Assets] Missing texture: {candidates[0]}");
            return default;
        }

        static void LoadAssets()
        {
            texHeadCrotchet = LoadTex("Assets/crotchet_head.png");
            texHeadMinim = LoadTex("Assets/minim_head.png");
            texHeadSemibreve = LoadTex("Assets/semibreve_head.png");
            texRestWhole = LoadTex("Assets/rest_whole.png");
            texRestHalf = LoadTex("Assets/rest_half.png");
            texRestQuarter = LoadTex("Assets/rest_quarter.png");
            texRestEighth = LoadTex("Assets/rest_eighth.png");
            texRestSixteenth = LoadTex("Assets/rest_sixteenth.png");
            texTrebleClef = LoadTex("Assets/treble_clef.png");
            texBassClef = LoadTex("Assets/bass_clef.png");
            texArrow = LoadTex("Assets/arrow.png");
            texSharp = LoadTex("Assets/sharp.png");
            texFlat = LoadTex("Assets/flat.png");
            texNatural = LoadTex("Assets/natural.png");
            texEnemy = LoadTex("Assets/Enemy Sprites/default_enemy.png", "Assets/Enemy Sprites/enemy_default.png",
                               "Assets/enemy_default.png", "Assets/default_enemy.png");

            string menuPath = "Assets/Music/Slushii x Tokyo Machine - BEWM.mp3";
            if (File.Exists(menuPath))
            {
                menuMusic = Raylib.LoadMusicStream(menuPath);
                menuMusic.Looping = true;
                hasMenuMusic = true;
            }
        }

        /// <summary>Loads Assets/&lt;name&gt;.png once and caches it. Falls back to the arrow for decorations.</summary>
        public static Texture2D GetTexture(string? name, bool fallbackToArrow = true)
        {
            Texture2D fallback = fallbackToArrow ? texArrow : default;
            if (string.IsNullOrWhiteSpace(name)) return fallback;
            if (name == "arrow") return texArrow;
            if (Path.GetFileName(name) != name) return fallback;     // no folders / path tricks

            string key = Path.GetFileNameWithoutExtension(name);
            if (textureCache.TryGetValue(key, out var cached)) return cached.Id != 0 ? cached : fallback;

            string? path = new[] { Path.Combine("Assets", key + ".png"), Path.Combine("Assets", "Decorations", key + ".png"), Path.Combine("Assets", "Backgrounds", key + ".png") }
                .FirstOrDefault(File.Exists);
            Texture2D tex = path != null ? Raylib.LoadTexture(path) : default;
            if (tex.Id != 0) Raylib.SetTextureFilter(tex, TextureFilter.Bilinear);
            textureCache[key] = tex;
            return tex.Id != 0 ? tex : fallback;
        }

        static void UnloadAssets()
        {
            foreach (var t in new[] { texHeadCrotchet, texHeadMinim, texHeadSemibreve, texRestWhole, texRestHalf, texRestQuarter,
                                      texRestEighth, texRestSixteenth, texTrebleClef, texBassClef, texArrow, texSharp, texFlat, texNatural, texEnemy })
                if (t.Id != 0) Raylib.UnloadTexture(t);

            // Backgrounds live in the same cache, so they are unloaded exactly once here.
            foreach (var t in textureCache.Values) if (t.Id != 0) Raylib.UnloadTexture(t);
            textureCache.Clear();

            if (hasMenuMusic) Raylib.UnloadMusicStream(menuMusic);
            StopPreview();
            UnloadLevelMusic();
        }

        // =====================================================================
        //  Audio
        // =====================================================================
        static bool InMenus => currentState is GameState.MainMenu or GameState.LevelSelect or GameState.WorldSelect or GameState.LevelList or GameState.EndScreen or GameState.Cutscene;

        // Who's playing in the menus. Only one of these is audible at a time; the others fade out.
        //  - the level song carrying on over the results screen after you finish a level,
        //  - a looping preview of the selected level on the level map,
        //  - otherwise the menu track.
        static bool levelMusicTail;
        static float menuVolume;

        static Music previewMusic;
        static bool hasPreview;
        static string previewAudioPath = "";
        static float previewStart, previewLength, previewVolume;
        static double previewBpm = 120;
        static readonly Random previewRandom = new();
        const float PreviewSeamFade = 0.35f;

        static void UpdateMenuMusic(float dt)
        {
            // 1. Level song over the results screen: plays to the end, then the menu track comes back.
            bool tailPlaying = false;
            if (levelMusicTail)
            {
                if (currentState == GameState.EndScreen && hasLevelMusic)
                {
                    Raylib.UpdateMusicStream(levelMusic);
                    tailPlaying = Raylib.IsMusicStreamPlaying(levelMusic) &&
                                  Raylib.GetMusicTimePlayed(levelMusic) < Raylib.GetMusicTimeLength(levelMusic) - 0.05f;
                }
                if (!tailPlaying) { levelMusicTail = false; StopLevelMusic(); }
            }

            // 2. Level preview on the level map
            bool previewWanted = currentState == GameState.LevelList && hasPreview;
            if (hasPreview)
            {
                if (currentState != GameState.LevelList && previewVolume <= 0.01f) StopPreview();
                else UpdatePreview(dt, previewWanted);
            }

            // 3. Menu track: fades out under the other two, fades back in afterwards
            // A cutscene with its own music takes over from the menu track
            bool menuWanted = InMenus && !tailPlaying && !previewWanted && !(currentState == GameState.Cutscene && hasCutsceneMusic);
            if (hasMenuMusic)
            {
                if (!InMenus)
                {
                    if (Raylib.IsMusicStreamPlaying(menuMusic)) Raylib.StopMusicStream(menuMusic);
                    menuVolume = 0f;
                }
                else
                {
                    menuVolume = Approach(menuVolume, menuWanted ? 1f : 0f, dt * (menuWanted ? 0.8f : 2.5f));
                    if (menuVolume > 0.001f)
                    {
                        if (!Raylib.IsMusicStreamPlaying(menuMusic)) Raylib.PlayMusicStream(menuMusic);
                        Raylib.SetMusicVolume(menuMusic, menuVolume);
                        Raylib.UpdateMusicStream(menuMusic);
                    }
                    else if (Raylib.IsMusicStreamPlaying(menuMusic)) Raylib.PauseMusicStream(menuMusic);
                }
            }

            // Menu visuals pulse to whatever is playing
            if (InMenus)
            {
                double beat;
                if (tailPlaying) beat = chart.BeatPosition(Raylib.GetMusicTimePlayed(levelMusic) + audioSyncOffset);
                else if (previewWanted) beat = Raylib.GetMusicTimePlayed(previewMusic) * previewBpm / 60.0;
                else beat = (hasMenuMusic ? Raylib.GetMusicTimePlayed(menuMusic) : Raylib.GetTime()) * MenuBpm / 60.0;
                float frac = (float)(beat - Math.Floor(beat));
                Ui.BeatPulse = MathF.Pow(1f - frac, 4f);
            }
        }

        static float Approach(float value, float target, float step) =>
            value < target ? Math.Min(target, value + step) : Math.Max(target, value - step);

        /// <summary>
        /// Starts a looping preview of a level's song: a random stretch from the middle of the song, a whole number
        /// of bars long (read from the MIDI) so the loop lands on the downbeat.
        /// </summary>
        static void StartPreview(string midiPath)
        {
            string? audio = File.Exists(midiPath) ? FindLevelAudio(midiPath) : null;
            if (audio == null) { StopPreview(); return; }
            if (hasPreview && audio == previewAudioPath) return;
            StopPreview();

            previewMusic = Raylib.LoadMusicStream(audio);
            if (previewMusic.FrameCount == 0) return;
            previewMusic.Looping = true;
            hasPreview = true;
            previewAudioPath = audio;

            float songLength = Raylib.GetMusicTimeLength(previewMusic);
            float offset = previewConfig?.AudioSyncOffset ?? 0f;   // audio time = MIDI time - offset
            (previewStart, previewLength, previewBpm) = PickPreviewSection(midiPath, songLength);
            previewStart = Math.Clamp(previewStart - offset, 0f, Math.Max(0f, songLength - 1f));

            previewVolume = 0f;
            Raylib.SetMusicVolume(previewMusic, 0f);
            Raylib.PlayMusicStream(previewMusic);
            Raylib.SeekMusicStream(previewMusic, previewStart);
        }


        static (float start, float length, double bpm) PickPreviewSection(string midiPath, float songLength)
        {
            try
            {
                var tempo = Melanchall.DryWetMidi.Interaction.TempoMapManagingUtilities.GetTempoMap(MidiFile.Read(midiPath));
                float BarTime(long bar) => (float)(Melanchall.DryWetMidi.Interaction.TimeConverter.ConvertTo<Melanchall.DryWetMidi.Interaction.MetricTimeSpan>(
                    new Melanchall.DryWetMidi.Interaction.BarBeatTicksTimeSpan(bar, 0, 0), tempo).TotalMicroseconds / 1_000_000.0);

                var bars = new List<float>();
                for (long b = 0; b < 2000; b++) { float t = BarTime(b); if (t > songLength) break; bars.Add(t); }
                double bpm = tempo.GetTempoAtTime(new Melanchall.DryWetMidi.Interaction.MidiTimeSpan(0)).BeatsPerMinute;

                if (bars.Count > 8)
                {
                    // Loop 4, 8 or 16 bars: the first that lasts at least 12 seconds
                    float barLen = (bars[^1] - bars[0]) / (bars.Count - 1);
                    int n = 4;
                    while (n < 16 && n * barLen < 12f) n *= 2;
                    // Start somewhere in the middle of the song (25%-70%), on a bar that's a multiple of 4
                    var candidates = Enumerable.Range(0, bars.Count - n).Where(i => i % 4 == 0 &&
                        bars[i] >= songLength * 0.25f && bars[i + n] <= songLength * 0.75f).ToList();
                    if (candidates.Count > 0)
                    {
                        int i = candidates[previewRandom.Next(candidates.Count)];
                        return (bars[i], bars[i + n] - bars[i], bpm);
                    }
                }
            }
            catch (Exception ex) { Console.WriteLine("[Preview] " + ex.Message); }
            // No usable MIDI timing: 15 seconds from a bit before the middle
            float len = Math.Min(15f, songLength * 0.4f);
            float start = songLength * (0.3f + (float)previewRandom.NextDouble() * 0.2f);
            return (Math.Min(start, Math.Max(0f, songLength - len)), len, 120);
        }

        static void UpdatePreview(float dt, bool wanted)
        {
            Raylib.UpdateMusicStream(previewMusic);
            float t = Raylib.GetMusicTimePlayed(previewMusic);
            float loopEnd = previewStart + previewLength;
            if (t >= loopEnd || t < previewStart - 0.5f) { Raylib.SeekMusicStream(previewMusic, previewStart); t = previewStart; }

            // Quick dip at the loop seam so the jump back doesn't click
            float seam = Math.Min(Math.Clamp((loopEnd - t) / PreviewSeamFade, 0f, 1f), Math.Clamp((t - previewStart) / PreviewSeamFade + 0.15f, 0f, 1f));
            previewVolume = Approach(previewVolume, wanted ? 1f : 0f, dt * (wanted ? 1.2f : 3f));
            Raylib.SetMusicVolume(previewMusic, previewVolume * seam);
        }

        static void StopPreview()
        {
            if (hasPreview)
            {
                Raylib.StopMusicStream(previewMusic);
                Raylib.UnloadMusicStream(previewMusic);
            }
            hasPreview = false;
            previewAudioPath = "";
            previewVolume = 0f;
        }

        // =====================================================================
        //  Menu backdrop shared by every menu screen
        // =====================================================================
        static void DrawMenuBackdrop(float dt, string? worldBackground = null)
        {
            float kick = Ui.BeatPulse;
            Raylib.DrawRectangleGradientV(0, 0, screenWidth, screenHeight, Theme.BackgroundLight, Theme.Background);

            if (worldBackground != null)
            {
                Texture2D bg = GetTexture(worldBackground, false);
                if (bg.Id != 0)
                {
                    float scale = screenHeight / (float)bg.Height * (1.02f + kick * 0.012f);
                    float w = bg.Width * scale;
                    float scroll = (float)(Raylib.GetTime() * 30.0 % w);
                    float yOff = (screenHeight - bg.Height * scale) / 2f;
                    for (int i = 0; i < (int)(screenWidth / w) + 2; i++)
                        Raylib.DrawTextureEx(bg, new Vector2(-scroll + i * w, yOff), 0f, scale, new Color(80, 80, 100, 255));
                }
            }

            triangles.Update(dt, kick, screenWidth, screenHeight);
            triangles.Draw(kick);

            // Beat flash along the bottom edge
            Raylib.DrawRectangleGradientV(0, screenHeight - 160, screenWidth, 160, new Color(0, 0, 0, 0), Theme.WithAlpha(Theme.Accent, 0.10f + kick * 0.12f));
        }

        static void DrawScreenTitle(string title, string? subtitle = null)
        {
            float pop = Ease.OutBack(stateTime * 3f);
            Ui.Text(title, 60, 40, 64 * (0.85f + 0.15f * pop), Theme.Text);
            Raylib.DrawRectangle(60, 116, (int)(140 * Ease.OutCubic(stateTime * 2.5f)), 6, Theme.Accent);
            if (subtitle != null) Ui.Text(subtitle, 62, 130, 26, Theme.TextDim, Ui.Body);
        }

        static void DrawFooter(string left, string right)
        {
            Ui.Text(left, 24, screenHeight - 40, 22, Theme.TextFaint, Ui.Body);
            Ui.TextRight(right, screenWidth - 24, screenHeight - 40, 22, Theme.TextFaint, Ui.Body);
        }

        // =====================================================================
        //  Main menu
        // =====================================================================
        static void DrawMainMenu(float dt)
        {
            DrawMenuBackdrop(dt);
            float kick = Ui.BeatPulse;
            int cx = screenWidth / 2;
            var centre = new Vector2(cx, screenHeight * 0.34f);
            float intro = Ease.OutBack(stateTime * 1.6f);

            // Expanding beat rings
            double beat = hasMenuMusic ? Raylib.GetMusicTimePlayed(menuMusic) * MenuBpm / 60.0 : Raylib.GetTime() * 2;
            for (int k = 0; k < 3; k++)
            {
                float f = (float)((beat + k / 3.0) % 1.0);
                float r = 230 + f * 520;
                Raylib.DrawRing(centre, r - 3, r, 0, 360, 96, Theme.WithAlpha(Theme.Accent, (1 - f) * 0.28f));
            }

            // The "cookie": a big pulsing disc behind the logo
            float discR = (210 + kick * 14) * intro;
            Raylib.DrawCircleV(centre, discR + 18, Theme.WithAlpha(Theme.Accent, 0.18f + kick * 0.2f));
            Raylib.DrawCircleV(centre, discR, Theme.Accent);
            Raylib.DrawCircleV(centre, discR - 14, new Color(30, 30, 45, 255));
            float spin = (float)(Raylib.GetTime() * 25 % 360);
            for (int i = 0; i < 6; i++)
                Raylib.DrawRing(centre, discR - 40, discR - 28, spin + i * 60, spin + i * 60 + 32, 16, Theme.WithAlpha(Theme.Accent, 0.55f));

            Ui.TextGlow("ZIMRA", centre + new Vector2(0, 6), 150, intro * (1f + kick * 0.06f), Theme.Text, Theme.Accent, 1f + kick);
            Ui.TextCentered("play the music. fight the beat.", cx, centre.Y + discR + 34, 28, Theme.TextDim, Ui.Body);

            float by = centre.Y + discR + 100;
            float slide = (1 - Ease.OutCubic((stateTime - 0.15f) * 2.5f)) * 80;
            if (Ui.Button(new Rectangle(cx - 250, by + slide, 500, 96), "Play"))
                BeginTransition(() => SetState(GameState.LevelSelect));
            if (Ui.Button(new Rectangle(cx - 250, by + 124 + slide * 1.5f, 500, 96), "Quit"))
                BeginTransition(() => quitRequested = true);

            if (Raylib.IsKeyPressed(KeyboardKey.Enter)) BeginTransition(() => SetState(GameState.LevelSelect));

            DrawFooter(MidiInput.Status + "   (R to rescan)", "F11 fullscreen   F10 quit");
            if (assetWarning != null)
            {
                int sw = Raylib.GetScreenWidth();
                var box = new Rectangle(40, 24, sw - 80, 56);
                Raylib.DrawRectangleRounded(box, 0.3f, 8, new Color(30, 30, 45, 230));
                Raylib.DrawRectangleRoundedLinesEx(box, 0.3f, 8, 2, Theme.Accent);
                Ui.Text(Ui.Fit(assetWarning, box.Width - 40, 22, Ui.Body), box.X + 20, box.Y + 16, 22, Theme.Text, Ui.Body);
            }
            if (Raylib.IsKeyPressed(KeyboardKey.R)) MidiInput.Restart();
        }

        static void DrawLevelSelect(float dt)
        {
            DrawMenuBackdrop(dt);
            DrawScreenTitle("PLAY", "Choose what to play");
            int cx = screenWidth / 2, cy = screenHeight / 2;

            // The first time, Story Mode opens with the prologue (Story/prologue.json)
            if (Ui.Button(new Rectangle(cx - 260, cy - 90, 520, 100), "Story Mode"))
                GoWithStory(() => SetState(GameState.WorldSelect), "prologue");
            if (Ui.Button(new Rectangle(cx - 260, cy + 40, 520, 100), "Back") || BackPressed())
                BeginTransition(() => SetState(GameState.MainMenu));
            if (Raylib.IsKeyPressed(KeyboardKey.Enter)) GoWithStory(() => SetState(GameState.WorldSelect), "prologue");

            DrawFooter(MidiInput.Status, "Enter select   Esc back");
        }

        static bool BackPressed() => !isTransitioning && (Raylib.IsKeyPressed(KeyboardKey.Escape) || Raylib.IsKeyPressed(KeyboardKey.Backspace));

        // =====================================================================
        //  World select
        // =====================================================================
        static void DrawWorldSelect(float dt)
        {
            WorldInfo world = worlds[selectedWorld - 1];
            DrawMenuBackdrop(dt, world.BackgroundImage);
            DrawScreenTitle("WORLDS", "Pick a planet");
            int cx = screenWidth / 2, cy = screenHeight / 2;

            void Change(int delta)
            {
                int next = Math.Clamp(selectedWorld + delta, 1, worlds.Length);
                if (next == selectedWorld) return;
                selectedWorld = next;
                worldSlide = delta;
            }

            if (Raylib.IsKeyPressed(KeyboardKey.Right)) Change(1);
            if (Raylib.IsKeyPressed(KeyboardKey.Left)) Change(-1);
            if (selectedWorld > 1 && Ui.Button(new Rectangle(60, cy - 75, 90, 150), "<")) Change(-1);
            if (selectedWorld < worlds.Length && Ui.Button(new Rectangle(screenWidth - 150, cy - 75, 90, 150), ">")) Change(1);
            worldSlide = Ease.Damp(worldSlide, 0, 12f, dt);

            int panelW = 760, panelH = 480;
            float offset = worldSlide * 160f;
            var panel = new Rectangle(cx - panelW / 2f + offset, cy - panelH / 2f, panelW, panelH);
            Ui.Panel(panel);

            float alpha = 1f - Math.Min(1f, Math.Abs(worldSlide));
            Color A(Color c) => Theme.WithAlpha(c, 0.3f + 0.7f * alpha);
            float px = panel.X + panelW / 2f;

            Ui.TextCentered($"WORLD {selectedWorld}", px, panel.Y + 36, 30, A(Theme.TextDim));
            Ui.TextCentered(world.Name, px, panel.Y + 78, 84 * (1 + Ui.BeatPulse * 0.03f), A(Theme.Text));
            Ui.TextCentered(world.Theme, px, panel.Y + 186, 30, A(Color.SkyBlue), Ui.Body);
            float ly = panel.Y + 242;
            foreach (var line in world.Description.Split('\n'))
            {
                Ui.TextCentered(line.Trim(), px, ly, 26, A(Theme.TextDim), Ui.Body);
                ly += 34;
            }

            // Page dots
            for (int i = 0; i < worlds.Length; i++)
            {
                var c = new Vector2(px + (i - (worlds.Length - 1) / 2f) * 28, panel.Y + panelH - 132);
                Raylib.DrawCircleV(c, i == selectedWorld - 1 ? 8 : 5, i == selectedWorld - 1 ? Theme.Accent : Theme.TextFaint);
            }

            // First visit to a world plays its intro (Story/world1_intro.json etc.)
            void OpenWorld() { SetState(GameState.LevelList); selectedLevelIndex = -1; selectedLevelPath = ""; previewConfig = null; }
            string introId = $"world{selectedWorld}_intro";
            if (Ui.Button(new Rectangle(px - 170, panel.Y + panelH - 100, 340, 66), "ENTER WORLD") || Raylib.IsKeyPressed(KeyboardKey.Enter))
                GoWithStory(OpenWorld, introId);

            // Rewatch the story for this world once it's been seen (world 1 includes the prologue)
            if (CutsceneSeen(introId) && CutsceneExists(introId) &&
                Ui.Button(new Rectangle(panel.X + panelW - 210, panel.Y + 24, 180, 50), "Replay story", true, 24))
            {
                if (selectedWorld == 1) GoWithStory(() => SetState(GameState.WorldSelect), true, "prologue", introId);
                else GoWithStory(() => SetState(GameState.WorldSelect), true, introId);
            }

            if (Ui.Button(new Rectangle(cx - 110, screenHeight - 120, 220, 60), "Back") || BackPressed())
                BeginTransition(() => SetState(GameState.LevelSelect));

            DrawFooter(MidiInput.Status, "Left/Right switch   Enter open   Esc back");
        }

        // =====================================================================
        //  Level list (map)
        // =====================================================================
        static string LevelPath(int world, int index) => $"Levels/World{world}/level{index + 1}.mid";

        static void SelectLevel(int index)
        {
            selectedLevelIndex = index;
            selectedLevelPath = LevelPath(selectedWorld, index);
            previewConfig = LevelLoader.PeekConfig(Path.ChangeExtension(selectedLevelPath, ".json"));
            StartPreview(selectedLevelPath);
            previewSlide = 1f;
        }

        static void DrawLevelList(float dt)
        {
            WorldInfo world = worlds[Math.Clamp(selectedWorld - 1, 0, worlds.Length - 1)];
            DrawMenuBackdrop(dt, world.BackgroundImage);
            DrawScreenTitle(world.Name.ToUpper(), $"World {selectedWorld}   |   {world.Theme}");
            int cx = screenWidth / 2, cy = screenHeight / 2;

            int mapX = 120, mapY = 210, mapW = cx - 160, mapH = screenHeight - 400;
            Vector2 P(int i) => new(mapX + world.Map[i].X * mapW, mapY + world.Map[i].Y * mapH);
            float time = (float)Raylib.GetTime();

            // Marching dotted path
            for (int i = 0; i < world.Map.Length - 1; i++)
            {
                Vector2 a = P(i), b = P(i + 1);
                float dist = Vector2.Distance(a, b);
                int dots = Math.Max(1, (int)(dist / 26f));
                for (int d = 0; d <= dots; d++)
                {
                    float t = (float)d / dots;
                    float wave = 0.5f + 0.5f * MathF.Sin((i * dots + d) * 0.6f - time * 6f);
                    Raylib.DrawCircleV(Vector2.Lerp(a, b, t), 4 + wave * 2, Theme.WithAlpha(Theme.Accent, 0.35f + 0.4f * wave));
                }
            }

            // Level nodes
            for (int i = 0; i < world.Map.Length; i++)
            {
                Vector2 pos = P(i);
                bool exists = File.Exists(LevelPath(selectedWorld, i));
                int size = i == world.Map.Length - 1 ? 110 : 82;
                float appear = Ease.OutBack((stateTime - i * 0.04f) * 3f);
                float s = size * appear;
                var rect = new Rectangle(pos.X - s / 2, pos.Y - s / 2, s, s);

                if (i == selectedLevelIndex)
                {
                    float r = size * 0.8f + Ui.BeatPulse * 10;
                    Raylib.DrawRing(pos, r, r + 5, 0, 360, 48, Theme.WithAlpha(Theme.Accent, 0.8f));
                }

                if (Ui.Button(rect, exists ? (i + 1).ToString() : "-", exists, size * 0.42f, $"node{i}"))
                    SelectLevel(i);

                if (exists && bestScores.TryGetValue(LevelPath(selectedWorld, i), out var best))
                {
                    var badge = new Vector2(pos.X + s / 2 - 6, pos.Y - s / 2 + 6);
                    Raylib.DrawCircleV(badge, 18, RankColor(best.Rank));
                    Ui.TextCentered(best.Rank, badge.X, badge.Y - 13, 26, Theme.Background);
                }
            }

            if (Ui.Button(new Rectangle(60, screenHeight - 120, 220, 60), "Back") || BackPressed())
                BeginTransition(() => SetState(GameState.WorldSelect));

            // Preview panel
            if (selectedLevelIndex >= 0)
            {
                previewSlide = Ease.Damp(previewSlide, 0, 14f, dt);
                bool exists = File.Exists(selectedLevelPath);
                int panelW = 560, panelH = 470;
                var panel = new Rectangle(cx + 120 + previewSlide * 120, cy - panelH / 2f, panelW, panelH);
                Ui.Panel(panel);

                float x = panel.X + 40, y = panel.Y + 34;
                Ui.Text($"LEVEL {selectedLevelIndex + 1}", x, y, 26, Theme.Accent);
                string name = exists ? (previewConfig?.SongName ?? Path.GetFileNameWithoutExtension(selectedLevelPath)) : "Coming soon";
                Ui.Text(Ui.Fit(name, panelW - 80, 54), x, y + 34, 54, Theme.Text);
                if (exists)
                {
                    Ui.Text($"Artist   {previewConfig?.Author ?? "Unknown"}", x, y + 112, 28, Theme.TextDim, Ui.Body);
                    Ui.Text($"Genre    {previewConfig?.Genre ?? "Unknown"}", x, y + 150, 28, Theme.TextDim, Ui.Body);

                    if (bestScores.TryGetValue(selectedLevelPath, out var best))
                    {
                        Ui.Text("PERSONAL BEST", x, y + 210, 24, Theme.TextFaint);
                        Ui.Text($"{best.Score:N0}", x, y + 240, 46, Theme.Text);
                        Ui.Text($"{best.Accuracy:0.00}%   {best.MaxCombo}x", x, y + 294, 26, Theme.TextDim, Ui.Body);
                        Ui.TextRight(best.Rank, panel.X + panelW - 44, y + 205, 110, RankColor(best.Rank));
                    }
                    else Ui.Text("Not played yet", x, y + 220, 28, Theme.TextFaint, Ui.Body);
                }
                else Ui.Text("Add " + Path.GetFileName(selectedLevelPath) + " to unlock this level.", x, y + 120, 24, Theme.TextDim, Ui.Body);

                if (Ui.Button(new Rectangle(panel.X + 40, panel.Y + panelH - 96, panelW - 80, 68), "PLAY LEVEL", exists) ||
                    (exists && Raylib.IsKeyPressed(KeyboardKey.Enter)))
                {
                    // Story/world1_level3.json plays before level 3 the first time; world1_boss.json before the last level
                    string path = selectedLevelPath;
                    GoWithStory(() => { StartLevel(path); SetState(GameState.Playing); }, LevelStoryIds(selectedWorld, selectedLevelIndex));
                }
            }
            else
            {
                Ui.TextCentered("Pick a level on the map", cx + 400, cy - 20, 34, Theme.TextDim, Ui.Body);
            }

            DrawFooter(MidiInput.Status, "Click a level   Enter play   Esc back");
        }

        // =====================================================================
        //  Results
        // =====================================================================
        static string resultRank = "F";
        static float resultAccuracy;
        static bool resultNewBest, resultFailed;

        static string RankFor(float accuracy) =>
            accuracy >= 95 ? "S" : accuracy >= 85 ? "A" : accuracy >= 75 ? "B" : accuracy >= 60 ? "C" : accuracy >= 50 ? "D" : "F";

        static Color RankColor(string rank) => rank switch
        {
            "S" => Theme.Gold, "A" => Theme.Good, "B" => Theme.Perfect, "C" => Theme.Okay, "D" => new Color(200, 140, 100, 255), _ => Theme.Miss
        };

        static void FinishLevel(bool failed)
        {
            resultFailed = failed;
            resultAccuracy = Accuracy;
            resultRank = failed ? "F" : RankFor(resultAccuracy);
            resultNewBest = false;

            if (!failed && !autoplay && !string.IsNullOrEmpty(currentMidiPath))
            {
                if (!bestScores.TryGetValue(currentMidiPath, out var best) || score > best.Score)
                {
                    bestScores[currentMidiPath] = new BestScore { Score = score, Accuracy = resultAccuracy, Rank = resultRank, MaxCombo = maxCombo };
                    resultNewBest = true;
                    SaveScores();
                }
            }
            SetState(GameState.EndScreen);
        }

        static void DrawEndScreen(float dt)
        {
            DrawMenuBackdrop(dt);
            int cx = screenWidth / 2, cy = screenHeight / 2;
            string song = level.Config.SongName ?? Path.GetFileNameWithoutExtension(currentMidiPath);
            DrawScreenTitle("RESULTS", song);

            // Rank disc
            var rc = new Vector2(cx - 330, cy + 10);
            float pop = Ease.OutBack((stateTime - 0.6f) * 2.2f);
            Color rankCol = RankColor(resultRank);
            float discR = 190 * Math.Max(0, pop) + Ui.BeatPulse * 6;
            if (discR > 1)
            {
                Raylib.DrawCircleV(rc, discR + 16, Theme.WithAlpha(rankCol, 0.25f));
                Raylib.DrawCircleV(rc, discR, Theme.WithAlpha(rankCol, 0.95f));
                Raylib.DrawCircleV(rc, discR - 12, new Color(30, 30, 45, 255));
                Ui.TextPop(resultRank, rc, 240, Math.Max(0, pop), 0, rankCol);
            }
            if (stateTime > 0.6f && stateTime - dt <= 0.6f) { Fx.Burst(rc, rankCol, 60, 900, 10, 0.9f); Fx.Ring(rc, rankCol, 120, 420, 0.6f, 14); }

            // Stats panel
            var panel = new Rectangle(cx - 60, cy - 250, 640, 500);
            Ui.Panel(panel);
            float count = Ease.OutCubic(stateTime / 1.2f);
            float x = panel.X + 44, y = panel.Y + 36;
            Ui.Text("SCORE", x, y, 26, Theme.TextFaint);
            Ui.Text($"{(int)(score * count):N0}", x, y + 28, 76, Theme.Text);
            if (resultNewBest && stateTime > 1.2f)
            {
                float b = 1 + 0.08f * Ui.BeatPulse;
                Ui.TextPop("NEW BEST!", new Vector2(panel.X + panel.Width - 130, y + 66), 30, b, -6, Theme.Gold);
            }

            Ui.Text("ACCURACY", x, y + 130, 24, Theme.TextFaint);
            Ui.Text($"{resultAccuracy * count:0.00}%", x, y + 156, 46, Theme.Text);
            Ui.Text("MAX COMBO", x + 300, y + 130, 24, Theme.TextFaint);
            Ui.Text($"{(int)(maxCombo * count)}x", x + 300, y + 156, 46, Theme.Text);

            string[] labels = { "PERFECT", "GOOD", "OKAY", "MISS" };
            for (int i = 0; i < 4; i++)
            {
                float rx = x + (i % 2) * 300, ry = y + 240 + (i / 2) * 64;
                Color c = Theme.For((Judgement)i);
                Raylib.DrawRectangleRounded(new Rectangle(rx, ry + 6, 10, 38), 0.5f, 6, c);
                Ui.Text(labels[i], rx + 24, ry + 4, 24, Theme.TextDim, Ui.Body);
                Ui.Text($"{(int)(judgeCounts[i] * count)}", rx + 24, ry + 26, 30, c);
            }
            if (resultFailed) Ui.Text("FAILED", x, y + 380, 34, Theme.Miss);

            if (Ui.Button(new Rectangle(panel.X, panel.Y + panel.Height + 40, 300, 72), "Replay") || Raylib.IsKeyPressed(KeyboardKey.Enter))
                BeginTransition(() => { StartLevel(currentMidiPath); SetState(GameState.Playing); });
            if (Ui.Button(new Rectangle(panel.X + 340, panel.Y + panel.Height + 40, 300, 72), "Back to Map") || BackPressed())
                BeginTransition(() => SetState(GameState.LevelList));

            Fx.Update(dt);
            Fx.Draw();
            DrawFooter(MidiInput.Status, "Enter replay   Esc back");
        }

        // =====================================================================
        //  Scores
        // =====================================================================
        static void LoadScores()
        {
            try
            {
                if (File.Exists(ScoresFile))
                    bestScores = JsonSerializer.Deserialize<Dictionary<string, BestScore>>(File.ReadAllText(ScoresFile)) ?? new();
            }
            catch (Exception ex) { Console.WriteLine("[Scores] Could not read scores: " + ex.Message); }
        }

        static void SaveScores()
        {
            try { File.WriteAllText(ScoresFile, JsonSerializer.Serialize(bestScores, new JsonSerializerOptions { WriteIndented = true })); }
            catch (Exception ex) { Console.WriteLine("[Scores] Could not save scores: " + ex.Message); }
        }

        // =====================================================================
        //  Transition: a red and navy diagonal wipe
        // =====================================================================
        static void BeginTransition(Action action)
        {
            if (isTransitioning) return;
            isTransitioning = true;
            transitionProgress = 0f;
            pendingTransitionAction = action;
        }

        static void DrawTransition(float dt)
        {
            if (!isTransitioning) return;
            const float speed = 3.6f;
            transitionProgress += dt * speed;

            if (pendingTransitionAction != null && transitionProgress >= 1f)
            {
                var action = pendingTransitionAction;
                pendingTransitionAction = null;
                action();
                transitionProgress = 1f;
            }
            if (transitionProgress >= 2f) { isTransitioning = false; return; }

            float skew = screenHeight * 0.35f;
            float total = screenWidth + skew * 2;
            // 0..1 covers from the left, 1..2 uncovers to the right
            float p = transitionProgress;
            float lead = Ease.InOutCubic(Math.Min(p, 1f)) * total - skew;
            float tail = p > 1f ? Ease.InOutCubic(p - 1f) * total - skew : -skew * 2;

            void Band(float from, float to, Color c)
            {
                if (to <= from) return;
                Ui.Quad(new Vector2(from + skew, 0), new Vector2(to + skew, 0), new Vector2(to, screenHeight), new Vector2(from, screenHeight), c);
            }
            Band(tail - skew, lead + 40, Theme.Accent);
            Band(tail - skew, lead - 10, new Color(18, 20, 32, 255));
        }

        // =====================================================================
        //  MIDI keyboard input (listens to every connected device)
        // =====================================================================
        static class MidiInput
        {
            static readonly List<InputDevice> devices = new();
            public static readonly ConcurrentQueue<(int pitch, bool pressed)> Queue = new();
            public static string Status { get; private set; } = "MIDI: not started";

            public static void Start()
            {
                try
                {
                    foreach (var device in InputDevice.GetAll())
                    {
                        try
                        {
                            device.EventReceived += OnEvent;
                            device.StartEventsListening();
                            devices.Add(device);
                            Console.WriteLine($"[MIDI] Listening to {device.Name}");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[MIDI] Could not open {device.Name}: {ex.Message}");
                            device.Dispose();
                        }
                    }
                    Status = devices.Count == 0
                        ? "MIDI: no keyboard found, using laptop keys"
                        : "MIDI: " + string.Join(", ", devices.Select(d => d.Name));
                }
                catch (Exception ex)
                {
                    // Happens on systems DryWetMidi doesn't support; the game still works with the laptop keyboard.
                    Status = "MIDI: unavailable, using laptop keys";
                    Console.WriteLine("[MIDI] " + ex.Message);
                }
            }

            static void OnEvent(object? sender, MidiEventReceivedEventArgs e)
            {
                if (e.Event is NoteOnEvent on) Queue.Enqueue((on.NoteNumber, on.Velocity > 0));
                else if (e.Event is NoteOffEvent off) Queue.Enqueue((off.NoteNumber, false));
            }

            public static void Stop()
            {
                foreach (var d in devices)
                {
                    try { d.EventReceived -= OnEvent; d.StopEventsListening(); } catch { }
                    d.Dispose();
                }
                devices.Clear();
            }

            public static void Restart() { Stop(); Start(); }
        }
    }
}
