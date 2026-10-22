using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Raylib_cs;

namespace RhythmGameUI
{
    // =========================================================================
    //  Cutscene data. Everything here is loaded from Story/<id>.json, so the
    //  dialogue, backgrounds, characters and effects can all be edited without
    //  touching the code. See Story/README.md for the format.
    // =========================================================================
#pragma warning disable CS0649   // these fields are filled in by the JSON loader
    class CutsceneFile
    {
        /// <summary>Optional music for the whole cutscene (e.g. "Assets/Music/prologue.mp3"). Otherwise the menu track keeps playing.</summary>
        public string? Music;
        public float MusicVolume = 0.8f;
        /// <summary>Who can appear and speak. Anyone not listed still works, with a grey placeholder.</summary>
        public Dictionary<string, CharacterDef> Characters = new(StringComparer.OrdinalIgnoreCase);
        public List<Slide> Slides = new();
    }

    class CharacterDef
    {
        /// <summary>Name shown in the name tag (defaults to the key, e.g. "Tehilo").</summary>
        public string? DisplayName;
        /// <summary>Colour of the name tag and the placeholder body.</summary>
        public string Color = "#A8ACC2";
        /// <summary>
        /// Portrait image name, looked for in Assets/Story/Characters/. Expressions use name_expression.png
        /// (e.g. Tehilo_happy.png), falling back to the plain portrait, then to the drawn placeholder.
        /// </summary>
        public string? Portrait;
        /// <summary>Placeholder body shape: "quad" (slanted quadrilateral) or "round".</summary>
        public string Shape = "round";
    }

    class Slide
    {
        /// <summary>Image name: Assets/Story/&lt;name&gt;.png, or any Assets/&lt;name&gt;.png such as bg_auvora.</summary>
        public string? Background;
        /// <summary>Gradient used when there's no image (top and bottom colours).</summary>
        public string BackgroundColor = "#101624", BackgroundColor2 = "#05070D";
        /// <summary>How dark the background image is drawn, 0 (black) to 1 (full brightness).</summary>
        public float BackgroundBrightness = 0.7f;
        /// <summary>none | calm | stars | warp | alarm | signal. Can be changed per line too.</summary>
        public string Effect = "none";
        /// <summary>Big title card shown as the slide opens, e.g. "AUVORA".</summary>
        public string? Caption, CaptionSub;
        /// <summary>Characters standing on the left and right.</summary>
        public List<string> Left = new(), Right = new();
        /// <summary>Enemies shown on screen, and whether one of them is a big boss.</summary>
        public int Enemies;
        public bool Boss;
        public List<Line> Lines = new();
    }

    class Line
    {
        /// <summary>Who's talking. Empty or "Narrator" gives a narration box with no portrait.</summary>
        public string Speaker = "";
        public string Text = "";
        /// <summary>The speaker's face for this line: neutral, happy, sad, surprised, angry, scared, hurt.</summary>
        public string? Expression;
        /// <summary>Optional stage changes from this line on (null = leave as it was).</summary>
        public List<string>? Left, Right;
        public int? Enemies;
        public bool? Boss;
        public string? Effect;
        /// <summary>Screen shake strength when the line starts (about 5 to 20).</summary>
        public float Shake;
        /// <summary>Full-screen flash colour when the line starts, e.g. "#FFFFFF".</summary>
        public string? Flash;

        public int? RequirePitch;
        public string? RequirePitchName;
    }

#pragma warning restore CS0649

    partial class Program
    {
        // ---------------- Which cutscenes have been seen ----------------
        const string StoryFolder = "Story";
        const string StoryProgressFile = "story_progress.json";
        class StoryProgress { public List<string> Seen { get; set; } = new(); }
        static StoryProgress storyProgress = new();

        static void LoadStoryProgress()
        {
            try
            {
                if (File.Exists(StoryProgressFile))
                    storyProgress = JsonSerializer.Deserialize<StoryProgress>(File.ReadAllText(StoryProgressFile)) ?? new();
            }
            catch (Exception ex) { Console.WriteLine("[Story] Could not read progress: " + ex.Message); }
        }

        static void SaveStoryProgress()
        {
            try { File.WriteAllText(StoryProgressFile, JsonSerializer.Serialize(storyProgress, new JsonSerializerOptions { WriteIndented = true })); }
            catch (Exception ex) { Console.WriteLine("[Story] Could not save progress: " + ex.Message); }
        }

        static bool CutsceneExists(string id) => File.Exists(Path.Combine(StoryFolder, id + ".json"));
        static bool CutsceneSeen(string id) => storyProgress.Seen.Contains(id, StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Goes to the next screen, playing any of the given cutscenes first if they exist and haven't been seen.
        /// With force = true they play even if seen (for "Replay story").
        /// </summary>
        static void GoWithStory(Action next, bool force, params string[] ids)
        {
            var queue = ids.Where(id => CutsceneExists(id) && (force || !CutsceneSeen(id))).ToList();
            if (queue.Count == 0) { BeginTransition(next); return; }
            BeginTransition(() => StartCutscene(queue, next));
        }

        static void GoWithStory(Action next, params string[] ids) => GoWithStory(next, false, ids);

        /// <summary>Cutscenes to play before a level: Story/world1_level3.json, plus world1_boss.json before the last level.</summary>
        static string[] LevelStoryIds(int world, int index)
        {
            var ids = new List<string> { $"world{world}_level{index + 1}" };
            if (index == worlds[world - 1].Map.Length - 1) ids.Add($"world{world}_boss");
            return ids.ToArray();
        }

        // ---------------- Player state ----------------
        static readonly Queue<string> cutsceneQueue = new();
        static Action? cutsceneDone;
        static string cutsceneId = "";
        static bool cutsceneValid;              // false when the file failed to load (then it isn't marked as seen)
        static CutsceneFile cutscene = new();
        static int slideIndex, lineIndex;
        static float boxTime;   // how long the dialogue box has been up on this slide
        static float slideTime, lineTime, typed, typeDelay, flashAlpha, escPromptTimer, captionTime;
        static Color flashColor;
        static List<string> stageLeft = new(), stageRight = new();
        static int stageEnemies;
        static bool stageBoss;
        static string stageEffect = "none";
        static readonly Dictionary<string, string> expressions = new(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, float> actorAppear = new(StringComparer.OrdinalIgnoreCase);
        static Music cutsceneMusic;
        static bool hasCutsceneMusic;
        static float cutsceneMusicVolume;
        static bool cutsceneEnding;

        const float TypeSpeed = 48f;            // characters per second

        static readonly JsonSerializerOptions storyJson = new()
        {
            IncludeFields = true,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,   // // comments are allowed in the story files
            AllowTrailingCommas = true,
        };

        static void StartCutscene(List<string> ids, Action next)
        {
            cutsceneQueue.Clear();
            foreach (var id in ids) cutsceneQueue.Enqueue(id);
            cutsceneDone = next;
            LoadNextCutscene();
            SetState(GameState.Cutscene);
        }

        static void LoadNextCutscene()
        {
            cutsceneId = cutsceneQueue.Dequeue();
            string path = Path.Combine(StoryFolder, cutsceneId + ".json");
            try
            {
                cutscene = JsonSerializer.Deserialize<CutsceneFile>(File.ReadAllText(path), storyJson) ?? new();
                cutscene.Characters = new Dictionary<string, CharacterDef>(cutscene.Characters, StringComparer.OrdinalIgnoreCase);
                cutsceneValid = cutscene.Slides.Any(s => s.Lines.Count > 0);
                if (!cutsceneValid) throw new Exception("it has no slides with lines in it.");
            }
            catch (Exception ex)
            {
                // Show the problem on screen rather than silently skipping, so a typo in the JSON is easy to find.
                Console.WriteLine($"[Story] {path}: {ex.Message}");
                cutsceneValid = false;
                cutscene = new CutsceneFile
                {
                    Slides = { new Slide { Lines = { new Line { Text = $"Couldn't read {path}: {ex.Message}" } } } }
                };
            }

            foreach (var s in cutscene.Slides)
                foreach (var l in s.Lines) l.Text = CleanText(l.Text);

            StopCutsceneMusic();
            if (!string.IsNullOrWhiteSpace(cutscene.Music) && File.Exists(cutscene.Music))
            {
                cutsceneMusic = Raylib.LoadMusicStream(cutscene.Music);
                if (cutsceneMusic.FrameCount > 0)
                {
                    cutsceneMusic.Looping = true;
                    hasCutsceneMusic = true;
                    cutsceneMusicVolume = 0f;
                    Raylib.SetMusicVolume(cutsceneMusic, 0f);
                    Raylib.PlayMusicStream(cutsceneMusic);
                }
            }

            expressions.Clear();
            actorAppear.Clear();
            Fx.Clear();
            cutsceneEnding = false;
            escPromptTimer = 0f;
            BeginSlide(0);
        }

        /// <summary>
        /// The fonts only contain plain ASCII, so swap the "smart" punctuation that macOS and Word insert
        /// automatically for plain versions (otherwise they'd show up as '?').
        /// </summary>
        static string CleanText(string s) => s
            .Replace('\u2018', '\'').Replace('\u2019', '\'').Replace('\u201C', '"').Replace('\u201D', '"')
            .Replace("\u2026", "...").Replace('\u2013', '-').Replace("\u2014", " - ").Replace('\u00A0', ' ');

        static void BeginSlide(int index)
        {
            slideIndex = index;
            var slide = cutscene.Slides[index];
            stageLeft = new List<string>(slide.Left);
            stageRight = new List<string>(slide.Right);
            stageEnemies = slide.Enemies;
            stageBoss = slide.Boss;
            stageEffect = slide.Effect;
            slideTime = 0f;
            boxTime = 0f;
            captionTime = string.IsNullOrWhiteSpace(slide.Caption) ? 0f : 2.2f;
            BeginLine(0);
        }

        static void BeginLine(int index)
        {
            lineIndex = index;
            var slide = cutscene.Slides[slideIndex];
            if (slide.Lines.Count == 0) { lineTime = 0; typed = 0; return; }
            var line = slide.Lines[index];

            if (line.Left != null) stageLeft = new List<string>(line.Left);
            if (line.Right != null) stageRight = new List<string>(line.Right);
            if (line.Enemies.HasValue) stageEnemies = line.Enemies.Value;
            if (line.Boss.HasValue) stageBoss = line.Boss.Value;
            if (!string.IsNullOrWhiteSpace(line.Effect)) stageEffect = line.Effect;
            if (!IsNarrator(line.Speaker) && line.Expression != null) expressions[line.Speaker] = line.Expression;

            if (line.Shake > 0) Fx.Shake(line.Shake);
            if (!string.IsNullOrWhiteSpace(line.Flash)) { flashColor = Ui.ParseHex(line.Flash, Color.White); flashAlpha = 1f; }

            lineTime = 0f;
            typed = 0f;
            typeDelay = 0f;
        }

        static bool IsNarrator(string? speaker) => string.IsNullOrWhiteSpace(speaker) || speaker.Equals("Narrator", StringComparison.OrdinalIgnoreCase);

        static Line? CurrentLine
        {
            get
            {
                var slide = cutscene.Slides[slideIndex];
                return slide.Lines.Count > 0 ? slide.Lines[lineIndex] : null;
            }
        }

        static bool LineFinished => CurrentLine == null || typed >= CurrentLine.Text.Length;

        static void Advance()
        {
            if (!LineFinished) { typed = CurrentLine!.Text.Length; return; }   // first press finishes the line
            var slide = cutscene.Slides[slideIndex];
            if (lineIndex + 1 < slide.Lines.Count) BeginLine(lineIndex + 1);
            else if (slideIndex + 1 < cutscene.Slides.Count) BeginSlide(slideIndex + 1);
            else EndCutscene(false);
        }

        static void EndCutscene(bool skipped)
        {
            if (cutsceneEnding) return;
            if (cutsceneValid && !CutsceneSeen(cutsceneId))
            {
                storyProgress.Seen.Add(cutsceneId);
                SaveStoryProgress();
            }
            if (!skipped && cutsceneQueue.Count > 0)
            {
                BeginTransition(LoadNextCutscene);
                return;
            }
            // Skipping skips the rest of the queue too, but still counts them as seen
            if (skipped)
                while (cutsceneQueue.Count > 0)
                {
                    string id = cutsceneQueue.Dequeue();
                    if (!CutsceneSeen(id)) storyProgress.Seen.Add(id);
                }
            SaveStoryProgress();
            cutsceneEnding = true;
            var next = cutsceneDone ?? (() => SetState(GameState.MainMenu));
            BeginTransition(() => { StopCutsceneMusic(); Fx.Clear(); next(); });
        }

        static void StopCutsceneMusic()
        {
            if (!hasCutsceneMusic) return;
            Raylib.StopMusicStream(cutsceneMusic);
            Raylib.UnloadMusicStream(cutsceneMusic);
            hasCutsceneMusic = false;
        }

        // =====================================================================
        //  Drawing
        // =====================================================================
        static void DrawCutscene(float dt)
        {
            slideTime += dt;
            lineTime += dt;
            captionTime = Math.Max(0, captionTime - dt);
            flashAlpha = Math.Max(0, flashAlpha - dt * 2.2f);
            escPromptTimer = Math.Max(0, escPromptTimer - dt);
            Fx.Update(dt);

            if (hasCutsceneMusic)
            {
                cutsceneMusicVolume = Ease.Damp(cutsceneMusicVolume, cutsceneEnding ? 0f : cutscene.MusicVolume, 3f, dt);
                Raylib.SetMusicVolume(cutsceneMusic, cutsceneMusicVolume);
                Raylib.UpdateMusicStream(cutsceneMusic);
            }

            var slide = cutscene.Slides[slideIndex];
            var line = CurrentLine;
            bool captionShowing = captionTime > 0.25f;
            if (!captionShowing) boxTime += dt;

            // Typewriter, with short pauses after punctuation so it reads like speech
            if (line != null && !captionShowing && !isTransitioning)
            {
                bool fast = Raylib.IsKeyDown(KeyboardKey.LeftControl) || Raylib.IsKeyDown(KeyboardKey.RightControl);
                typeDelay -= dt * (fast ? 6f : 1f);
                while (typeDelay <= 0 && typed < line.Text.Length)
                {
                    char c = line.Text[(int)typed];
                    typed += 1;
                    typeDelay += c is '.' or '!' or '?' ? 0.22f : c is ',' or ';' or ':' ? 0.09f : 1f / TypeSpeed;
                }
                if (fast && LineFinished && lineTime > 0.15f) Advance();   // hold Ctrl to fast-forward
            }

            // ---------- Input ----------
            var skipRect = new Rectangle(screenWidth - 220, 30, 180, 62);
            bool overSkip = Ui.MouseOver(skipRect);

            // Drain MIDI to prevent backlog, and check if they played the required note
            bool correctNotePlayed = false;
            while (MidiInput.Queue.TryDequeue(out var e))
            {
                if (e.pressed && line != null && line.RequirePitch == e.pitch) correctNotePlayed = true;
            }
            // Check Laptop fallback keys too
            if (line != null && line.RequirePitch.HasValue)
            {
                foreach (var (key, pitch) in LaptopKeys)
                    if (Raylib.IsKeyPressed(key) && pitch == line.RequirePitch.Value) correctNotePlayed = true;
            }

            if (!isTransitioning && !cutsceneEnding)
            {
                bool clickOrSpace = Raylib.IsKeyPressed(KeyboardKey.Space) || Raylib.IsKeyPressed(KeyboardKey.Enter) ||
                                    Raylib.IsKeyPressed(KeyboardKey.Right) || (Raylib.IsMouseButtonPressed(MouseButton.Left) && !overSkip);

                if (correctNotePlayed && LineFinished)
                {
                    // Success! Flash green and automatically advance
                    flashColor = Theme.Good; 
                    flashAlpha = 0.5f;
                    Fx.Shake(4);
                    Advance();
                }
                else if (clickOrSpace)
                {
                    if (captionShowing) captionTime = Math.Min(captionTime, 0.75f); 
                    else if (!LineFinished) Advance(); // Allow skipping the typewriter effect
                    else if (line == null || !line.RequirePitch.HasValue) Advance(); // Normal advance
                    else Fx.Shake(3); // Shake screen slightly to say "No, you must play the note!"
                }

                if (Raylib.IsKeyPressed(KeyboardKey.Escape) || Raylib.IsKeyPressed(KeyboardKey.Backspace))
                {
                    // Esc twice to skip, so one stray press doesn't throw the whole scene away
                    if (escPromptTimer > 0) EndCutscene(true);
                    else escPromptTimer = 2f;
                }
            }

            // ---------- Scene ----------
            var shake = Fx.ShakeOffset;
            DrawCutsceneBackground(slide, shake);
            DrawCutsceneEffect(stageEffect, dt);
            DrawCutsceneEnemies(shake);
            string? speaker = line != null && !IsNarrator(line.Speaker) ? line.Speaker : null;
            DrawActors(stageLeft, true, speaker, dt, shake);
            DrawActors(stageRight, false, speaker, dt, shake);
            Fx.Draw();

            // Letterbox bars for a cinematic frame
            float bars = 70 * Ease.OutCubic(slideTime * 2f);
            Raylib.DrawRectangle(0, 0, screenWidth, (int)bars, Color.Black);
            Raylib.DrawRectangle(0, screenHeight - (int)bars, screenWidth, (int)bars, Color.Black);

            if (line != null && !captionShowing) DrawDialogueBox(line);
            DrawCaption(slide);

            if (flashAlpha > 0) Raylib.DrawRectangle(0, 0, screenWidth, screenHeight, Theme.WithAlpha(flashColor, flashAlpha * 0.85f));

            if (Ui.Button(skipRect, "Skip", true, 30) && !isTransitioning) EndCutscene(true);
            if (escPromptTimer > 0)
                Ui.TextRight("Press Esc again to skip", screenWidth - 40, 104, 24, Theme.WithAlpha(Theme.Text, Math.Min(1, escPromptTimer * 2)), Ui.Body);
            Ui.Text("Space / Click  next      Hold Ctrl  fast-forward      Esc  skip", 30, screenHeight - 46, 22, Theme.WithAlpha(Theme.TextFaint, 0.9f), Ui.Body);
        }

        static Texture2D StoryTexture(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return default;
            string key = "story:" + name;
            if (textureCache.TryGetValue(key, out var cached)) return cached;
            string file = Path.GetFileNameWithoutExtension(name);
            string? path = new[] { Path.Combine("Assets", "Story", file + ".png"), Path.Combine("Assets", "Story", "Characters", file + ".png") }
                .FirstOrDefault(File.Exists);
            Texture2D tex = path != null ? Raylib.LoadTexture(path) : GetTexture(file, false);
            if (path != null && tex.Id != 0) Raylib.SetTextureFilter(tex, TextureFilter.Bilinear);
            textureCache[key] = tex;
            return tex;
        }

        static void DrawCutsceneBackground(Slide slide, Vector2 shake)
        {
            Raylib.DrawRectangleGradientV(0, 0, screenWidth, screenHeight,
                Ui.ParseHex(slide.BackgroundColor, Theme.BackgroundLight), Ui.ParseHex(slide.BackgroundColor2, Theme.Background));

            var bg = StoryTexture(slide.Background);
            if (bg.Id == 0) return;
            // Cover the screen, with a slow push-in (Ken Burns) so still images feel alive
            float zoom = 1.04f + Math.Min(slideTime, 30f) * 0.0025f;
            float scale = MathF.Max(screenWidth / (float)bg.Width, screenHeight / (float)bg.Height) * zoom;
            float w = bg.Width * scale, h = bg.Height * scale;
            var dest = new Rectangle((screenWidth - w) / 2 + shake.X, (screenHeight - h) / 2 + shake.Y, w, h);
            byte b = (byte)(255 * Math.Clamp(slide.BackgroundBrightness, 0f, 1f));
            Raylib.DrawTexturePro(bg, new Rectangle(0, 0, bg.Width, bg.Height), dest, Vector2.Zero, 0f, new Color(b, b, b, (byte)255));
        }

        static void DrawCutsceneEffect(string effect, float dt)
        {
            float t = (float)Raylib.GetTime();
            int W = screenWidth, H = screenHeight;
            switch (effect.ToLowerInvariant())
            {
                case "calm":
                    // Slow glowing motes drifting upwards
                    for (int i = 0; i < 46; i++)
                    {
                        float x = (i * 137.5f % W + MathF.Sin(t * 0.4f + i) * 30 + W) % W;
                        float y = H - ((t * (14 + i % 7 * 4) + i * 71) % (H + 40));
                        float a = 0.25f + 0.25f * MathF.Sin(t * 1.5f + i);
                        Raylib.DrawCircleV(new Vector2(x, y), 2 + i % 3, Theme.WithAlpha(new Color(255, 240, 200, 255), a));
                    }
                    break;

                case "stars":
                    for (int i = 0; i < 120; i++)
                    {
                        float x = (i * 97.3f + t * (4 + i % 5 * 3)) % W;
                        float y = i * 53.7f % H;
                        float a = 0.3f + 0.4f * MathF.Abs(MathF.Sin(t * 0.8f + i));
                        Raylib.DrawCircleV(new Vector2(x, y), 1 + i % 3 * 0.6f, Theme.WithAlpha(Color.White, a));
                    }
                    break;

                case "warp":
                    // Stars streaking past: fleeing through space
                    var centre = new Vector2(W / 2f, H / 2f);
                    for (int i = 0; i < 140; i++)
                    {
                        float ang = i * 2.399f;
                        float d = (t * 0.6f + i * 0.0713f) % 1f;
                        float r0 = d * d * W * 0.9f, r1 = r0 + 20 + d * 160;
                        var dir = new Vector2(MathF.Cos(ang), MathF.Sin(ang));
                        Raylib.DrawLineEx(centre + dir * r0, centre + dir * r1, 1 + d * 3, Theme.WithAlpha(new Color(190, 220, 255, 255), d));
                    }
                    break;

                case "alarm":
                    // Pulsing red sky and embers rising from below
                    float pulse = 0.5f + 0.5f * MathF.Sin(t * 5f);
                    Raylib.DrawRectangleGradientV(0, 0, W, H, Theme.WithAlpha(Theme.Accent, 0.10f + 0.12f * pulse), Theme.WithAlpha(new Color(120, 0, 10, 255), 0.35f));
                    for (int i = 0; i < 60; i++)
                    {
                        float x = (i * 151.3f + MathF.Sin(t * 2 + i) * 40 + W) % W;
                        float y = H - ((t * (120 + i % 9 * 25) + i * 97) % (H + 60));
                        Raylib.DrawCircleV(new Vector2(x, y), 2 + i % 3, Theme.WithAlpha(new Color(255, 150 + i % 4 * 25, 60, 255), 0.7f));
                    }
                    if (Raylib.GetRandomValue(0, 90) == 0) Fx.Shake(5);
                    break;

                case "signal":
                    DrawSkySignal(t);
                    break;
            }
        }

        /// <summary>The antagonist's signal: a glowing stave across the sky with notes scrolling along it.</summary>
        static void DrawSkySignal(float t)
        {
            int W = screenWidth;
            float top = 150, gap = 26;
            float glow = 0.55f + 0.25f * MathF.Sin(t * 3f);
            Color line = Theme.WithAlpha(Theme.Accent, glow);
            for (int i = 0; i < 5; i++)
            {
                float y = top + i * gap + MathF.Sin(t * 0.8f + i * 0.3f) * 4;
                Raylib.DrawLineEx(new Vector2(0, y), new Vector2(W, y), 3, line);
                Raylib.DrawLineEx(new Vector2(0, y), new Vector2(W, y), 10, Theme.WithAlpha(Theme.Accent, 0.08f));
            }
            if (texTrebleClef.Id != 0) Ui.TextureCentered(texTrebleClef, 120, top + gap * 2.2f, 0.32f, Theme.WithAlpha(Theme.Accent, glow + 0.2f));

            // Notes appear at the right and travel left, like an incoming score
            for (int i = 0; i < 14; i++)
            {
                float x = W - ((t * 140 + i * 160) % (W - 200));
                int step = (i * 5 + 3) % 9;   // spread over the stave
                float y = top + step * gap / 2f;
                float a = Math.Clamp((W - x) / 200f, 0f, 1f) * Math.Clamp((x - 220) / 120f, 0f, 1f);
                if (texHeadCrotchet.Id != 0) Ui.TextureCentered(texHeadCrotchet, x, y, 0.55f, Theme.WithAlpha(Color.White, a));
                else Raylib.DrawEllipse((int)x, (int)y, 14, 10, Theme.WithAlpha(Color.White, a));
                Raylib.DrawLineEx(new Vector2(x + 14, y), new Vector2(x + 14, y - 70), 3, Theme.WithAlpha(Color.White, a));
            }
        }

        static void DrawCutsceneEnemies(Vector2 shake)
        {
            if (stageEnemies <= 0 && !stageBoss) return;
            float t = (float)Raylib.GetTime();
            float ground = screenHeight - 360;
            if (stageBoss)
            {
                float s = 4.2f + MathF.Sin(t * 2) * 0.1f;
                DrawStoryEnemy(new Vector2(screenWidth * 0.5f, ground - 40) + shake, s, new Color(255, 200, 200, 255));
            }
            for (int i = 0; i < stageEnemies; i++)
            {
                float x = screenWidth * (0.5f + (i - (stageEnemies - 1) / 2f) * 0.09f);
                float hop = MathF.Abs(MathF.Sin(t * 5 + i * 1.3f)) * 26;
                DrawStoryEnemy(new Vector2(x, ground - hop + (i % 2) * 30) + shake, 1.6f, Color.White);
            }
        }

        static void DrawStoryEnemy(Vector2 feet, float scale, Color tint)
        {
            if (texEnemy.Id != 0)
            {
                float h = texEnemy.Height * scale * 80f / Math.Max(1, texEnemy.Height);
                Ui.TextureCentered(texEnemy, feet.X, feet.Y - h / 2, 80f * scale / Math.Max(1, texEnemy.Height), tint);
            }
            else Raylib.DrawRectangleRounded(new Rectangle(feet.X - 30 * scale, feet.Y - 60 * scale, 60 * scale, 60 * scale), 0.3f, 8, new Color(160, 40, 200, 255));
        }

        // ---------------- Characters ----------------
        static CharacterDef CharacterFor(string name) =>
            cutscene.Characters.TryGetValue(name, out var def) ? def : new CharacterDef();

        static void DrawActors(List<string> names, bool left, string? speaker, float dt, Vector2 shake)
        {
            for (int i = 0; i < names.Count; i++)
            {
                string name = names[i];
                actorAppear.TryGetValue(name, out float appear);
                appear = Ease.Damp(appear, 1f, 9f, dt);
                actorAppear[name] = appear;

                bool talking = speaker != null && speaker.Equals(name, StringComparison.OrdinalIgnoreCase);
                float baseX = left ? screenWidth * (0.25f + i * 0.13f) : screenWidth * (0.8f - i * 0.13f);
                float enter = (1 - Ease.OutCubic(appear)) * (left ? -500 : 500);
                float bob = MathF.Sin((float)Raylib.GetTime() * (talking ? 6f : 2f) + i) * (talking ? 6f : 3f);
                var feet = new Vector2(baseX + enter, screenHeight - 300 + bob) + shake;
                float brightness = speaker == null || talking ? 1f : 0.5f;
                expressions.TryGetValue(name, out string? face);
                DrawCharacter(name, CharacterFor(name), feet, talking ? 1.05f : 1f, brightness, face ?? "neutral", !left);
            }

            // Characters that left the stage start their slide-in again next time
            foreach (var key in actorAppear.Keys.ToList())
                if (!stageLeft.Contains(key, StringComparer.OrdinalIgnoreCase) && !stageRight.Contains(key, StringComparer.OrdinalIgnoreCase))
                    actorAppear.Remove(key);
        }

        /// <summary>
        /// Draws a character: their portrait image if there is one, otherwise a placeholder body with a face.
        /// Tehilo's placeholder is the blue quadrilateral from the game, Elyra's is the pink one.
        /// </summary>
        static void DrawCharacter(string name, CharacterDef def, Vector2 feet, float scale, float brightness, string face, bool facingLeft)
        {
            Color baseCol = Ui.ParseHex(def.Color, Theme.TextDim);
            Color Shade(Color c) => new((int)(c.R * brightness), (int)(c.G * brightness), (int)(c.B * brightness), (int)c.A);

            // Real portrait if available: Assets/Story/Characters/<Portrait>_<face>.png, then <Portrait>.png
            string portrait = def.Portrait ?? name;
            var tex = StoryTexture($"{portrait}_{face}");
            if (tex.Id == 0) tex = StoryTexture(portrait);
            if (tex.Id != 0)
            {
                float h = 560 * scale, s = h / tex.Height, w = tex.Width * s;
                var src = new Rectangle(0, 0, facingLeft ? -tex.Width : tex.Width, tex.Height);
                Raylib.DrawTexturePro(tex, src, new Rectangle(feet.X - w / 2, feet.Y - h, w, h), Vector2.Zero, 0f, Shade(Color.White));
                return;
            }

            float bw = 230 * scale, bh = 420 * scale;
            Raylib.DrawEllipse((int)feet.X, (int)feet.Y + 6, bw * 0.6f, 16, new Color(0, 0, 0, 90));   // shadow
            Color body = Shade(baseCol);
            Color edge = Shade(new Color(255, 255, 255, 200));
            float top = feet.Y - bh;
            if (def.Shape.Equals("quad", StringComparison.OrdinalIgnoreCase))
            {
                float lean = 26 * scale * (facingLeft ? -1 : 1);
                Vector2 tl = new(feet.X - bw / 2 + lean, top), tr = new(feet.X + bw / 2 + lean, top + 14 * scale),
                        br = new(feet.X + bw / 2, feet.Y), bl = new(feet.X - bw / 2, feet.Y);
                Ui.Quad(tl, tr, br, bl, body);
                Raylib.DrawLineEx(tl, tr, 4, edge); Raylib.DrawLineEx(tr, br, 4, edge);
                Raylib.DrawLineEx(br, bl, 4, edge); Raylib.DrawLineEx(bl, tl, 4, edge);
            }
            else
            {
                var r = new Rectangle(feet.X - bw / 2, top, bw, bh);
                Raylib.DrawRectangleRounded(r, 0.45f, 16, body);
                Raylib.DrawRectangleRoundedLinesEx(r, 0.45f, 16, 4, edge);
                // A little tuft on top so the two placeholders don't look identical
                Raylib.DrawTriangle(new Vector2(feet.X - 10, top + 8), new Vector2(feet.X + 34, top + 4), new Vector2(feet.X + 4, top - 40 * scale), body);
            }

            DrawFace(new Vector2(feet.X + (facingLeft ? -14 : 14) * scale, top + bh * 0.3f), scale, face, brightness);
        }

        static void DrawFace(Vector2 c, float s, string face, float brightness)
        {
            Color white = new((int)(250 * brightness), (int)(250 * brightness), (int)(255 * brightness), 255);
            Color ink = new(20, 20, 34, 255);
            float ex = 34 * s, ey = 0, er = 18 * s;
            Vector2 le = c + new Vector2(-ex, ey), re = c + new Vector2(ex, ey);
            Vector2 mouth = c + new Vector2(0, 62 * s);
            float t = (float)Raylib.GetTime();
            bool blink = t % 3.7f < 0.12f;

            switch (face.ToLowerInvariant())
            {
                case "happy":
                    foreach (var e in new[] { le, re }) Raylib.DrawRing(e + new Vector2(0, 6 * s), er - 5, er, 180, 360, 16, ink);
                    Raylib.DrawCircleSector(mouth - new Vector2(0, 6 * s), 22 * s, 0, 180, 16, ink);
                    break;
                case "sad":
                    foreach (var e in new[] { le, re }) { Raylib.DrawCircleV(e, er, white); Raylib.DrawCircleV(e + new Vector2(0, 5 * s), er * 0.45f, ink); }
                    Raylib.DrawLineEx(le + new Vector2(-er, -er - 4), le + new Vector2(er, -er - 14), 5, ink);
                    Raylib.DrawLineEx(re + new Vector2(-er, -er - 14), re + new Vector2(er, -er - 4), 5, ink);
                    Raylib.DrawRing(mouth + new Vector2(0, 18 * s), 16 * s, 21 * s, 200, 340, 16, ink);
                    break;
                case "surprised":
                    foreach (var e in new[] { le, re }) { Raylib.DrawCircleV(e, er * 1.25f, white); Raylib.DrawCircleV(e, er * 0.4f, ink); }
                    Raylib.DrawEllipse((int)mouth.X, (int)mouth.Y, 12 * s, 16 * s, ink);
                    break;
                case "angry":
                    foreach (var e in new[] { le, re }) { Raylib.DrawCircleV(e, er, white); Raylib.DrawCircleV(e + new Vector2(0, 3), er * 0.5f, ink); }
                    Raylib.DrawLineEx(le + new Vector2(-er, -er - 12), le + new Vector2(er, -er + 2), 7, ink);
                    Raylib.DrawLineEx(re + new Vector2(-er, -er + 2), re + new Vector2(er, -er - 12), 7, ink);
                    Raylib.DrawLineEx(mouth + new Vector2(-18 * s, 4), mouth + new Vector2(18 * s, 0), 6, ink);
                    break;
                case "scared":
                    float jitter = MathF.Sin(t * 40) * 1.5f;
                    foreach (var e in new[] { le, re }) { Raylib.DrawCircleV(e, er * 1.15f, white); Raylib.DrawCircleV(e + new Vector2(jitter, 0), er * 0.25f, ink); }
                    for (int k = 0; k < 4; k++)   // wobbly mouth
                        Raylib.DrawLineEx(mouth + new Vector2(-20 * s + k * 10 * s, k % 2 * 6), mouth + new Vector2(-10 * s + k * 10 * s, (k + 1) % 2 * 6), 5, ink);
                    break;
                case "hurt":
                    foreach (var e in new[] { le, re })
                    {
                        Raylib.DrawLineEx(e + new Vector2(-er * 0.8f, -er * 0.6f), e + new Vector2(er * 0.8f, 0), 6, ink);
                        Raylib.DrawLineEx(e + new Vector2(er * 0.8f, 0), e + new Vector2(-er * 0.8f, er * 0.6f), 6, ink);
                    }
                    Raylib.DrawRing(mouth + new Vector2(0, 16 * s), 14 * s, 19 * s, 200, 340, 16, ink);
                    break;
                default: // neutral
                    foreach (var e in new[] { le, re })
                    {
                        if (blink) Raylib.DrawLineEx(e - new Vector2(er, 0), e + new Vector2(er, 0), 5, ink);
                        else { Raylib.DrawCircleV(e, er, white); Raylib.DrawCircleV(e + new Vector2(3, 2), er * 0.5f, ink); }
                    }
                    Raylib.DrawLineEx(mouth - new Vector2(14 * s, 0), mouth + new Vector2(14 * s, 0), 5, ink);
                    break;
            }
        }

        // ---------------- Dialogue box ----------------
        static void DrawDialogueBox(Line line)
        {
            bool narrator = IsNarrator(line.Speaker);
            float boxW = Math.Min(1500, screenWidth - 160), boxH = 230;
            float rise = (1 - Ease.OutCubic(boxTime * 3f)) * 60;
            var box = new Rectangle((screenWidth - boxW) / 2, screenHeight - boxH - 90 + rise, boxW, boxH);
            Ui.Panel(box, 0.12f, 4);   // same #FA3834 border and dark blue-grey fill as the menus

            Color nameCol = Theme.Accent;
            if (!narrator)
            {
                var def = CharacterFor(line.Speaker);
                nameCol = Ui.ParseHex(def.Color, Theme.Accent);
                string shown = (def.DisplayName ?? line.Speaker).ToUpperInvariant();
                var m = Ui.Measure(shown, 34);
                var tag = new Rectangle(box.X + 40, box.Y - 30, m.X + 48, 56);
                Raylib.DrawRectangleRounded(tag, 0.5f, 10, nameCol);
                Raylib.DrawRectangleRoundedLinesEx(tag, 0.5f, 10, 3, Theme.Background);
                Ui.Text(shown, tag.X + 24, tag.Y + 11, 34, Theme.Background);
            }

            // Wrapped, typed-out text
            string visible = line.Text[..Math.Min(line.Text.Length, (int)typed)];
            float size = 36, textX = box.X + 48, textY = box.Y + (narrator ? 44 : 50), maxW = boxW - 96;
            var lines = Wrap(line.Text, maxW, size, Ui.Body);
            int remaining = visible.Length;
            foreach (var row in lines)
            {
                if (remaining <= 0) break;
                string part = row[..Math.Min(row.Length, remaining)];
                remaining -= row.Length + 1;   // +1 for the space the wrap removed
                Color col = narrator ? new Color(220, 222, 236, 255) : Theme.Text;
                // Narration is centred; the row's position is fixed by its full width so it doesn't slide while typing
                float rowX = narrator ? box.X + boxW / 2 - Ui.Measure(row, size, Ui.Body).X / 2 : textX;
                Ui.Text(part, rowX, textY, size, col, Ui.Body);
                textY += size * 1.25f;
            }

            // "Next" arrow once the line is finished
            if (LineFinished)
            {
                if (line.RequirePitch.HasValue)
                {
                    float glow = 0.7f + MathF.Abs(MathF.Sin((float)Raylib.GetTime() * 4)) * 0.3f;
                    string noteName = string.IsNullOrWhiteSpace(line.RequirePitchName) ? $"Pitch {line.RequirePitch.Value}" : line.RequirePitchName;
                    string prompt = $"[ Play {noteName} to continue ]";
                    var m = Ui.Measure(prompt, 26, Ui.Body);
                    Ui.Text(prompt, box.X + boxW - m.X - 30, box.Y + boxH - 40, 26, Theme.WithAlpha(Theme.Accent, glow), Ui.Body);
                }
                else
                {
                    float bounce = MathF.Abs(MathF.Sin((float)Raylib.GetTime() * 5)) * 8;
                    var p = new Vector2(box.X + boxW - 52, box.Y + boxH - 40 + bounce);
                    Raylib.DrawTriangle(p + new Vector2(-14, -10), p + new Vector2(0, 8), p + new Vector2(14, -10), nameCol);
                }
            }

            // Progress through the cutscene
            int total = cutscene.Slides.Sum(s => s.Lines.Count);
            int done = cutscene.Slides.Take(slideIndex).Sum(s => s.Lines.Count) + lineIndex + 1;
            Raylib.DrawRectangle((int)box.X + 30, (int)(box.Y + boxH - 14), (int)((boxW - 60) * done / Math.Max(1, total)), 4, Theme.WithAlpha(nameCol, 0.6f));
        }

        static List<string> Wrap(string text, float width, float size, Font font)
        {
            var rows = new List<string>();
            foreach (var paragraph in text.Split('\n'))
            {
                string row = "";
                foreach (var word in paragraph.Split(' '))
                {
                    string attempt = row.Length == 0 ? word : row + " " + word;
                    if (row.Length > 0 && Ui.Measure(attempt, size, font).X > width) { rows.Add(row); row = word; }
                    else row = attempt;
                }
                rows.Add(row);
            }
            return rows;
        }

        static void DrawCaption(Slide slide)
        {
            if (string.IsNullOrWhiteSpace(slide.Caption) || captionTime <= 0) return;
            float a = Math.Clamp((captionTime - 0.25f) / 0.5f, 0f, 1f) * Ease.OutCubic(slideTime * 2.5f);
            Raylib.DrawRectangle(0, 0, screenWidth, screenHeight, Theme.WithAlpha(Color.Black, 0.35f * a));
            float cy = screenHeight * 0.38f;
            float grow = 0.9f + 0.1f * Ease.OutBack(slideTime * 2f);
            Ui.TextGlow(slide.Caption!, new Vector2(screenWidth / 2f, cy), 150, grow, Theme.WithAlpha(Theme.Text, a), Theme.WithAlpha(Theme.Accent, a), 1f);
            float lineW = 420 * Ease.OutCubic(slideTime * 2f);
            Raylib.DrawRectangle((int)(screenWidth / 2f - lineW / 2), (int)cy + 90, (int)lineW, 6, Theme.WithAlpha(Theme.Accent, a));
            if (!string.IsNullOrWhiteSpace(slide.CaptionSub))
                Ui.TextCentered(slide.CaptionSub!, screenWidth / 2f, cy + 112, 36, Theme.WithAlpha(Theme.TextDim, a), Ui.Body);
        }
    }
}
