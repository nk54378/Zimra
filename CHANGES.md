# Zimra: cleanup and editor rework

## Files
Drop these into your project folder in place of the old Program.cs and LevelEditor.cs. New .cs files are picked up automatically by the SDK-style project.

| File | Contents |
|---|---|
| Program.cs | Startup, screens (menu, worlds, level map, results), transitions |
| Gameplay.cs | Notes, hit detection, scoring, combat, HUD, effects |
| LevelEditor.cs | The rebuilt F1 editor |
| LevelLoader.cs | Reads MIDI and JSON into a level (lenient, gives warnings rather than crashing) |
| Models.cs | Data classes (Chart, LevelData, decorations, events) |
| Ui.cs | Fonts, theme colours, buttons and text helpers |

Packages: Raylib-cs 8.1.0 and Melanchall.DryWetMidi 8.0.3 (see Zimra.csproj). Your existing JSON files load as they are.

## Bug fixes
- MIDI start-up no longer crashes when no device is found; it listens to every device and R on the main menu rescans.
- Background texture was unloaded twice; the MIDI file was read twice; JSON was parsed before the time signature was known.
- Colour pulses now return to the live base colour, keep alpha and work after seeking.
- Chord and Space judgements no longer show in red; accuracy no longer counts ghost notes; Space can't hit ghost notes.
- Ghost notes don't spawn enemies and are left out of beams.
- Bass stave lines use the stave colour; key signatures are drawn and the hit zone moves to make room.
- Scrolling decorations appear as they approach instead of popping in at the hit line.
- Quit no longer calls Environment.Exit; music is no longer paused and resumed every frame; the timer follows the audio clock.
- "Quit Level" text no longer overflows its button.

## Visuals
- osu!-style drifting triangles, a pulsing logo with beat rings (synced to the 128 BPM menu track), and buttons that grow and glow on hover. The button colours are unchanged.
- Red and navy wipe transitions, world cards, an animated level map with locked levels and rank badges, and personal bests saved to scores.json.
- Gameplay: approach rings, hit bursts, combo and judgement pops, screen shake, a low-health vignette, a progress bar, a 3-2-1-GO countdown, a pause menu on Esc, character lunge and hurt animations.
- Results screen with an animated rank and counting stats.

## Controls
- Laptop keys: A S D F G H J K L ; ' play C4 to F5, and W E T Y U O P are the black keys.
- Esc pauses, F11 toggles fullscreen, F1 opens the editor.

## Level editor (F1)
The editor pauses the level and opens a side panel. Pick a tool, click on the stave and it is written into the JSON as Bar / Beat / StaffStep, snapped to the grid. Everything previews live.

| Key | Action |
|---|---|
| 1-6 | Select / Sprite / Text / Pulse / Colour / Clef tool |
| Click | Place (right-click returns to Select) |
| Drag | Move a sprite, or a marker on the timeline strip |
| Shift+click | Pulse tool: pulse every beat of the bar |
| Wheel, Left/Right | Scrub by the snap size (Shift: one bar) |
| Up/Down | Move the selection one staff step |
| G | Snap 1, 1/2, 1/4, 1/8 beat |
| Delete | Delete the selection |
| Cmd/Ctrl+D, C, V | Duplicate, copy, paste at the playhead |
| Cmd/Ctrl+Z, Y | Undo, redo |
| Cmd/Ctrl+S | Save |
| Space, Home, H | Play/pause, back to the start, shortcut list |

- Sprites: choose a texture from the thumbnails (every PNG in Assets/ and Assets/Decorations/, except game textures and bg_* files). "Stays on screen" makes a static sprite.
- Text: scrub to where the lyric starts, click where it should go, type it and press Enter. Type \n for a new line.
- The panel shows every field for the selected event with +/- buttons (the mouse wheel and Shift x10 also work), toggles, colour swatches and text boxes. Fields that aren't set yet appear greyed out; editing one adds it, and x removes it.
- The event list is sorted by time; click a row to jump there.
- Save sorts events by time, writes 4-space-indented JSON without escaping apostrophes, and keeps the previous file as level.json.bak.

## Dev flags
`--windowed`, `--level Levels/World1/level1.mid`, `--editor`, `--seek 12.5`, `--autoplay`, `--screen play|worlds|levels|end`
