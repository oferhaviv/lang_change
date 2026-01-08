# Knowing Your Keyboard Language — Without Looking at the Screen

<p align="center">
  <img src="about/Real Keyboard changing color - direction.gif" width="800" />
</p>

If you work with more than one keyboard language, you’ve probably hit this before:  
you start typing, stop — and realize the language is wrong.

Sure, Windows shows a small indicator near the clock, but in practice:
- it’s far from your focus area
- easy to miss in fullscreen apps or IDEs
- and useless when you briefly glance down at the keyboard

The idea was simple:  
**provide a clear, physical indication directly on the keyboard.**

When the input language switches, a small set of keys changes color.  
When switching back, the keyboard returns to its normal state.

No profile switching, no RGB reset — just an immediate visual cue exactly where your eyes already are.

---

## How It Works (High Level)

## Architecture / Flow

<p align="center">
  <img src="about/Seq Diagram.png" alt="Language change sequence diagram" />
  <br/>
  <em>Sequence diagram – detecting Windows input language changes and updating keyboard lighting</em>
</p>

The solution is split into two independent processes that communicate via REST:

### 1) Windows Language Listener (C#)

A lightweight background listener tracks the currently active keyboard language in Windows.

Since Windows does not expose a dedicated event for input language changes, the listener:
- detects the active (foreground) window
- reads the keyboard layout of its thread using `GetKeyboardLayout`
- extracts the `LangID` (e.g. `0x040D` for Hebrew, `0x0409` for English)

To ensure long-term stability (including elevated apps where hooks may fail), it uses a low-frequency polling fallback with debounce — minimal CPU usage, no event spam.

When a language change is detected, the listener sends a simple REST call (e.g. `/heb` or `/eng`).

---

### 2) Keyboard Color Controller (Python + iCUE)

A small Python REST service (CherryPy) listens for these calls and interacts with Corsair iCUE via the official SDK.

- On `/heb`:
  - requests **Shared Lighting Control**
  - applies a color overlay to selected keys (no profile switching)
- On `/eng`:
  - clears the overlay
  - releases control so iCUE resumes normal behavior

All key selections, colors, ports, and mappings are defined in a shared `config.json` file used by both processes.

---

## Why This Approach

- No drivers
- No admin privileges
- No iCUE profile duplication
- Works reliably across applications
- Easy to extend (additional languages, different key groups)

The sequence diagram in this repository illustrates the full flow:

**Windows → Listener → REST → iCUE SDK → Keyboard**

---

## Possible Next Steps

- Package as a standalone installer
- Add a tray UI / pause toggle
- Extend support to additional keyboards or vendors

Happy hacking 👋


## Demo

[▶ Watch demo video](about/video.mp4)
