# Zimra

A high-performance, hardware-integrated desktop rhythm action game built from scratch using C# and Raylib. Players connect a physical keyboard to their computer and play scrolling sheet music notes in real-time to defeat incoming waves of enemies.

## 🚀 Key Engineering Highlights
* **Hardware Integration (MIDI API):** Implemented a low-latency input pipeline that hooks into native OS MIDI subsystems to poll and parse hardware keyboard events (`Note On`, `Note Off`, and key velocity).
* **Custom 2D Game Loop:** Bypassed bloated game engines to engineer a lightweight rendering loop using Raylib, maintaining a locked 60+ FPS with precise frame-rate independent state updates (`requestAnimationFrame` equivalent mechanics).
* **Millisecond note collision pipeline:** Designed an efficient time-based tracking system to align scrolling vector notes on the screen with real-time hardware timestamps for high-precision hit detection.
* **Decoupled Architecture:** Followed clean software engineering principles to strictly separate input parsing engines, game-state logic registers, and the graphics rendering pipeline.

## 🛠️ Built With
* **Language:** C# (.NET Core)
* **Graphics & Audio Library:** Raylib (via Raylib-cs bindings)
* **API Hooks:** [e.g., WinMM MIDI API / Managed-MIDI]

## 🎮 How to Play / Run Safely
*(Note: This project is an active work-in-progress prototype)*

### Prerequisites
* .NET SDK 8.0 or higher
* A MIDI-compatible keyboard connected via USB (optional, falls back to standard keyboard mapping)

### Installation & Execution
1. Clone this repository:
   ```bash
   git clone https://github.com
   ```
2. Navigate to the project directory:
   ```bash
   cd Zimra
   ```
3. Build and execute the environment:
   ```bash
   dotnet run
   ```

## Bugs / things to fix:

- The chord blocks continue to respond to previous keys which were held
- The treble clef and bass clef should *probably* be a bit smaller
- The stems of quavers / semiquavers etc
- Move C ledger line slightly up/down, and remove higher/lower ledger lines (+ potentially the option to separate the staves)

## Things to add

- More backgrounds
- Floor / foreground
- Sprites for characters
- Sprites for enemies
- When the key changes (+ at the beginning), highlighting the lines which are sharpened or flattened
- Sound effects for buttons
- Shaders (e.g motion blur)
- Particles / gradients
- Helpful tips before levels
- Black box behind levels (mix with 'clef' in the level editor, rename to 'other')
- Combine colour and pulse
- Make accidentals and (maybe) decorations pulse to the rhythm