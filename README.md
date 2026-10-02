# wall42

**Living particles on your macOS desktop — and zero CPU the moment a window covers them.**
Part of the **42 series** by [Okle42](https://github.com/Okle42) — follow for more AI tools that actually ship.

[繁體中文說明](README.zh-TW.md)

Animated particles for the macOS wallpaper layer: softly glowing, drifting particles plus a neural-network mesh that links nearby ones. It sits above the system wallpaper and below your desktop icons.

**When it's fully covered by windows, it stops drawing: CPU drops to zero and memory falls by 42%.** That's the core design of the whole program, not a side feature.

---

## Usage

```bash
./preset.sh         # list the built-in styles
./preset.sh neon    # apply one (takes effect on save — the screen changes immediately)
./build.sh          # build
./wall42            # run in the foreground; Ctrl-C to quit
./install.sh        # install and launch at login
./uninstall.sh      # remove (your config is kept; the system wallpaper setting was never touched)
```

Config lives at `~/.config/wall42/config.json`. **Changes apply as soon as you save — no restart needed.**

---

## Settings

### background — background layer

| Key | What it does | Recommended |
|---|---|---|
| `mode` | `gradient` (radial gradient) / `solid` (flat color) | gradient gives cinematic lighting; solid is the cheapest |
| `centerColor` | Gradient center color | Keep it dark — too bright and it drowns out the particles |
| `edgeColor` | Gradient edge color | `#000000` |
| `radius` | How far the gradient spreads | 0.6 is a tight glow, 1.02 spreads out, 1.5 fills almost everything |
| `solidColor` | Color used when `mode: solid` | |

### motion — motion layer

| Key | What it does | Recommended |
|---|---|---|
| `particleCount` | **Particles per main-display area** (i.e. density). With multiple displays, the actual count for the whole world = this value × world area ÷ main display area — two side-by-side displays means twice as many, so each display looks equally dense | 140. **In testing, CPU is almost independent of particle count** — 300 vs. 120 particles differs by 0.6% |
| `fps` | Frame-rate cap | 30. On a 60 Hz display only 60/30/20/15 are meaningful; 24 effectively becomes 20 |
| `secondaryFps` | fps for non-focused displays (the ones with neither the pointer nor the frontmost app's window). Unset = half of `fps`; has no effect with a single display | 15 |
| `colorA` / `colorB` | The two end colors; particle hues are spread between them | Defaults: cyan `#1ADBF5` / hot pink `#FC3D99` |
| `speed` | Drift speed | 11. Go much faster and you lose the "floating" feel |
| `sizeMin/Max` | Regular particle size | 7–13. Below 5 the white core can't show |
| `nodeRatio` | Fraction of larger "node" particles | 0.20 |
| `nodeSizeMin/Max` | Node size | 17–27 |
| `brightness` | Overall brightness multiplier | 1.0 |
| `breathSpeed` | How fast particles "breathe" (pulse in and out) | 0.7 |
| `sizeBias` | Size-distribution skew. 1 = uniform; higher = more small particles | 2.2. Use 3.0 for starfield styles |
| `twinkleVariance` | How much each particle's twinkle rate varies. 0 = everything breathes in sync | 0.6. Use 0.85 for starfield styles |

### motion.effect — effects (hot-reloaded; switches on save)

| Value | Behavior | Related keys |
|---|---|---|
| `floating` | The original glowing drift + link mesh | All of them |
| `snow` | Snowflakes fall slowly and sway side to side, fade out just before the bottom of the display's usable area (clear of the Dock), then drift down again from the top. x is continuous across the whole world, so flakes cross display seams. Bokeh particles act as big close-up flakes that fall faster and sway more | `speed` fall speed, `wind` wind speed (positive = rightward, default 8), `softness` edge softness, `bokeh.*` close-up flakes |
| `sand` | Hourglass: a thin stream of sand pours from the top of each display; grains accelerate as they fall, slide down at the angle of repose, and build dunes. The whole pile slowly sinks and fades out at the bottom, with older sand getting darker. The busier the machine, the faster the stream | `speed` terminal fall speed, `streams` streams per display (default 1), `sizeMin/Max` grain size, `particleCount` grain pool (2000+ recommended) |

`softness` (0..1): particle edge softness. 0 is the original crisp point of light; 0.5 is recommended for snowflakes.
`background.mode` also accepts `vertical`: a top-to-bottom gradient computed across the whole world (top `edgeColor`, bottom `centerColor`, with `radius` as the curve), so two displays join with no visible break. The snow and sand presets use it.

### motion.sessions — Claude session lights

Each active Claude session gets a persistent bright point with a thin outer ring. Its position is derived from a hash of the session id, so the same session always appears in the same spot, inside the usable area of one of your displays.
Busy sessions glow continuously and frequently fire off queries in the attention network (idle ones only occasionally). Works on top of any effect.

| Key | What it does |
|---|---|
| `enabled` | On/off |
| `source` | `auto` (default) reads `~/.claude/sessions/<pid>.json`; a session counts only if its pid is alive, and `status=busy` counts as busy. `file` only looks at `~/.config/wall42/sessions.json` |
| `size` | Point size; defaults to nodeSizeMax × 1.15 |

When `~/.config/wall42/sessions.json` exists it takes precedence over auto. The format is `{"count":5,"busy":2}` or `{"sessions":[{"id":"a","busy":true}]}`, written by the MCP tool `wall42_sessions(count, busy)`; calling `wall42_sessions()` without a count deletes the file and goes back to auto.

### motion.link — links

| Key | What it does | Recommended |
|---|---|---|
| `enabled` | Turn off for pure drifting particles | |
| `distance` | No link beyond this distance | 168. Larger makes a denser structure, but link count grows O(n²) |
| `opacity` | Line opacity | 0.40. Below 0.26 the neural-network feel disappears |
| `boost` | Extra brightness multiplier for lines | 1.35 |
| `onlyNodes` | Only the large "node" particles get linked, giving a constellation look | Set true for starfield styles. As a bonus, the O(n²) work only runs across a few nodes |

### Link modes (motion.link.mode)

| Mode | Behavior | How it feels |
|---|---|---|
| `proximity` | Link anything within range; the links directly reflect the geometry | A fixed mesh — reads as a diagram, not as computation |
| `traffic` | A pool of in-flight transfers: fade in → pulse runs across → fade out, constantly switching partners | Feels like data moving, but random pairings look like noise |
| `attention` | **Focus on one node → fire queries at its neighbors → pulses flow back → the focus flares to mark an "aha" → move on** | Structured, like it's thinking. Several foci run in parallel when busy |

The three phases of `attention` (query 45% / return 35% / conclude 20%) only change the alpha
and pulse position of the same set of line segments — no extra geometry or draw calls. In testing it
actually uses less CPU than `traffic` (1.33% vs. 4.2%, because far fewer segments exist at once).

### motion.pulse — pulses flowing along links

This is where the "something is computing" look comes from. The line fragment shader already interpolates
the position along each line, so all it takes is a Gaussian wave packet that moves over time — no extra
geometry, and the cost is one extra exponential per line pixel.

| Key | What it does | Recommended |
|---|---|---|
| `speed` | Lines traversed per second | 0.35. The actual speed is further multiplied by activity |
| `strength` | Pulse brightness | 1.2 |
| `width` | Gaussian width; smaller looks more like a single dot | 0.003 |

### motion.activity — how busy it looks

| Key | What it does |
|---|---|
| `source` | `system` reads system CPU load / `manual` is set via MCP / `off` disables it |
| `manualLevel` | Value used when source=manual, 0..1 |
| `smoothing` | 0..0.99; higher means slower changes. 0.85 takes about 6 seconds to reach full and never twitches |
| `minLoad` / `maxLoad` | The system-load range mapped to activity 0 and 1 |

Activity affects all of these at once: pulse speed and brightness, link brightness, and particle breathing speed.

### motion.bokeh — foreground bokeh

**Fakes out-of-focus highlights** with large sizes and a gentler falloff curve — no real blur pass, so it costs the same as drawing small dots.

| Key | What it does | Recommended |
|---|---|---|
| `ratio` | Share of bokeh particles | 0.15 |
| `sizeMin/Max` | Highlight size | 34–72 |
| `speed` | The foreground drifts faster than the background, creating parallax | 18 |
| `dimming` | How much bokeh is dimmed | 0.22. Lower = stronger depth-of-field contrast |

---

## Multiple displays: one sky

All displays are merged into a single world (the union rectangle of their actual arrangement in System Settings → Displays, including vertical offsets).
Particles, links, pulses, and attention foci all live in that one world, and lines crossing between displays are continuous.
There's only one simulation (stepped once per frame); each display simply views the same world from its own position,
so CPU doesn't multiply with the number of displays. Particles are drawn as instanced quads, and a large highlight straddling a seam is drawn half on each side instead of popping out of existence.

- `link.targetCount` also scales with area, so each display gets the same thinking density as a single-display setup
- Each display gets its own background gradient (lit from its own center)
- When the display arrangement changes, the world is rebuilt and existing particles are mapped proportionally into it — no full reset
- Occlusion pausing is still per display: the simulation runs as long as any display is visible, and stops entirely only when all of them are covered

## Measurements (Mac mini M4 / 1920×1080 / 140 particles / 30 fps)

| Scenario | CPU (one core) | CPU (whole machine, 10 cores) | Memory |
|---|---|---|---|
| Desktop visible | 4.54% | 0.45% | 81.5MB |
| **Covered by windows** | **0.07%** | **0.007%** | **47.4MB** |
| Baseline: drawing nothing | 4.44% | 0.44% | 81.3MB |

**The entire visual effect costs only 0.10% more CPU than drawing nothing at all.** Almost all of the cost is MTKView's fixed framework overhead of waking up every frame, not the drawing itself — so raising the particle count, adding links, or turning on the background gradient won't make it noticeably more expensive.

---

## MCP

`mcp/wall42_mcp.py` is registered as a user-scope MCP server (`claude mcp add --scope user wall42`).
The control interface is the config file itself — wall42 checks its mtime every second, so the MCP server only needs to write the file. No IPC required.

| Tool | Purpose |
|---|---|
| `wall42_status` | Running state, whether it's occluded, fps, CPU, memory, link count, activity |
| `wall42_list_presets` | List styles |
| `wall42_set_preset` | Switch style (keeps the current activity settings) |
| `wall42_set_activity` | **Busyness control**: `manual` + level to crank it up by hand, `system` to hand it back to system load, `off` to disable |
| `wall42_set` | Change a single setting, e.g. `motion.link.distance` |
| `wall42_think` | **Before a long task**, raise the thinking density; reverts automatically when it expires |
| `wall42_insight` | **The moment something clicks**, fire a flare pulse that fades naturally over 1.3 seconds |
| `wall42_sync_wallpaper` | Sync the current frame to the system wallpaper (re-run after changing styles) |
| `wall42_sessions` | Claude session lights: no count = auto-read ~/.claude/sessions and return the detected list; with count/busy = feed numbers from outside; enable=True turns the lights on in the current config |
| `wall42_control` | start / stop / restart |

### Hooking it up to an AI agent

```
Before a long task   wall42_think(seconds=300, level=0.9)   # reverts on expiry, nothing to remember to turn off
When it clicks       wall42_insight()                        # one flash, fades over 1.3 s
```

`think` **expiring on its own** is deliberate: an AI agent may forget to turn it off, leaving the screen stuck at full speed.
To end it early, call `wall42_set_activity(mode="system")`.

Events use **file signals** (`~/.config/wall42/.signal`, written atomically), which wall42 checks once a second —
no sockets or IPC of any kind. The signal path is pinned to `~/.config/wall42` and does not follow `WALL42_CONFIG`;
otherwise, pointing it at a different config file for testing would mean it never receives commands.

### How it divides work with cool42

Both read system load, but neither depends on the other: **cool42 handles temperature and fans** (whether it's safe to start heavy work),
while **wall42 handles "letting people see the machine is busy."** Before an AI starts a long computation, call
`wall42_set_activity(mode="manual", level=1.0)` and pulses speed up along the links while the lines brighten;
when it's done, set it back to `mode="system"`.

## Wallpaper sync

wall42 **does not change your system wallpaper setting** — it's a window that sits above the wallpaper layer and below the desktop icons.
The upside is that the moment you run `./uninstall.sh`, your original wallpaper is back, with nothing to restore.
The trade-off is two gaps:

1. System Settings shows your original wallpaper, which doesn't match what you actually see
2. In the window between boot and launchd starting wall42, the old wallpaper shows through

```bash
./sync-wallpaper.sh      # capture the current frame and set it as the system wallpaper, closing both gaps
```

After switching styles or tweaking colors, run it again to retake the shot. Your original wallpaper path is saved to
`backup/original-wallpaper.txt`, and `./uninstall.sh` restores it automatically.

Under the hood it's a **file signal**: `touch ~/.config/wall42/.sync-request`, and the running instance picks it up within a second and
captures the current frame. So what you get is exactly the frame you're looking at, not a fresh render.
Output alternates between `wallpaper_a.png` / `wallpaper_b.png` — macOS caches the wallpaper for a given path and won't redraw it otherwise.

## Reinstall after changing code

What runs in the background is `~/.local/bin/wall42`, not the copy in the project directory. **Running `./build.sh` alone does not affect the running instance** —
after changing code you need `./install.sh` for it to take effect (it stops the old one, swaps the binary, and starts it again).

This once caused new settings to be ignored by an old binary: `onlyNodes` had no effect, all 460 stars linked to each other, and it ended up drawing over 5,000 lines.

## Built-in styles

`./preset.sh <name>` automatically backs up your config before switching; `./preset.sh --restore` restores it.

| Name | Description |
|---|---|
| `neon` | Cyan / hot pink, cyberpunk neon |
| `deepsea` | Teal to blue, slow, dense structure |
| `amber` | Warm amber, easy on the eyes at night |
| `starfield` | No links, 400 small dots |
| `starfield2` | Enhanced starfield: power-law sizes, independent twinkling, blue-white to warm white |
| `starfield-constellation` | Starfield + constellation lines, bright stars only, about 96 links |
| `starfield-web` | Starfield + full mesh, about 1,000 links, denser |
| `neural` | AI computation: dense links + flowing pulses that follow system load |
| `compute` | Distributed compute: no neon, monochrome lines, links constantly forming and dissolving |
| `thinking` | **AI thinking**: focus → radiate queries → flow back → "aha" flare |
| `minimal` | Pure black, no links, a few large points of light |
| `snow` | Snowfall: soft-edged flakes + big close-up flakes, vertical night-sky gradient, continuous across two displays |
| `sand` | Sand: a thin stream of sand per display piling into dunes, like an hourglass |
| `sessions` | The `kang` style + one ringed light per Claude session |

## Known limitations

- The wallpaper layer is an unofficial technique (Apple provides no public API for it), so future macOS versions may change how this layer behaves. Similar apps such as Plash and Backdrop work the same way.
- Display sleep, system sleep, screen lock, the screen saver, and fast user switching all stop drawing and release the drawable (the log shows `SUSPENDED(reason)`). On wake it resumes from the current time, so particles don't teleport.
- A display showing a full-screen app stops drawing automatically (via occlusion detection) while the other one keeps going. Spaces / Stage Manager test notes are in `docs/bench-20260926.md`.
- The 81MB of memory is almost entirely fixed AppKit + Metal framework overhead; the program itself only uses 0.2–0.4MB.
- If it's ready before WindowServer during login at boot, MTKView's display link can bind to a stale display and never draw (the log stays at `fps=0.0 steps=0`). A watchdog that detects "should be drawing but 0 frames for 5 seconds straight" rebuilds the view automatically (the log shows `⚠ 該畫卻連續 5 秒 0 幀`), up to 3 attempts in a row.
- Idle slowdown: after 10 minutes with no keyboard or mouse input → 5 fps, after 30 minutes → 1 fps (slow motion, not a black screen); any input restores full speed within 1 second.
  When another app keeps the display from sleeping (say, Chrome playing a video), this is the only mechanism that lets it save power. Tune it with `motion.idle`
  (`enabled` / `slowAfter` / `slowFps` / `deepAfter` / `deepFps`, in seconds and fps; unset = defaults).
- Non-focused display slowdown: only the display where the mouse moved within the last 10 seconds, or where the frontmost app's window is, runs at full speed.
- The `cpu=` value in the log fluctuates with clock speed (when the system is quiet it runs at a lower clock, so the same work shows 3–5× the CPU% it would when busy). To compare cost, look at `inst=` (millions of instructions per second) instead.
  Running two copies of wall42 at once for measurement doesn't work: the one launched later covers the earlier one, and the covered one stops drawing.
- The log (`~/Library/Logs/wall42.log`) gets one stats line every 60 seconds while running in the background, with events and anomalies written as they happen; it's cleared automatically once it exceeds 20MB.
- Any new config field must be declared Optional; otherwise an older config file missing that key makes Codable fail to decode the whole file, and the user's settings get thrown back to defaults.

## Diagnostic switches (environment variables)

| Variable | Purpose |
|---|---|
| `WALL42_FORCE_DRAW=1` | Ignore occlusion and always draw, for measuring peak cost |
| `WALL42_NO_DRAW=1` | Only clear, issue no draw calls, for measuring the framework baseline |
| `WALL42_SNAPSHOT=path` | Save a PNG of frame 90 |
| `WALL42_DURATION=seconds` | Exit automatically after this many seconds (also turns on per-second stats; the bench scripts rely on this) |
| `WALL42_VERBOSE=1` | Write a stats line every second even in the background (default is one every 60 seconds) |
| `WALL42_SIMULATE_IDLE=1` | Treat "seconds since launch" as idle time, for testing the idle slowdown |
| `WALL42_SIMULATE_STALL=1` | Don't attach the renderer to the first batch of views, reproducing the 0-frame-at-boot bug to verify the watchdog rebuilds automatically |
| `WALL42_PARTICLES` / `WALL42_FPS` | Override the config file, for testing |
| `WALL42_ONLY_MAIN=1` | Only use the main display, for a single-display baseline |
| `WALL42_REPO=path` | Repo location (presets / README / backup). `./install.sh` writes it into the LaunchAgent; defaults to `~/github-repos/wall42` |

Test signals (written to `~/.config/wall42/.signal`):
`{"kind":"snapshot","dir":"/path","tag":"x"}` saves one image of the current frame per display (without changing the system wallpaper);
`{"kind":"debug-suspend","reason":"locked","on":true}` takes the same stop-drawing path as a screen lock.

`./bench_cpu.sh [binary] [seconds] [label]` pauses the background instance, runs the given binary to measure its own CPU, then brings the background instance back.
For the full `sudo` accounting (including WindowServer compositing cost): `./bench_powermetrics.sh` — usage is in the file header.
Measurement notes: `docs/bench-20260926.md`.

## License

[MIT](LICENSE) © 2026 Okle42
