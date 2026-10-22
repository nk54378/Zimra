using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Raylib_cs;

namespace RhythmGameUI
{
    /// <summary>
    /// In-game level editor (F1 while playing).
    /// Pick a tool, click on the stave to place things, drag to move them, then Ctrl/Cmd+S to save.
    /// Everything is snapped to the beat grid and written as Bar / Beat / StaffStep, the same format as the hand-written JSON.
    /// </summary>
    class LevelEditor
    {
        // =====================================================================
        //  Public surface used by the game
        // =====================================================================
        public bool IsOpen { get; set; }
        public bool Paused { get; private set; } = true;
        public bool Dirty { get; private set; }
        public bool HasLevel => root != null;
        public string JsonPath { get; private set; } = "";
        public int PanelWidth => 480;

        /// <summary>True when keyboard keys belong to the editor rather than the piano input.</summary>
        public bool WantsKeyboard => IsOpen && (focusId != null || Paused);

        public bool OwnsMouse(Vector2 m) => IsOpen && (m.X >= PanelX || m.Y > TopBarBottom);

        public void Init() { }

        public void Toggle()
        {
            IsOpen = !IsOpen;
            if (IsOpen) { Paused = true; Status("Editor open. Pick a tool and click on the stave."); }
            focusId = null;
            dragging = false;
        }

        // =====================================================================
        //  State
        // =====================================================================
        enum Tool { Select, Sprite, Text, Pulse, Colour, Clef }
        enum Kind { Int, Num, Bool, Str, Color, Enum, Texture, Text }
        record Field(string Name, Kind Kind, object? Default, double Step = 1, string[]? Options = null, string? Label = null);

        JsonObject? root;
        JsonArray Events => (JsonArray)root!["Events"]!;
        readonly List<string> undo = new(), redo = new();
        int selected = -1;
        Tool tool = Tool.Select;
        static readonly double[] SnapOptions = { 1, 0.5, 0.25, 0.125 };
        static readonly string[] SnapLabels = { "1", "1/2", "1/4", "1/8" };
        int snapIndex = 1;

        // Brush settings for new objects
        string brushTexture = "arrow";
        float brushScale = 1f;
        bool brushStatic;
        string brushColor = "#FFFFFF";
        string pulseTarget = "Background";
        string pulseColor = "#0038A8";
        double pulseIn = 0.1, pulseHold = 0, pulseOut = 0.7;
        double colourDuration = 1;

        // Widgets
        string? focusId;
        int frame, focusFrame;
        string focusBuffer = "";
        Action<string>? focusCommit;
        string? paletteFor;
        float propScroll, listScroll;
        bool showHelp;

        // Dragging
        bool dragging, dragUndoPushed, dragIsMarker;
        Vector2 dragStartMouse;
        int dragStartStep;
        float dragStartX, dragStartY, dragStartTime;

        // Rebuild requests
        bool rebuildRequested;
        float rebuildTime;

        // Status line
        string status = "";
        float statusTimer;

        // Cached timing of every event (rebuilt when the JSON changes)
        int version, cachedVersion = -1;
        readonly List<(int index, float time)> eventTimes = new();
        string[] assets = Array.Empty<string>();

        static readonly string[] Palette =
        {
            "#FFFFFF", "#FA3834", "#0038A8", "#001F5E", "#0024C4", "#50D2FF",
            "#B9F2FF", "#6EEB78", "#FFD040", "#FF8C00", "#FF00AA", "#000000"
        };

        static readonly string[] Targets = { "Background", "Stave", "Notes", "Note", "Decoration" };
        static readonly HashSet<string> GameTextures = new(StringComparer.OrdinalIgnoreCase)
        {
            "crotchet_head", "minim_head", "semibreve_head", "rest_whole", "rest_half", "rest_quarter", "rest_eighth",
            "treble_clef", "bass_clef", "sharp", "flat", "natural"
        };

        static readonly JsonSerializerOptions SaveOptions = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping   // keeps apostrophes and accents readable in lyrics
        };

        // ---------------- Field schema per event type ----------------
        static readonly Field[] TimingFields =
        {
            new("Bar", Kind.Int, 1), new("Beat", Kind.Num, 1.0, 0.25),
        };

        static readonly Dictionary<string, Field[]> Schema = new()
        {
            ["SpawnDecoration"] = new Field[]
            {
                new("TextureName", Kind.Texture, "arrow", Label: "Texture"), new("ID", Kind.Str, "", Label: "ID"),
                new("StaffStep", Kind.Int, 0), new("XOffset", Kind.Num, 0.0, 5), new("YOffset", Kind.Num, 0.0, 5),
                new("Scale", Kind.Num, 1.0, 0.1), new("Rotation", Kind.Num, 0.0, 15), new("ColorHex", Kind.Color, "#FFFFFF", Label: "Colour"),
                new("SpinSpeed", Kind.Num, 0.0, 15), new("BobSpeed", Kind.Num, 0.0, 0.5), new("BobAmplitude", Kind.Num, 0.0, 2),
                new("IsStatic", Kind.Bool, false), new("FollowStave", Kind.Bool, false),
                new("LeaveBar", Kind.Int, null), new("LeaveBeat", Kind.Num, null, 0.25),
                new("FadeInBeats", Kind.Num, null, 0.25), new("FadeOutBeats", Kind.Num, null, 0.25),
            },
            ["SpawnText"] = new Field[]
            {
                new("Text", Kind.Text, ""), new("ID", Kind.Str, "", Label: "ID"), new("FontSize", Kind.Int, 40, 2),
                new("ColorHex", Kind.Color, "#FFFFFF", Label: "Colour"), new("OutlineThickness", Kind.Int, 0),
                new("OutlineColorHex", Kind.Color, "#000000", Label: "Outline"), new("IsBold", Kind.Bool, false),
                new("IsUnderlined", Kind.Bool, false), new("IsStatic", Kind.Bool, true),
                new("XOffset", Kind.Num, 0.0, 5), new("YOffset", Kind.Num, 0.0, 5),
                new("LeaveBar", Kind.Int, null), new("LeaveBeat", Kind.Num, null, 0.25),
                new("FadeInBeats", Kind.Num, null, 0.25), new("FadeOutBeats", Kind.Num, null, 0.25),
            },
            ["PulseColor"] = new Field[]
            {
                new("Target", Kind.Enum, "Background", Options: Targets), new("ColorHex", Kind.Color, "#FFFFFF", Label: "Colour"),
                new("FadeInBeats", Kind.Num, 0.1, 0.1), new("HoldBeats", Kind.Num, 0.0, 0.25), new("FadeOutBeats", Kind.Num, 0.7, 0.1),
                new("TargetBar", Kind.Int, null), new("TargetBeat", Kind.Num, null, 0.25), new("TargetStaffStep", Kind.Int, null),
                new("TargetID", Kind.Str, null, Label: "Target ID"),
            },
            ["AnimateColor"] = new Field[]
            {
                new("Target", Kind.Enum, "Background", Options: Targets), new("ColorHex", Kind.Color, "#FFFFFF", Label: "Colour"),
                new("DurationBeats", Kind.Num, 1.0, 0.25), new("TargetID", Kind.Str, null, Label: "Target ID"),
            },
            ["AnimateDecoration"] = new Field[]
            {
                new("ID", Kind.Str, "", Label: "Target ID"), new("DurationBeats", Kind.Num, 2.0, 0.25),
                new("XOffset", Kind.Num, null, 5), new("YOffset", Kind.Num, null, 5), new("Scale", Kind.Num, null, 0.1),
                new("Rotation", Kind.Num, null, 15), new("ColorHex", Kind.Color, null, Label: "Colour"),
            },
            ["ToggleBassClef"] = new Field[] { new("ToggleState", Kind.Bool, true, Label: "Bass clef on") },
        };

        static readonly Field[] LevelFields =
        {
            new("SongName", Kind.Str, ""), new("Author", Kind.Str, ""), new("Genre", Kind.Str, ""),
            new("InitialBackground", Kind.Texture, "bg_default", Label: "Background"),
            new("InitialBackgroundColorHex", Kind.Color, "#001133", Label: "BG colour"),
            new("InitialStaveColorHex", Kind.Color, "#FFFFFF", Label: "Stave colour"),
            new("InitialNoteColorHex", Kind.Color, "#FFFFFF", Label: "Note colour"),
            new("AudioSyncOffset", Kind.Num, 0.0, 0.01, Label: "Audio offset"),
        };

        // =====================================================================
        //  Layout
        // =====================================================================
        static int ScreenW => Raylib.GetScreenWidth();
        static int ScreenH => Raylib.GetScreenHeight();
        int PanelX => ScreenW - PanelWidth;
        const int TopBarBottom = 130;
        const int StripTop = 136, StripHeight = 46;
        int ListTop => ScreenH - 330;

        // =====================================================================
        //  Loading / saving
        // =====================================================================
        public void OpenLevel(string path)
        {
            try
            {
                JsonObject data = File.Exists(path)
                    ? JsonNode.Parse(File.ReadAllText(path), null, LevelJson.Document) as JsonObject
                        ?? throw new InvalidDataException("Level must be a JSON object.")
                    : new JsonObject
                    {
                        ["SongName"] = Path.GetFileNameWithoutExtension(path),
                        ["InitialBackground"] = "bg_default",
                        ["InitialBackgroundColorHex"] = "#001133",
                        ["InitialStaveColorHex"] = "#FFFFFF",
                        ["InitialNoteColorHex"] = "#FFFFFF",
                        ["Events"] = new JsonArray()
                    };
                if (data["Events"] is not JsonArray) data["Events"] = new JsonArray();
                root = data;
                JsonPath = path;
                selected = -1;
                undo.Clear(); redo.Clear();
                Dirty = false;
                focusId = null;
                version++;
                ScanAssets();
                Status(File.Exists(path) ? "Loaded " + Path.GetFileName(path) : "New level: " + Path.GetFileName(path));
            }
            catch (Exception ex)
            {
                root = null;
                Status("Load failed: " + ex.Message);
            }
        }

        void ScanAssets()
        {
            var found = new List<string>();
            foreach (var dir in new[] { "Assets", Path.Combine("Assets", "Decorations") })
            {
                if (!Directory.Exists(dir)) continue;
                found.AddRange(Directory.GetFiles(dir, "*.png")
                    .Select(p => Path.GetFileNameWithoutExtension(p)!)
                    .Where(n => !GameTextures.Contains(n) && !n.StartsWith("bg_", StringComparison.OrdinalIgnoreCase) &&
                                !n.Contains("background", StringComparison.OrdinalIgnoreCase)));
            }
            assets = found.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n).ToArray();
            if (assets.Length > 0 && !assets.Contains(brushTexture)) brushTexture = assets.Contains("arrow") ? "arrow" : assets[0];
        }

        public string GetPreviewJson() => root!.ToJsonString(SaveOptions);

        public bool TakeRebuildRequest(out string json, out float time)
        {
            json = ""; time = 0;
            if (!rebuildRequested || root == null) return false;
            rebuildRequested = false;
            json = GetPreviewJson();
            time = rebuildTime;
            return true;
        }

        void RequestRebuild(float at)
        {
            rebuildRequested = true;
            rebuildTime = at;
        }

        void Save()
        {
            if (root == null) return;
            try
            {
                // Sort events by musical time so the file reads top-to-bottom like the song.
                var chart = Program.CurrentChart;
                JsonNode? keep = SelectedNode;
                var sorted = Events.Select((n, i) => (n, i, t: EventTime(n as JsonObject, chart))).OrderBy(x => x.t).ThenBy(x => x.i).Select(x => x.n).ToList();
                Events.Clear();
                foreach (var n in sorted) Events.Add(n);
                selected = keep == null ? -1 : sorted.IndexOf(keep);
                version++;

                string json = Reindent(root.ToJsonString(SaveOptions));
                string full = Path.GetFullPath(JsonPath);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                string temp = full + ".tmp";
                File.WriteAllText(temp, json);
                if (File.Exists(full)) File.Copy(full, full + ".bak", true);
                File.Move(temp, full, true);
                Dirty = false;
                Status($"Saved {Path.GetFileName(full)} ({Events.Count} events, backup in .bak)");
                RequestRebuild(Program.SongTime);
            }
            catch (Exception ex) { Status("Save failed: " + ex.Message); }
        }

        /// <summary>System.Text.Json indents with 2 spaces; the hand-written levels use 4.</summary>
        static string Reindent(string json)
        {
            var sb = new StringBuilder();
            foreach (var line in json.Split('\n'))
            {
                int n = 0;
                while (n < line.Length && line[n] == ' ') n++;
                sb.Append(' ', n * 2).Append(line.AsSpan(n)).Append('\n');
            }
            return sb.ToString();
        }

        // =====================================================================
        //  Undo / change tracking
        // =====================================================================
        void PushUndo()
        {
            if (root == null) return;
            undo.Add(root.ToJsonString());
            if (undo.Count > 200) undo.RemoveAt(0);
            redo.Clear();
        }

        void Changed(bool rebuild = true)
        {
            Dirty = true;
            version++;
            if (rebuild) RequestRebuild(Program.SongTime);
        }

        void Undo()
        {
            if (undo.Count == 0) { Status("Nothing to undo"); return; }
            redo.Add(root!.ToJsonString());
            root = (JsonObject)JsonNode.Parse(undo[^1])!;
            undo.RemoveAt(undo.Count - 1);
            selected = Math.Min(selected, Events.Count - 1);
            focusId = null;
            Changed();
            Status("Undo");
        }

        void Redo()
        {
            if (redo.Count == 0) { Status("Nothing to redo"); return; }
            undo.Add(root!.ToJsonString());
            root = (JsonObject)JsonNode.Parse(redo[^1])!;
            redo.RemoveAt(redo.Count - 1);
            selected = Math.Min(selected, Events.Count - 1);
            focusId = null;
            Changed();
            Status("Redo");
        }

        void Status(string text) { status = text; statusTimer = 6f; }

        // =====================================================================
        //  Time helpers
        // =====================================================================
        double Snap => SnapOptions[snapIndex];

        static float EventTime(JsonObject? o, Chart chart)
        {
            if (o == null) return 0f;
            if (o["Bar"] != null) return chart.BarBeatToSeconds(GetInt(o, "Bar", 1), GetNum(o, "Beat", 1));
            return (float)GetNum(o, "TimeSeconds", 0);
        }

        (int bar, double beat) SnapTime(float t)
        {
            var chart = Program.CurrentChart;
            if (t <= 0) return (1, 1.0);
            var (bar, beat) = chart.SecondsToBarBeat(t + 0.0005f);
            double b0 = Math.Round((beat - 1) / Snap) * Snap;
            if (b0 >= chart.TimeSigNum - 1e-6) { bar++; b0 -= chart.TimeSigNum; }
            return (bar, Math.Round(b0 + 1, 4));
        }

        float BarBeatTime(int bar, double beat) => Program.CurrentChart.BarBeatToSeconds(bar, beat);

        void Seek(float t)
        {
            t = Math.Clamp(t, -Program.LeadInTime, Math.Max(0f, Program.LevelEndTime));
            RequestRebuild(t);
        }

        float StepSeconds(float at, double beats) => Program.CurrentChart.BeatsAfter(Math.Max(0f, at), beats);

        void RefreshEventTimes()
        {
            if (cachedVersion == version && eventTimes.Count == Events.Count) return;
            cachedVersion = version;
            eventTimes.Clear();
            var chart = Program.CurrentChart;
            for (int i = 0; i < Events.Count; i++) eventTimes.Add((i, EventTime(Events[i] as JsonObject, chart)));
            eventTimes.Sort((a, b) => a.time != b.time ? a.time.CompareTo(b.time) : a.index.CompareTo(b.index));
        }

        // =====================================================================
        //  JSON helpers
        // =====================================================================
        JsonObject? SelectedObject
        {
            get
            {
                if (root == null || selected < 0) return null;
                if (selected >= Events.Count) { selected = -1; return null; }
                return Events[selected] as JsonObject;
            }
        }
        JsonNode? SelectedNode => SelectedObject;

        static double GetNum(JsonObject o, string key, double fallback)
        {
            var n = o[key];
            if (n is not JsonValue v) return fallback;
            if (v.TryGetValue(out double d)) return d;
            if (v.TryGetValue(out int i)) return i;
            if (v.TryGetValue(out string? s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            return fallback;
        }

        static int GetInt(JsonObject o, string key, int fallback) => (int)Math.Round(GetNum(o, key, fallback));
        static bool GetBool(JsonObject o, string key, bool fallback) => o[key] is JsonValue v && v.TryGetValue(out bool b) ? b : fallback;
        static string? GetStr(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue(out string? s) ? s : o[key]?.ToJsonString();

        static void SetNum(JsonObject o, string key, double value, bool integer)
        {
            if (integer) o[key] = (int)Math.Round(value);
            else o[key] = Math.Round(value, 4);
        }

        static string NewId(string prefix) => prefix + "_" + Guid.NewGuid().ToString("N")[..6];
        static string Type(JsonObject o) => GetStr(o, "EventType") ?? "?";

        // =====================================================================
        //  Creating objects
        // =====================================================================
        int Add(JsonObject ev)
        {
            Events.Add(ev);
            selected = Events.Count - 1;
            Changed();
            return selected;
        }

        void PlaceAtMouse(Vector2 mouse, bool shift)
        {
            float songTime = Program.SongTime;
            var (bar, beat) = SnapTime(Program.XToTime(mouse.X));
            float anchor = Program.StaffAnchorY(false);

            switch (tool)
            {
                case Tool.Sprite:
                    PushUndo();
                    if (brushStatic)
                    {
                        var (sb, sbeat) = SnapTime(songTime);
                        Add(new JsonObject
                        {
                            ["EventType"] = "SpawnDecoration", ["ID"] = NewId("dec"), ["TextureName"] = brushTexture,
                            ["Bar"] = sb, ["Beat"] = sbeat, ["IsStatic"] = true,
                            ["XOffset"] = MathF.Round(mouse.X - ScreenW / 2f), ["YOffset"] = MathF.Round(mouse.Y - anchor),
                            ["LeaveBar"] = sb + 2, ["LeaveBeat"] = sbeat, ["FadeInBeats"] = 0.5, ["FadeOutBeats"] = 0.5,
                            ["Scale"] = Math.Round(brushScale, 2), ["ColorHex"] = brushColor,
                        });
                    }
                    else
                    {
                        Add(new JsonObject
                        {
                            ["EventType"] = "SpawnDecoration", ["ID"] = NewId("dec"), ["TextureName"] = brushTexture,
                            ["Bar"] = bar, ["Beat"] = beat, ["StaffStep"] = MouseStaffStep(mouse.Y),
                            ["Scale"] = Math.Round(brushScale, 2), ["ColorHex"] = brushColor,
                        });
                    }
                    Status($"Placed sprite at bar {bar}, beat {beat:0.##}");
                    break;

                case Tool.Text:
                    {
                        PushUndo();
                        var (tb, tbeat) = SnapTime(songTime);
                        float x = MathF.Round(mouse.X - ScreenW / 2f);
                        if (Math.Abs(x) < 60) x = 0;   // snaps to the centre for lyrics
                        int i = Add(new JsonObject
                        {
                            ["EventType"] = "SpawnText", ["ID"] = NewId("txt"), ["IsStatic"] = true,
                            ["Bar"] = tb, ["Beat"] = tbeat, ["FadeInBeats"] = 0.5, ["LeaveBar"] = tb + 2, ["LeaveBeat"] = tbeat,
                            ["FadeOutBeats"] = 1, ["XOffset"] = x, ["YOffset"] = MathF.Round((mouse.Y - anchor) / 10f) * 10f,
                            ["Text"] = "New lyric", ["FontSize"] = 40, ["ColorHex"] = brushColor == "#FFFFFF" ? "#0024C4" : brushColor,
                            ["OutlineThickness"] = 2, ["OutlineColorHex"] = "#000000", ["IsBold"] = true, ["IsUnderlined"] = false,
                        });
                        // Jump straight into typing the lyric
                        BeginEdit($"{i}.Text", "New lyric", v => { var o = Events[i] as JsonObject; if (o != null) { PushUndo(); o["Text"] = v.Replace("\\n", "\n"); Changed(); } });
                        focusBuffer = "";
                        Status("Type the lyric, then press Enter. Use \\n for a new line.");
                        break;
                    }

                case Tool.Pulse:
                    PushUndo();
                    if (shift)
                    {
                        int beats = Program.CurrentChart.TimeSigNum, added = 0;
                        for (int b = 1; b <= beats; b++)
                        {
                            if (Events.OfType<JsonObject>().Any(o => Type(o) == "PulseColor" && GetInt(o, "Bar", -1) == bar &&
                                                                    Math.Abs(GetNum(o, "Beat", -1) - b) < 1e-4 && (GetStr(o, "Target") ?? "") == pulseTarget))
                                continue;
                            Events.Add(MakePulse(bar, b));
                            added++;
                        }
                        selected = Events.Count - 1;
                        Changed();
                        Status($"Filled bar {bar} with {added} pulses");
                    }
                    else
                    {
                        Add(MakePulse(bar, beat));
                        Status($"Pulse at bar {bar}, beat {beat:0.##}  (Shift+click fills the whole bar)");
                    }
                    break;

                case Tool.Colour:
                    PushUndo();
                    Add(new JsonObject
                    {
                        ["Bar"] = bar, ["Beat"] = beat, ["EventType"] = "AnimateColor", ["Target"] = pulseTarget,
                        ["DurationBeats"] = colourDuration, ["ColorHex"] = pulseColor,
                    });
                    Status($"Colour change at bar {bar}, beat {beat:0.##}");
                    break;

                case Tool.Clef:
                    {
                        PushUndo();
                        float t = BarBeatTime(bar, beat);
                        var chart = Program.CurrentChart;
                        bool current = Events.OfType<JsonObject>().Where(o => Type(o) == "ToggleBassClef" && EventTime(o, chart) < t)
                                             .OrderBy(o => EventTime(o, chart)).Select(o => GetBool(o, "ToggleState", false)).LastOrDefault();
                        Add(new JsonObject { ["Bar"] = bar, ["Beat"] = beat, ["EventType"] = "ToggleBassClef", ["ToggleState"] = !current });
                        Status($"Bass clef {(!current ? "on" : "off")} at bar {bar}, beat {beat:0.##}");
                        break;
                    }
            }
        }

        JsonObject MakePulse(int bar, double beat) => new()
        {
            ["Bar"] = bar, ["Beat"] = beat, ["EventType"] = "PulseColor", ["Target"] = pulseTarget,
            ["FadeInBeats"] = pulseIn, ["HoldBeats"] = pulseHold, ["FadeOutBeats"] = pulseOut, ["ColorHex"] = pulseColor,
        };

        static int MouseStaffStep(float y) => (int)MathF.Round((y - Program.StaffAnchorY(false) + 40f) / 16f);

        void DeleteSelected()
        {
            if (SelectedObject == null) return;

            // Cancel edits belonging to the event being deleted.
            focusId = null;
            focusCommit = null;
            paletteFor = null;
            dragging = false;

            PushUndo();
            Events.RemoveAt(selected);
            selected = -1;

            Changed();
            Status("Deleted");
        }


        void DuplicateSelected(bool atPlayhead)
        {
            if (SelectedObject == null) return;
            PushUndo();
            Add(CloneShifted(SelectedObject, atPlayhead));
            Status("Duplicated (one grid step later)");
        }

        void Paste()
        {
            if (clipboard == null) { Status("Nothing copied yet"); return; }
            PushUndo();
            Add(CloneShifted(clipboard, true));
            Status("Pasted at the playhead");
        }

        /// <summary>Copies an event and moves it to the playhead or one grid step later, keeping its length.</summary>
        JsonObject CloneShifted(JsonObject src, bool atPlayhead)
        {
            var chart = Program.CurrentChart;
            var copy = (JsonObject)src.DeepClone();
            if (Type(copy).StartsWith("Spawn")) copy["ID"] = NewId(Type(copy) == "SpawnText" ? "txt" : "dec");

            float oldTime = EventTime(copy, chart);
            var (bar, beat) = atPlayhead ? SnapTime(Program.SongTime) : SnapTime(oldTime + StepSeconds(oldTime, Snap));
            if (copy["LeaveBar"] != null)
            {
                float len = BarBeatTime(GetInt(copy, "LeaveBar", 1), GetNum(copy, "LeaveBeat", 1)) - oldTime;
                var (lb, lbeat) = SnapTime(BarBeatTime(bar, beat) + len);
                copy["LeaveBar"] = lb; copy["LeaveBeat"] = lbeat;
            }
            copy.Remove("TimeSeconds");
            copy["Bar"] = bar; copy["Beat"] = beat;
            return copy;
        }

        JsonObject? clipboard;

        // =====================================================================
        //  Frame
        // =====================================================================
        public void Draw()
        {
            if (!IsOpen) return;
            frame++;
            float dt = Raylib.GetFrameTime();
            statusTimer -= dt;

            if (root == null)
            {
                DrawPanelBackground();
                Ui.Text("Open a level from the map first.", PanelX + 20, 80, 24, Theme.TextDim, Ui.Body);
                return;
            }

            RefreshEventTimes();
            HandleTextInput();
            if (focusId == null) HandleShortcuts();

            Vector2 mouse = Raylib.GetMousePosition();
            bool inPanel = mouse.X >= PanelX;

            DrawStrip(mouse, inPanel);
            HandlePlayfield(mouse, inPanel);
            DrawPanel(mouse);
            if (showHelp) DrawHelp();
        }

        // =====================================================================
        //  Keyboard
        // =====================================================================
        static bool Ctrl => Raylib.IsKeyDown(KeyboardKey.LeftControl) || Raylib.IsKeyDown(KeyboardKey.RightControl) ||
                            Raylib.IsKeyDown(KeyboardKey.LeftSuper) || Raylib.IsKeyDown(KeyboardKey.RightSuper);
        static bool Shift => Raylib.IsKeyDown(KeyboardKey.LeftShift) || Raylib.IsKeyDown(KeyboardKey.RightShift);
        static bool Pressed(KeyboardKey k) => Raylib.IsKeyPressed(k) || Raylib.IsKeyPressedRepeat(k);

        void HandleShortcuts()
        {
            if (Ctrl)
            {
                if (Raylib.IsKeyPressed(KeyboardKey.S)) Save();
                if (Raylib.IsKeyPressed(KeyboardKey.Z)) { if (Shift) Redo(); else Undo(); }
                if (Raylib.IsKeyPressed(KeyboardKey.Y)) Redo();
                if (Raylib.IsKeyPressed(KeyboardKey.D)) DuplicateSelected(false);
                if (Raylib.IsKeyPressed(KeyboardKey.C) && SelectedObject != null) { clipboard = (JsonObject)SelectedObject.DeepClone(); Status("Copied"); }
                if (Raylib.IsKeyPressed(KeyboardKey.V)) Paste();
                return;
            }

            if (Raylib.IsKeyPressed(KeyboardKey.Space)) { Paused = !Paused; Status(Paused ? "Paused" : "Playing"); }
            if (!Paused) return;   // while playing, the keyboard is the piano

            if (Raylib.IsKeyPressed(KeyboardKey.Delete) || Raylib.IsKeyPressed(KeyboardKey.Backspace)) DeleteSelected();
            if (Raylib.IsKeyPressed(KeyboardKey.Escape)) { selected = -1; paletteFor = null; showHelp = false; }
            if (Raylib.IsKeyPressed(KeyboardKey.G)) { snapIndex = (snapIndex + 1) % SnapOptions.Length; Status("Snap: " + SnapLabels[snapIndex] + " beat"); }
            if (Raylib.IsKeyPressed(KeyboardKey.Home)) Seek(-Program.LeadInTime);
            if (Raylib.IsKeyPressed(KeyboardKey.H)) showHelp = !showHelp;

            KeyboardKey[] toolKeys = { KeyboardKey.One, KeyboardKey.Two, KeyboardKey.Three, KeyboardKey.Four, KeyboardKey.Five, KeyboardKey.Six };
            for (int i = 0; i < toolKeys.Length; i++) if (Raylib.IsKeyPressed(toolKeys[i])) tool = (Tool)i;

            float now = Program.SongTime;
            double step = Shift ? Program.CurrentChart.TimeSigNum : Snap;
            if (Pressed(KeyboardKey.Right)) Seek(SnapToGrid(now + StepSeconds(now, step) + 0.001f));
            if (Pressed(KeyboardKey.Left)) Seek(SnapToGrid(now - StepSeconds(Math.Max(0, now - 0.5f), step) - 0.001f));

            if (SelectedObject is JsonObject sel && (Pressed(KeyboardKey.Up) || Pressed(KeyboardKey.Down)))
            {
                int dir = Pressed(KeyboardKey.Up) ? -1 : 1;
                PushUndo();
                if (sel["StaffStep"] != null) sel["StaffStep"] = GetInt(sel, "StaffStep", 0) + dir;
                else SetNum(sel, "YOffset", GetNum(sel, "YOffset", 0) + dir * (Shift ? 50 : 10), false);
                Changed();
            }
        }

        float SnapToGrid(float t)
        {
            if (t <= 0) return t;
            var (bar, beat) = SnapTime(t);
            return BarBeatTime(bar, beat);
        }

        // =====================================================================
        //  Timeline strip (beats, bars and a marker for every event)
        // =====================================================================
        void DrawStrip(Vector2 mouse, bool inPanel)
        {
            RefreshEventTimes();
            var chart = Program.CurrentChart;
            int right = PanelX;
            var strip = new Rectangle(0, StripTop, right, StripHeight);
            Raylib.DrawRectangleRec(strip, new Color(10, 12, 22, 210));
            Raylib.DrawLineEx(new Vector2(0, StripTop + StripHeight), new Vector2(right, StripTop + StripHeight), 2, Theme.Accent);

            // Beat ticks
            int beatIndex = 0;
            foreach (float bt in chart.BeatTimes)
            {
                float x = Program.TimeToX(bt);
                bool barStart = beatIndex % chart.TimeSigNum == 0;
                if (x > -10 && x < right)
                {
                    Raylib.DrawLineEx(new Vector2(x, StripTop + (barStart ? 2 : 30)), new Vector2(x, StripTop + StripHeight), barStart ? 2 : 1,
                                      barStart ? new Color(255, 255, 255, 160) : new Color(255, 255, 255, 60));
                    if (barStart) Ui.Text((beatIndex / chart.TimeSigNum + 1).ToString(), x + 4, StripTop + 2, 18, Theme.TextDim, Ui.Body);
                }
                beatIndex++;
            }

            // Playhead
            float px = Program.HitZoneX;
            Raylib.DrawLineEx(new Vector2(px, StripTop), new Vector2(px, StripTop + StripHeight), 3, Theme.Accent);

            // Markers
            int hover = -1;
            foreach (var (index, time) in eventTimes)
            {
                float x = Program.TimeToX(time);
                if (x < -20 || x > right) continue;
                var o = Events[index] as JsonObject;
                if (o == null) continue;
                var c = new Vector2(x, StripTop + 30);
                bool isSel = index == selected;
                if (!inPanel && Vector2.Distance(mouse, c) < 10) hover = index;
                DrawMarker(o, c, isSel ? 10 : 7, isSel || hover == index);
            }

            if (inPanel || mouse.Y < StripTop || mouse.Y > StripTop + StripHeight) return;

            if (hover >= 0)
            {
                var o = (JsonObject)Events[hover]!;
                Tooltip(mouse, Describe(o));
            }

            if (Raylib.IsMouseButtonPressed(MouseButton.Left))
            {
                if (hover >= 0)
                {
                    selected = hover;
                    focusId = null;
                    BeginDrag(mouse, true);
                }
                else
                {
                    var (bar, beat) = SnapTime(Program.XToTime(mouse.X));
                    Seek(mouse.X < Program.HitZoneX && Program.XToTime(mouse.X) < 0 ? Program.XToTime(mouse.X) : BarBeatTime(bar, beat));
                }
            }
        }

        void DrawMarker(JsonObject o, Vector2 c, float r, bool highlight)
        {
            string type = Type(o);
            Color col = Ui.ParseHex(GetStr(o, "ColorHex"), Color.White);
            if (highlight) Raylib.DrawCircleV(c, r + 5, Theme.WithAlpha(Theme.Accent, 0.8f));
            switch (type)
            {
                case "PulseColor":
                    Raylib.DrawCircleV(c, r, col);
                    Raylib.DrawCircleLinesV(c, r, Color.White);
                    break;
                case "AnimateColor":
                    Raylib.DrawRectangleRec(new Rectangle(c.X - r, c.Y - r, r * 2, r * 2), col);
                    Raylib.DrawRectangleLinesEx(new Rectangle(c.X - r, c.Y - r, r * 2, r * 2), 1, Color.White);
                    break;
                case "SpawnText":
                    Raylib.DrawRectangleRounded(new Rectangle(c.X - r, c.Y - r, r * 2, r * 2), 0.3f, 4, new Color(255, 208, 64, 255));
                    Ui.TextCentered("T", c.X, c.Y - r + 1, r * 1.8f, Theme.Background, Ui.Body);
                    break;
                case "ToggleBassClef":
                    Raylib.DrawRectangleRounded(new Rectangle(c.X - r, c.Y - r, r * 2, r * 2), 0.3f, 4, new Color(255, 100, 200, 255));
                    Ui.TextCentered("B", c.X, c.Y - r + 1, r * 1.8f, Theme.Background, Ui.Body);
                    break;
                case "AnimateDecoration":
                    Raylib.DrawPoly(c, 3, r + 2, 90, new Color(255, 140, 0, 255));
                    break;
                default:
                    Raylib.DrawPoly(c, 4, r + 2, 0, new Color(80, 210, 255, 255));
                    break;
            }
        }

        // =====================================================================
        //  Clicking and dragging on the stave
        // =====================================================================
        void HandlePlayfield(Vector2 mouse, bool inPanel)
        {
            bool inField = !inPanel && mouse.Y > StripTop + StripHeight;
            var level = Program.CurrentLevel;

            // Highlight the selected decoration
            foreach (var d in level.Decorations)
            {
                if (d.SourceIndex != selected || !d.WasDrawn) continue;
                var b = d.LastBounds;
                var r = new Rectangle(b.X - 6, b.Y - 6, b.Width + 12, b.Height + 12);
                Raylib.DrawRectangleLinesEx(r, 3, Theme.Accent);
                foreach (var corner in new[] { new Vector2(r.X, r.Y), new Vector2(r.X + r.Width, r.Y), new Vector2(r.X, r.Y + r.Height), new Vector2(r.X + r.Width, r.Y + r.Height) })
                    Raylib.DrawRectangleV(corner - new Vector2(5, 5), new Vector2(10, 10), Color.White);
            }

            // Dragging in progress
            if (dragging)
            {
                if (Raylib.IsMouseButtonDown(MouseButton.Left)) UpdateDrag(mouse);
                else { dragging = false; RequestRebuild(Program.SongTime); }
                return;
            }

            if (!inField || !Paused || showHelp) return;

            // Scrub with the mouse wheel
            float wheel = Raylib.GetMouseWheelMove();
            if (wheel != 0)
            {
                float now = Program.SongTime;
                double beats = (Shift ? Program.CurrentChart.TimeSigNum : Snap) * Math.Sign(wheel);
                float delta = StepSeconds(beats > 0 ? now : Math.Max(0, now - 0.5f), Math.Abs(beats)) * Math.Sign(wheel);
                Seek(SnapToGrid(now - delta));
            }

            // Guide line and ghost preview for the placement tools
            if (tool != Tool.Select)
            {
                bool usesPlayhead = tool == Tool.Text || (tool == Tool.Sprite && brushStatic);
                var (bar, beat) = SnapTime(usesPlayhead ? Program.SongTime : Program.XToTime(mouse.X));
                float gx = usesPlayhead ? Program.HitZoneX : Program.TimeToX(BarBeatTime(bar, beat));
                Raylib.DrawLineEx(new Vector2(gx, StripTop + StripHeight), new Vector2(gx, ScreenH - 130), 2, Theme.WithAlpha(Theme.Accent, 0.7f));

                if (tool == Tool.Sprite)
                {
                    var tex = Program.GetTexture(brushTexture);
                    float gy = brushStatic ? mouse.Y : Program.StaffAnchorY(false) + LevelLoader.StaffStepToYOffset(MouseStaffStep(mouse.Y));
                    float gxs = brushStatic ? mouse.X : gx;
                    Ui.TextureCentered(tex, gxs, gy, brushScale, Theme.WithAlpha(Ui.ParseHex(brushColor, Color.White), 0.55f));
                    Tooltip(mouse, brushStatic ? $"Static sprite from bar {bar} beat {beat:0.##}" : $"Bar {bar} | beat {beat:0.##} | step {MouseStaffStep(mouse.Y)}");
                }
                else Tooltip(mouse, tool switch
                {
                    Tool.Pulse => $"Pulse {pulseTarget} at bar {bar} | beat {beat:0.##}  (Shift: whole bar)",
                    Tool.Colour => $"Colour {pulseTarget} at bar {bar} | beat {beat:0.##}",
                    Tool.Clef => $"Toggle bass clef at bar {bar} | beat {beat:0.##}",
                    Tool.Text => $"Lyric from bar {bar} | beat {beat:0.##}",
                    _ => ""
                });

                if (Raylib.IsMouseButtonPressed(MouseButton.Left)) PlaceAtMouse(mouse, Shift);
                if (Raylib.IsMouseButtonPressed(MouseButton.Right)) tool = Tool.Select;
                return;
            }

            // Select tool: click a decoration to select it, drag to move it
            if (Raylib.IsMouseButtonPressed(MouseButton.Left))
            {
                var hit = level.Decorations.LastOrDefault(d => d.WasDrawn && Raylib.CheckCollisionPointRec(mouse, Grow(d.LastBounds, 6)));
                focusId = null;
                if (hit != null)
                {
                    selected = hit.SourceIndex;
                    BeginDrag(mouse, false);
                }
                else selected = -1;
            }
        }

        static Rectangle Grow(Rectangle r, float by) => new(r.X - by, r.Y - by, r.Width + by * 2, r.Height + by * 2);

        void BeginDrag(Vector2 mouse, bool marker)
        {
            var o = SelectedObject;
            if (o == null) return;
            dragging = true;
            dragIsMarker = marker;
            dragUndoPushed = false;
            dragStartMouse = mouse;
            dragStartTime = EventTime(o, Program.CurrentChart);
            dragStartStep = GetInt(o, "StaffStep", 0);
            dragStartX = (float)GetNum(o, "XOffset", 0);
            dragStartY = (float)GetNum(o, "YOffset", 0);
        }

        void UpdateDrag(Vector2 mouse)
        {
            var o = SelectedObject;
            if (o == null) { dragging = false; return; }
            Vector2 delta = mouse - dragStartMouse;
            if (!dragUndoPushed && delta.Length() < 4) return;
            if (!dragUndoPushed) { PushUndo(); dragUndoPushed = true; }

            bool isStatic = GetBool(o, "IsStatic", false);
            bool moved = false;

            if (isStatic && !dragIsMarker)
            {
                SetNum(o, "XOffset", MathF.Round(dragStartX + delta.X), false);
                SetNum(o, "YOffset", MathF.Round(dragStartY + delta.Y), false);
                moved = true;
            }
            else
            {
                if (o["Bar"] != null || o["TimeSeconds"] == null)
                {
                    float startX = Program.TimeToX(dragStartTime);
                    var (bar, beat) = SnapTime(Program.XToTime(startX + delta.X));
                    if (GetInt(o, "Bar", -1) != bar || Math.Abs(GetNum(o, "Beat", -1) - beat) > 1e-6)
                    {
                        // Lifetimes move with the object
                        if (o["LeaveBar"] != null)
                        {
                            float len = BarBeatTime(GetInt(o, "LeaveBar", 1), GetNum(o, "LeaveBeat", 1)) - EventTime(o, Program.CurrentChart);
                            var (lb, lbeat) = SnapTime(BarBeatTime(bar, beat) + len);
                            o["LeaveBar"] = lb; o["LeaveBeat"] = lbeat;
                        }
                        o["Bar"] = bar; o["Beat"] = beat;
                        o.Remove("TimeSeconds");
                        moved = true;
                    }
                }
                if (!dragIsMarker)
                {
                    if (o["StaffStep"] != null || Type(o) == "SpawnDecoration")
                    {
                        int step = dragStartStep + (int)MathF.Round(delta.Y / 16f);
                        if (GetInt(o, "StaffStep", int.MinValue) != step) { o["StaffStep"] = step; moved = true; }
                    }
                    else
                    {
                        double y = Math.Round((dragStartY + delta.Y) / 5) * 5;
                        if (Math.Abs(GetNum(o, "YOffset", 0) - y) > 0.01) { SetNum(o, "YOffset", y, false); moved = true; }
                    }
                }
            }

            if (moved) Changed();
        }

        // =====================================================================
        //  Side panel
        // =====================================================================
        void DrawPanelBackground()
        {
            int x = PanelX;
            Raylib.DrawRectangle(x, 0, PanelWidth, ScreenH, new Color(18, 20, 32, 245));
            Raylib.DrawRectangle(x, 0, 4, ScreenH, Theme.Accent);
        }

        void DrawPanel(Vector2 mouse)
        {
            DrawPanelBackground();
            int x = PanelX + 20, w = PanelWidth - 40;
            int y = 14;

            Ui.Text("LEVEL EDITOR", x, y, 32, Theme.Text);
            Ui.TextRight(Dirty ? "unsaved" : "saved", x + w, y + 8, 20, Dirty ? Theme.Okay : Theme.Good, Ui.Body);
            y += 40;
            Ui.Text(Ui.Fit(Path.GetFileName(JsonPath), w, 18, Ui.Body), x, y, 18, Theme.TextFaint, Ui.Body);
            y += 28;

            int bw = (w - 18) / 4;
            if (SmallButton(new Rectangle(x, y, bw, 34), Paused ? "Play" : "Pause", !Paused)) Paused = !Paused;
            if (SmallButton(new Rectangle(x + (bw + 6), y, bw, 34), "Save", Dirty)) Save();
            if (SmallButton(new Rectangle(x + (bw + 6) * 2, y, bw, 34), "Undo", false, undo.Count > 0)) Undo();
            if (SmallButton(new Rectangle(x + (bw + 6) * 3, y, bw, 34), "Redo", false, redo.Count > 0)) Redo();
            y += 44;

            // Time readout
            float now = Program.SongTime;
            var (bar, beat) = now >= 0 ? Program.CurrentChart.SecondsToBarBeat(now) : (0, 0.0);
            string clock = now < 0 ? $"-{-now:0.00}s" : $"{(int)now / 60}:{now % 60:00.00}";
            Ui.Text(now < 0 ? "Count-in" : $"Bar {bar}   |   Beat {beat:0.00}", x, y, 26, Theme.Text, Ui.Body);
            Ui.TextRight(clock, x + w, y + 2, 22, Theme.TextDim, Ui.Body);
            y += 38;

            // Tools
            string[] toolNames = { "Select", "Sprite", "Text", "Pulse", "Colour", "Clef" };
            int tw = (w - 5 * 5) / 6;
            for (int i = 0; i < toolNames.Length; i++)
                if (SmallButton(new Rectangle(x + i * (tw + 5), y, tw, 34), toolNames[i], tool == (Tool)i, true, 17)) tool = (Tool)i;
            y += 42;

            Ui.Text("Snap", x, y + 7, 20, Theme.TextDim, Ui.Body);
            for (int i = 0; i < SnapOptions.Length; i++)
                if (SmallButton(new Rectangle(x + 60 + i * 64, y, 58, 32), SnapLabels[i], snapIndex == i)) snapIndex = i;
            if (SmallButton(new Rectangle(x + w - 36, y, 36, 32), "?", showHelp)) showHelp = !showHelp;
            y += 42;

            Raylib.DrawRectangle(x, y, w, 2, new Color(255, 255, 255, 30));
            y += 10;

            // Middle section: properties of the selection, or brush options for the current tool
            var area = new Rectangle(PanelX, y, PanelWidth, ListTop - y - 10);
            Raylib.BeginScissorMode((int)area.X, (int)area.Y, (int)area.Width, (int)area.Height);
            float contentStart = y - propScroll;
            float end = SelectedObject != null ? DrawProperties(x, contentStart, w, mouse, area)
                      : tool == Tool.Select ? DrawLevelProperties(x, contentStart, w, mouse, area)
                      : DrawBrush(x, contentStart, w, mouse, area);
            Raylib.EndScissorMode();
            float contentHeight = end - contentStart;
            float maxScroll = Math.Max(0, contentHeight - area.Height);
            if (Raylib.CheckCollisionPointRec(mouse, area) && !overNumber)
                propScroll -= Raylib.GetMouseWheelMove() * 40;
            propScroll = Math.Clamp(propScroll, 0, maxScroll);
            if (maxScroll > 0)
            {
                float barH = area.Height * area.Height / contentHeight;
                float barY = area.Y + (area.Height - barH) * (propScroll / maxScroll);
                Raylib.DrawRectangleRounded(new Rectangle(PanelX + PanelWidth - 8, barY, 4, barH), 1f, 4, new Color(255, 255, 255, 70));
            }
            overNumber = false;

            DrawEventList(x, w, mouse);

            // Status line
            Raylib.DrawRectangle(PanelX + 4, ScreenH - 44, PanelWidth - 4, 44, new Color(10, 12, 22, 255));
            string line = statusTimer > 0 ? status : "1-6 tools | G snap | Space play | H help";
            Ui.Text(Ui.Fit(line, w, 19, Ui.Body), x, ScreenH - 32, 19, statusTimer > 0 ? Theme.Text : Theme.TextFaint, Ui.Body);
        }

        bool overNumber;

        float DrawBrush(int x, float y, int w, Vector2 mouse, Rectangle area)
        {
            Ui.Text(tool switch
            {
                Tool.Sprite => "SPRITE BRUSH", Tool.Text => "LYRIC / TEXT", Tool.Pulse => "PULSE BRUSH",
                Tool.Colour => "COLOUR CHANGE", Tool.Clef => "BASS CLEF", _ => ""
            }, x, y, 24, Theme.Accent);
            y += 34;

            switch (tool)
            {
                case Tool.Sprite:
                    Hint(x, ref y, w, brushStatic ? "Click to place a sprite that stays on screen (from the playhead for 2 bars)."
                                                  : "Click on the stave to place a sprite that scrolls with the music.");
                    y = Row(x, y, w, "Stays on screen", () => BoolWidget(x + 200, y, "brush.static", brushStatic, v => brushStatic = v, mouse));
                    y = Row(x, y, w, "Scale", () => NumberWidget(x + 200, y, w - 200, "brush.scale", brushScale, 0.1, v => brushScale = (float)Math.Max(0.05, v), mouse, area));
                    y = Row(x, y, w, "Colour", () => ColorWidget(x + 200, y, w - 200, "brush.color", brushColor, v => brushColor = v, mouse, area));
                    if (paletteFor == "brush.color") y = PaletteRow(x, y, w, v => brushColor = v, mouse, area);
                    y += 6;
                    Ui.Text("Texture", x, y, 20, Theme.TextDim, Ui.Body);
                    y += 26;
                    y = AssetGrid(x, y, w, brushTexture, v => brushTexture = v, mouse, area);
                    break;

                case Tool.Text:
                    Hint(x, ref y, w, "Scrub to where the lyric starts, then click where it should appear. Type it and press Enter.");
                    y = Row(x, y, w, "Colour", () => ColorWidget(x + 200, y, w - 200, "brush.color", brushColor, v => brushColor = v, mouse, area));
                    if (paletteFor == "brush.color") y = PaletteRow(x, y, w, v => brushColor = v, mouse, area);
                    break;

                case Tool.Pulse:
                case Tool.Colour:
                    Hint(x, ref y, w, tool == Tool.Pulse ? "Click a beat to flash a colour. Shift+click pulses every beat of that bar."
                                                         : "Click a beat to fade to a new colour and stay there.");
                    y = Row(x, y, w, "Target", () => EnumWidget(x + 200, y, w - 200, "brush.target", pulseTarget, new[] { "Background", "Stave", "Notes" }, v => pulseTarget = v, mouse, area));
                    y = Row(x, y, w, "Colour", () => ColorWidget(x + 200, y, w - 200, "brush.pcolor", pulseColor, v => pulseColor = v, mouse, area));
                    if (paletteFor == "brush.pcolor") y = PaletteRow(x, y, w, v => pulseColor = v, mouse, area);
                    if (tool == Tool.Pulse)
                    {
                        y = Row(x, y, w, "Fade in (beats)", () => NumberWidget(x + 200, y, w - 200, "brush.in", pulseIn, 0.1, v => pulseIn = Math.Max(0, v), mouse, area));
                        y = Row(x, y, w, "Hold (beats)", () => NumberWidget(x + 200, y, w - 200, "brush.hold", pulseHold, 0.25, v => pulseHold = Math.Max(0, v), mouse, area));
                        y = Row(x, y, w, "Fade out (beats)", () => NumberWidget(x + 200, y, w - 200, "brush.out", pulseOut, 0.1, v => pulseOut = Math.Max(0, v), mouse, area));
                    }
                    else y = Row(x, y, w, "Duration (beats)", () => NumberWidget(x + 200, y, w - 200, "brush.dur", colourDuration, 0.25, v => colourDuration = Math.Max(0, v), mouse, area));
                    break;

                case Tool.Clef:
                    Hint(x, ref y, w, "Click a beat to switch the bass clef on or off from that point.");
                    break;
            }
            return y;
        }

        float DrawLevelProperties(int x, float y, int w, Vector2 mouse, Rectangle area)
        {
            Ui.Text("LEVEL SETTINGS", x, y, 24, Theme.Accent);
            y += 34;
            Hint(x, ref y, w, "Click a marker in the strip above, an event in the list, or a sprite on the stave to edit it.");
            foreach (var f in LevelFields) y = FieldRow(root!, f, "level", x, y, w, mouse, area);
            return y;
        }

        float DrawProperties(int x, float y, int w, Vector2 mouse, Rectangle area)
        {
            var o = SelectedObject!;
            string type = Type(o);
            Ui.Text(FriendlyType(type).ToUpper(), x, y, 24, Theme.Accent);
            if (SmallButton(new Rectangle(x + w - 92, y - 2, 92, 30), "Delete", false, true, 17)) { DeleteSelected(); return y + 30; }
            if (SmallButton(new Rectangle(x + w - 192, y - 2, 94, 30), "Duplicate", false, true, 17)) { DuplicateSelected(false); return y + 30; }
            y += 38;

            string id = selected.ToString(CultureInfo.InvariantCulture);
            foreach (var f in TimingFields) y = FieldRow(o, f, id, x, y, w, mouse, area);
            if (Schema.TryGetValue(type, out var fields))
                foreach (var f in fields) y = FieldRow(o, f, id, x, y, w, mouse, area);

            // Anything else in the JSON that the schema doesn't know about
            var known = new HashSet<string>(TimingFields.Select(f => f.Name).Concat(fields?.Select(f => f.Name) ?? Array.Empty<string>())) { "EventType" };
            var extras = o.Select(p => p.Key).Where(k => !known.Contains(k)).ToList();
            if (extras.Count > 0)
            {
                y += 6;
                Ui.Text("Other fields", x, y, 20, Theme.TextFaint, Ui.Body);
                y += 28;
                foreach (var key in extras)
                {
                    string raw = o[key]?.ToJsonString() ?? "null";
                    float rowY = y;
                    Ui.Text(Ui.Fit(key, 180, 20, Ui.Body), x, y + 6, 20, Theme.TextDim, Ui.Body);
                    TextWidget(x + 200, y, w - 240, $"{id}.raw.{key}", raw, v =>
                    {
                        PushUndo();
                        try { o[key] = JsonNode.Parse(v); } catch { o[key] = v; }
                        Changed();
                    }, mouse, area);
                    if (SmallButton(new Rectangle(x + w - 32, rowY, 32, 32), "x", false, true, 18)) { PushUndo(); o.Remove(key); Changed(); break; }
                    y += 38;
                }
            }

            if (type == "SpawnDecoration")
            {
                y += 6;
                Ui.Text("Texture", x, y, 20, Theme.TextDim, Ui.Body);
                y += 26;
                y = AssetGrid(x, y, w, GetStr(o, "TextureName") ?? "arrow", v => { PushUndo(); o["TextureName"] = v; Changed(); }, mouse, area);
            }
            return y + 10;
        }

        static string FriendlyType(string type) => type switch
        {
            "SpawnDecoration" => "Sprite", "SpawnArrow" => "Sprite", "SpawnText" => "Text", "PulseColor" => "Pulse",
            "AnimateColor" => "Colour change", "AnimateDecoration" => "Animate sprite", "ToggleBassClef" => "Bass clef", _ => type
        };

        // ---------------- Generic field row ----------------
        float FieldRow(JsonObject o, Field f, string idPrefix, int x, float y, int w, Vector2 mouse, Rectangle area)
        {
            string id = idPrefix + "." + f.Name;
            bool present = o[f.Name] != null;
            Ui.Text(Ui.Fit(f.Label ?? Pretty(f.Name), 185, 20, Ui.Body), x, y + 6, 20, present ? Theme.TextDim : Theme.TextFaint, Ui.Body);
            int wx = x + 200, ww = w - 200;
            bool optional = idPrefix != "level" && f.Name is not ("Bar" or "Beat" or "EventType" or "TextureName" or "Text" or "Target");
            if (present && optional) ww -= 36;

            void Set(Action apply) { PushUndo(); apply(); Changed(); }

            switch (f.Kind)
            {
                case Kind.Int:
                case Kind.Num:
                    {
                        double fallback = f.Default is IConvertible c ? Convert.ToDouble(c, CultureInfo.InvariantCulture) : DefaultFor(o, f.Name);
                        double value = present ? GetNum(o, f.Name, fallback) : fallback;
                        NumberWidget(wx, y, ww, id, value, f.Step, v => Set(() => SetNum(o, f.Name, v, f.Kind == Kind.Int)), mouse, area, present, f.Kind == Kind.Int);
                        break;
                    }
                case Kind.Bool:
                    BoolWidget(wx, y, id, present ? GetBool(o, f.Name, false) : (bool)(f.Default ?? false), v => Set(() => o[f.Name] = v), mouse, present);
                    break;
                case Kind.Color:
                    ColorWidget(wx, y, ww, id, present ? GetStr(o, f.Name) ?? "" : (string?)f.Default ?? "#FFFFFF", v => Set(() => o[f.Name] = v), mouse, area, present);
                    break;
                case Kind.Enum:
                    EnumWidget(wx, y, ww, id, present ? GetStr(o, f.Name) ?? "" : (string?)f.Default ?? "", f.Options!, v => Set(() => o[f.Name] = v), mouse, area);
                    break;
                case Kind.Text:
                    TextWidget(wx, y, ww, id, (present ? GetStr(o, f.Name) ?? "" : "").Replace("\n", "\\n"), v => Set(() => o[f.Name] = v.Replace("\\n", "\n")), mouse, area);
                    break;
                default:
                    TextWidget(wx, y, ww, id, present ? GetStr(o, f.Name) ?? "" : "", v => Set(() => { if (v.Length == 0 && optional) o.Remove(f.Name); else o[f.Name] = v; }), mouse, area);
                    break;
            }

            if (present && optional && SmallButton(new Rectangle(x + w - 32, y, 32, 32), "x", false, true, 18))
                Set(() => o.Remove(f.Name));

            y += 38;
            if (f.Kind == Kind.Color && paletteFor == id)
                y = PaletteRow(x, y, w, v => Set(() => o[f.Name] = v), mouse, area);
            return y;
        }

        double DefaultFor(JsonObject o, string name) => name switch
        {
            "LeaveBar" => GetInt(o, "Bar", 1) + 2,
            "LeaveBeat" => GetNum(o, "Beat", 1),
            "TargetBar" => GetInt(o, "Bar", 1),
            "TargetBeat" => GetNum(o, "Beat", 1),
            "FadeInBeats" or "FadeOutBeats" => 0.5,
            "Scale" => 1,
            _ => 0
        };

        static string Pretty(string name)
        {
            var sb = new StringBuilder();
            foreach (char c in name.Replace("Hex", ""))
            {
                if (char.IsUpper(c) && sb.Length > 0) sb.Append(' ');
                sb.Append(sb.Length == 0 ? c : char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        static float Row(int x, float y, int w, string label, Action widget)
        {
            Ui.Text(label, x, y + 6, 20, Theme.TextDim, Ui.Body);
            widget();
            return y + 38;
        }

        static void Hint(int x, ref float y, int w, string text)
        {
            foreach (var line in Wrap(text, w, 19))
            {
                Ui.Text(line, x, y, 19, Theme.TextFaint, Ui.Body);
                y += 23;
            }
            y += 8;
        }

        static IEnumerable<string> Wrap(string text, float width, float size)
        {
            var line = "";
            foreach (var word in text.Split(' '))
            {
                string attempt = line.Length == 0 ? word : line + " " + word;
                if (Ui.Measure(attempt, size, Ui.Body).X > width && line.Length > 0) { yield return line; line = word; }
                else line = attempt;
            }
            if (line.Length > 0) yield return line;
        }

        // =====================================================================
        //  Widgets
        // =====================================================================
        bool Clickable(Rectangle r, Vector2 mouse, Rectangle? clip) =>
            Raylib.CheckCollisionPointRec(mouse, r) && (clip == null || Raylib.CheckCollisionPointRec(mouse, clip.Value)) && !dragging;

        bool SmallButton(Rectangle r, string label, bool active = false, bool enabled = true, float size = 19, Rectangle? clip = null)
        {
            Vector2 mouse = Raylib.GetMousePosition();
            bool hover = enabled && Clickable(r, mouse, clip);
            Color fill = active ? Theme.Accent : hover ? new Color(70, 40, 52, 255) : Theme.ButtonFill;
            Raylib.DrawRectangleRounded(r, 0.3f, 6, fill);
            Raylib.DrawRectangleRoundedLinesEx(r, 0.3f, 6, active || hover ? 2 : 1, enabled ? Theme.Accent : new Color(90, 60, 66, 255));
            string t = Ui.Fit(label, r.Width - 8, size, Ui.Body);
            var m = Ui.Measure(t, size, Ui.Body);
            Ui.Text(t, r.X + (r.Width - m.X) / 2, r.Y + (r.Height - m.Y) / 2, size, enabled ? Theme.Text : Theme.TextFaint, Ui.Body);
            return hover && Raylib.IsMouseButtonPressed(MouseButton.Left);
        }

        void BeginEdit(string id, string initial, Action<string> commit)
        {
            CommitFocus();
            focusId = id;
            focusFrame = frame;
            focusBuffer = initial;
            focusCommit = commit;
        }

        void CommitFocus()
        {
            if (focusId == null) return;
            var commit = focusCommit;
            string value = focusBuffer;
            focusId = null;
            focusCommit = null;
            commit?.Invoke(value);
        }

        void HandleTextInput()
        {
            if (focusId == null) return;
            int c;
            while ((c = Raylib.GetCharPressed()) != 0)
                if (c >= 32 && focusBuffer.Length < 1000) focusBuffer += char.ConvertFromUtf32(c);
            if (Pressed(KeyboardKey.Backspace) && focusBuffer.Length > 0)
                focusBuffer = Ctrl ? "" : focusBuffer[..^1];
            if (Ctrl && Raylib.IsKeyPressed(KeyboardKey.V)) focusBuffer += (Raylib.GetClipboardText_() ?? "").Replace("\r", "").Replace("\n", "\\n");
            if (Raylib.IsKeyPressed(KeyboardKey.Enter) || Raylib.IsKeyPressed(KeyboardKey.KpEnter)) CommitFocus();
            else if (Raylib.IsKeyPressed(KeyboardKey.Escape)) { focusId = null; focusCommit = null; }
            else if (Raylib.IsKeyPressed(KeyboardKey.Tab)) CommitFocus();
        }

        void TextWidget(float x, float y, float w, string id, string value, Action<string> commit, Vector2 mouse, Rectangle? clip)
        {
            var r = new Rectangle(x, y, w, 32);
            bool focused = focusId == id;
            bool hover = Clickable(r, mouse, clip);
            Raylib.DrawRectangleRounded(r, 0.25f, 6, focused ? new Color(40, 42, 64, 255) : new Color(26, 28, 44, 255));
            Raylib.DrawRectangleRoundedLinesEx(r, 0.25f, 6, focused ? 2 : 1, focused ? Theme.Accent : hover ? Theme.AccentSoft : new Color(70, 74, 100, 255));
            string shown = focused ? focusBuffer : value;
            bool caret = focused && (int)(Raylib.GetTime() * 2) % 2 == 0;
            string fitted = focused ? FitEnd(shown, w - 18, 19) : Ui.Fit(shown, w - 14, 19, Ui.Body);
            Ui.Text(fitted + (caret ? "|" : ""), x + 7, y + 6, 19, Theme.Text, Ui.Body);

            if (hover && Raylib.IsMouseButtonPressed(MouseButton.Left) && !focused) BeginEdit(id, value, commit);
            else if (focused && frame != focusFrame && Raylib.IsMouseButtonPressed(MouseButton.Left) && !hover) CommitFocus();
        }

        static string FitEnd(string text, float width, float size)
        {
            while (text.Length > 0 && Ui.Measure(text, size, Ui.Body).X > width) text = text[1..];
            return text;
        }

        void NumberWidget(float x, float y, float w, string id, double value, double step, Action<double> set, Vector2 mouse, Rectangle? clip, bool present = true, bool integer = false)
        {
            var minus = new Rectangle(x, y, 32, 32);
            var plus = new Rectangle(x + w - 32, y, 32, 32);
            double mult = Shift ? 10 : 1;
            if (SmallButton(minus, "-", false, true, 22, clip)) set(Math.Round(value - step * mult, 4));
            if (SmallButton(plus, "+", false, true, 22, clip)) set(Math.Round(value + step * mult, 4));

            var field = new Rectangle(x + 36, y, w - 72, 32);
            string text = present ? value.ToString(integer ? "0" : "0.###", CultureInfo.InvariantCulture) : "-";
            if (Clickable(field, mouse, clip))
            {
                overNumber = true;
                float wheel = Raylib.GetMouseWheelMove();
                if (wheel != 0 && focusId != id) set(Math.Round(value + Math.Sign(wheel) * step * mult, 4));
            }
            TextWidget(field.X, field.Y, field.Width, id, text, v =>
            {
                if (double.TryParse(v.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) set(d);
                else if (v.Trim().Length > 0) Status($"'{v}' is not a number");
            }, mouse, clip);
            if (focusId == id && focusBuffer == "-") focusBuffer = "";
        }

        void BoolWidget(float x, float y, string id, bool value, Action<bool> set, Vector2 mouse, bool present = true)
        {
            var r = new Rectangle(x, y + 3, 56, 26);
            bool hover = Clickable(r, mouse, null);
            Raylib.DrawRectangleRounded(r, 1f, 12, value ? Theme.Accent : new Color(50, 54, 78, 255));
            Raylib.DrawCircleV(new Vector2(value ? r.X + r.Width - 13 : r.X + 13, r.Y + 13), 10, present ? Color.White : Theme.TextFaint);
            if (hover && Raylib.IsMouseButtonPressed(MouseButton.Left)) set(!value);
        }

        void ColorWidget(float x, float y, float w, string id, string hex, Action<string> set, Vector2 mouse, Rectangle? clip, bool present = true)
        {
            var sw = new Rectangle(x, y, 32, 32);
            Color c = Ui.ParseHex(hex, Color.White);
            Raylib.DrawRectangleRounded(sw, 0.25f, 6, c);
            Raylib.DrawRectangleRoundedLinesEx(sw, 0.25f, 6, paletteFor == id ? 3 : 1, paletteFor == id ? Theme.Accent : Color.White);
            if (Clickable(sw, mouse, clip) && Raylib.IsMouseButtonPressed(MouseButton.Left)) paletteFor = paletteFor == id ? null : id;
            TextWidget(x + 40, y, w - 40, id + ".hex", present ? hex : "-", v =>
            {
                string h = v.Trim().ToUpperInvariant();
                if (!h.StartsWith("#")) h = "#" + h;
                if (System.Text.RegularExpressions.Regex.IsMatch(h, "^#[0-9A-F]{6}([0-9A-F]{2})?$")) set(h);
                else Status("Colours look like #RRGGBB or #RRGGBBAA");
            }, mouse, clip);
        }

        float PaletteRow(int x, float y, int w, Action<string> set, Vector2 mouse, Rectangle area)
        {
            int size = (w - (Palette.Length - 1) * 4) / Palette.Length;
            for (int i = 0; i < Palette.Length; i++)
            {
                var r = new Rectangle(x + i * (size + 4), y, size, size);
                Raylib.DrawRectangleRounded(r, 0.25f, 6, Ui.ParseHex(Palette[i], Color.White));
                bool hover = Clickable(r, mouse, area);
                Raylib.DrawRectangleRoundedLinesEx(r, 0.25f, 6, hover ? 3 : 1, hover ? Theme.Accent : new Color(255, 255, 255, 90));
                if (hover && Raylib.IsMouseButtonPressed(MouseButton.Left)) { set(Palette[i]); paletteFor = null; }
            }
            return y + size + 10;
        }

        void EnumWidget(float x, float y, float w, string id, string value, string[] options, Action<string> set, Vector2 mouse, Rectangle? clip)
        {
            var r = new Rectangle(x, y, w, 32);
            int i = Array.IndexOf(options, value);
            if (SmallButton(r, $"<  {value}  >", false, true, 19, clip)) set(options[(i + 1 + options.Length) % options.Length]);
            if (Clickable(r, mouse, clip) && Raylib.IsMouseButtonPressed(MouseButton.Right)) set(options[(i - 1 + options.Length * 2) % options.Length]);
        }

        float AssetGrid(int x, float y, int w, string current, Action<string> set, Vector2 mouse, Rectangle area)
        {
            if (assets.Length == 0)
            {
                Ui.Text("No PNGs found in Assets/ or Assets/Decorations/", x, y, 18, Theme.TextFaint, Ui.Body);
                return y + 30;
            }
            int cols = 5, gap = 6;
            int cell = (w - gap * (cols - 1)) / cols;
            for (int i = 0; i < assets.Length; i++)
            {
                var r = new Rectangle(x + (i % cols) * (cell + gap), y + (i / cols) * (cell + 22 + gap), cell, cell);
                bool isCurrent = string.Equals(assets[i], current, StringComparison.OrdinalIgnoreCase);
                bool hover = Clickable(r, mouse, area);
                Raylib.DrawRectangleRounded(r, 0.15f, 6, isCurrent ? new Color(70, 30, 36, 255) : new Color(26, 28, 44, 255));
                Raylib.DrawRectangleRoundedLinesEx(r, 0.15f, 6, isCurrent || hover ? 2 : 1, isCurrent ? Theme.Accent : hover ? Theme.AccentSoft : new Color(70, 74, 100, 255));
                var tex = Program.GetTexture(assets[i]);
                if (tex.Id != 0)
                {
                    float s = (cell - 14) / (float)Math.Max(tex.Width, tex.Height);
                    Ui.TextureCentered(tex, r.X + cell / 2f, r.Y + cell / 2f, s, Color.White);
                }
                Ui.TextCentered(Ui.Fit(assets[i], cell, 15, Ui.Body), r.X + cell / 2f, r.Y + cell + 2, 15, isCurrent ? Theme.Text : Theme.TextFaint, Ui.Body);
                if (hover && Raylib.IsMouseButtonPressed(MouseButton.Left)) set(assets[i]);
            }
            int rows = (assets.Length + cols - 1) / cols;
            return y + rows * (cell + 22 + gap) + 4;
        }

        // =====================================================================
        //  Event list
        // =====================================================================
        void DrawEventList(int x, int w, Vector2 mouse)
        {
            RefreshEventTimes();
            int top = ListTop;
            Raylib.DrawRectangle(PanelX + 4, top - 6, PanelWidth - 4, 2, new Color(255, 255, 255, 30));
            Ui.Text($"EVENTS ({Events.Count})", x, top, 22, Theme.Accent);
            Ui.TextRight("click to jump", x + w, top + 3, 17, Theme.TextFaint, Ui.Body);

            var area = new Rectangle(PanelX + 4, top + 30, PanelWidth - 8, ScreenH - 50 - (top + 30));
            const int rowH = 30;
            float max = Math.Max(0, eventTimes.Count * rowH - area.Height);
            if (Raylib.CheckCollisionPointRec(mouse, area)) listScroll -= Raylib.GetMouseWheelMove() * rowH * 2;
            listScroll = Math.Clamp(listScroll, 0, max);

            Raylib.BeginScissorMode((int)area.X, (int)area.Y, (int)area.Width, (int)area.Height);
            for (int row = 0; row < eventTimes.Count; row++)
            {
                float ry = area.Y + row * rowH - listScroll;
                if (ry < area.Y - rowH || ry > area.Y + area.Height) continue;
                var (index, time) = eventTimes[row];
                var o = Events[index] as JsonObject;
                if (o == null) continue;

                var r = new Rectangle(area.X + 6, ry, area.Width - 12, rowH - 3);
                bool hover = Clickable(r, mouse, area);
                bool isSel = index == selected;
                if (isSel || hover) Raylib.DrawRectangleRounded(r, 0.3f, 6, isSel ? new Color(80, 32, 38, 255) : new Color(36, 38, 58, 255));
                bool past = time < Program.SongTime - 0.01f;

                DrawMarker(o, new Vector2(r.X + 14, ry + rowH / 2f - 1), 7, false);
                string when = o["Bar"] != null ? $"{GetInt(o, "Bar", 1)}.{GetNum(o, "Beat", 1):0.##}" : $"{time:0.00}s";
                Ui.Text(when, r.X + 30, ry + 5, 18, past ? Theme.TextFaint : Theme.Text, Ui.Body);
                Ui.Text(Ui.Fit(Describe(o), r.Width - 110, 18, Ui.Body), r.X + 100, ry + 5, 18, past ? Theme.TextFaint : Theme.TextDim, Ui.Body);

                if (hover && Raylib.IsMouseButtonPressed(MouseButton.Left))
                {
                    selected = index;
                    focusId = null;
                    propScroll = 0;
                    // Jump so the event sits just ahead of the hit line
                    Seek(Math.Max(-Program.LeadInTime, time - StepSeconds(Math.Max(0, time - 1), 1)));
                }
            }
            Raylib.EndScissorMode();
        }

        string Describe(JsonObject o)
        {
            string type = Type(o);
            return type switch
            {
                "SpawnDecoration" or "SpawnArrow" => $"Sprite {GetStr(o, "TextureName") ?? "arrow"}" + (GetBool(o, "IsStatic", false) ? " (static)" : ""),
                "SpawnText" => $"Text \"{(GetStr(o, "Text") ?? "").Replace("\n", " ")}\"",
                "PulseColor" => $"Pulse {GetStr(o, "Target") ?? "Background"} {GetStr(o, "ColorHex")}",
                "AnimateColor" => $"Colour {GetStr(o, "Target") ?? "Background"} -> {GetStr(o, "ColorHex")}",
                "AnimateDecoration" => $"Animate {GetStr(o, "TargetID") ?? GetStr(o, "ID")}",
                "ToggleBassClef" => GetBool(o, "ToggleState", false) ? "Bass clef on" : "Bass clef off",
                _ => type
            };
        }

        // =====================================================================
        //  Help & tooltips
        // =====================================================================
        static void Tooltip(Vector2 mouse, string text)
        {
            var m = Ui.Measure(text, 18, Ui.Body);
            float x = Math.Min(mouse.X + 18, Raylib.GetScreenWidth() - 480 - m.X - 20);
            var r = new Rectangle(x, mouse.Y + 18, m.X + 16, m.Y + 10);
            Raylib.DrawRectangleRounded(r, 0.3f, 6, new Color(10, 12, 22, 235));
            Raylib.DrawRectangleRoundedLinesEx(r, 0.3f, 6, 1, Theme.Accent);
            Ui.Text(text, r.X + 8, r.Y + 5, 18, Theme.Text, Ui.Body);
        }

        void DrawHelp()
        {
            (string key, string what)[] lines =
            {
                ("1 - 6", "Select / Sprite / Text / Pulse / Colour / Clef tool"),
                ("Click", "Place with the current tool (right-click: Select tool)"),
                ("Drag", "Move a sprite or timeline marker (snaps to the grid)"),
                ("Shift + click", "Pulse tool: pulse every beat of the bar"),
                ("Wheel, Left/Right", "Scrub by the snap size (hold Shift: one bar)"),
                ("Up / Down", "Move the selection one staff step"),
                ("G", "Cycle snap: 1, 1/2, 1/4, 1/8 beat"),
                ("Delete", "Delete the selection"),
                ("Cmd/Ctrl + D", "Duplicate one grid step later"),
                ("Cmd/Ctrl + C, V", "Copy, then paste at the playhead"),
                ("Cmd/Ctrl + Z, Y", "Undo, redo (Shift + Z also redoes)"),
                ("Cmd/Ctrl + S", "Save (sorted by time, old file kept as .bak)"),
                ("Space / Home", "Play or pause / back to the start"),
                ("H", "Show or hide this list"),
                ("F1", "Hide the editor"),
            };
            float w = 900, h = lines.Length * 32 + 90;
            var r = new Rectangle((ScreenW - PanelWidth - w) / 2, (ScreenH - h) / 2, w, h);
            Ui.Panel(r, 0.05f);
            Ui.Text("SHORTCUTS", r.X + 30, r.Y + 22, 28, Theme.Accent);
            for (int i = 0; i < lines.Length; i++)
            {
                float y = r.Y + 70 + i * 32;
                Ui.Text(lines[i].key, r.X + 30, y, 20, Theme.Accent, Ui.Body);
                Ui.Text(Ui.Fit(lines[i].what, w - 270, 20, Ui.Body), r.X + 240, y, 20, Theme.Text, Ui.Body);
            }
            if (Raylib.IsMouseButtonPressed(MouseButton.Left) && !Raylib.CheckCollisionPointRec(Raylib.GetMousePosition(), r) && Raylib.GetMousePosition().X < PanelX)
                showHelp = false;
        }
    }
}
