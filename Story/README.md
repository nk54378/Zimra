# Story cutscenes

Each cutscene is one JSON file in this folder. The game picks them up by name:

| File | Plays |
|---|---|
| `prologue.json` | The first time the player presses Story Mode |
| `world1_intro.json` | The first time the player enters World 1 (`world2_intro.json` for World 2, and so on) |
| `world1_boss.json` | The first time the player starts the last level of World 1 |
| `world1_level3.json` | The first time the player starts level 3 of World 1 (works for any level) |

If a file doesn't exist, nothing plays, so you can add cutscenes whenever you like. Which ones have been seen is saved in `story_progress.json` next to `scores.json`. Delete that file (or run with `--reset-story`) to see them all again. To test one cutscene straight away, run `dotnet run -- --cutscene world1_intro --windowed`.

`//` comments and trailing commas are allowed in these files. If a file has a mistake in it, the game shows the error on screen instead of the cutscene, so it's easy to find.

## Controls

- Space, Enter, Right arrow or click: finish the line, then go to the next one
- Hold Ctrl: fast-forward
- Esc twice, or the Skip button: skip the cutscene (it still counts as seen)
- Worlds screen: "Replay story" rewatches a world's intro (World 1 includes the prologue)

## Format

```json
{
    "Music": "Assets/Music/story_theme.mp3",   // optional; otherwise the menu music keeps playing
    "MusicVolume": 0.8,
    "Characters": {
        "Tehilo": { "Color": "#66BFFF", "Shape": "quad" },
        "Elyra":  { "Color": "#FF64C8", "Shape": "round", "DisplayName": "Elyra" }
    },
    "Slides": [
        {
            "Background": "bg_auvora",
            "BackgroundColor": "#1C3A4A", "BackgroundColor2": "#0A1418",
            "BackgroundBrightness": 0.7,
            "Effect": "calm",
            "Caption": "AUVORA", "CaptionSub": "World 1",
            "Left": ["Tehilo"], "Right": [],
            "Enemies": 0, "Boss": false,
            "Lines": [
                { "Speaker": "Tehilo", "Expression": "happy", "Text": "It's so peaceful here." },
                { "Speaker": "Narrator", "Text": "Then the ground shook.", "Shake": 12, "Flash": "#FA3834", "Enemies": 3, "Effect": "alarm" }
            ]
        }
    ]
}
```

### Characters

- `Color`: the name tag colour, and the colour of the placeholder body.
- `Shape`: the placeholder body, either `quad` (the slanted blue quadrilateral) or `round`.
- `Portrait`: when you have art, put it in `Assets/Story/Characters/`. `Tehilo.png` is used automatically. For expressions add `Tehilo_happy.png`, `Tehilo_sad.png` and so on; any that are missing fall back to `Tehilo.png`. Portraits are drawn 560px tall with their feet just behind the dialogue box, and flipped for characters on the right, so draw them facing right.
- Anyone who speaks but isn't listed still works, with a grey placeholder.

### Slides

A slide is one background with any number of lines of dialogue.

- `Background`: an image name. The game looks in `Assets/Story/` first, then `Assets/` (so `bg_auvora`, `starry_background` and so on work). It's scaled to cover the screen with a slow zoom. With no image, it uses the `BackgroundColor` to `BackgroundColor2` gradient.
- `BackgroundBrightness`: 0 to 1. Lower values darken the image so the characters and text stand out.
- `Effect`:
  - `none`: no effect.
  - `calm`: drifting glowing motes.
  - `stars`: twinkling stars.
  - `warp`: stars streaking past.
  - `alarm`: a pulsing red sky with embers and small tremors.
  - `signal`: the antagonist's glowing stave across the sky, with notes scrolling along it.
- `Caption` / `CaptionSub`: a big title card as the slide opens. Clicking skips the card.
- `Left` / `Right`: who stands on each side. Each side can hold more than one character.
- `Enemies`: how many enemies stand in the middle. `Boss` shows one big enemy.

### Lines

- `Speaker`: a character name, or `Narrator` (or leave it empty) for centred narration with no name tag.
- `Text`: what's said. Use `\n` to force a line break. Curly quotes and dashes from macOS are converted automatically, because the fonts only have plain ASCII characters.
- `Expression`: the speaker's face from this line on: `neutral`, `happy`, `sad`, `surprised`, `angry`, `scared` or `hurt`. With real portraits, this picks `Name_expression.png`.
- `Shake`: shakes the screen as the line starts (about 5 to 20).
- `Flash`: flashes the screen a colour as the line starts, e.g. `"#FFFFFF"`.
- `Left`, `Right`, `Enemies`, `Boss`, `Effect`: change the stage from this line on, for example to make Elyra appear mid-scene with `"Right": ["Elyra"]`, or to clear the stage with `"Left": []`.
