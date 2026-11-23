# DesktopSnow — real snow on your Windows desktop

Real snow on the desktop: snowflakes fall **behind desktop icons**, mouse clicks go straight
through the snow, and windows and games are not disturbed. On first run the app adds itself
to **autostart** — after a reboot the snow comes back automatically.

Everything is one pure C# WPF file with **zero dependencies** — no installer, no runtime download,
nothing to install. All menus, settings, balloon tips and messages are in English.

## Quick start (30 seconds, nothing to install)

1. Download the project folder (green **Code** button → **Download ZIP**), unpack it.
2. Double-click **`build.bat`** — it compiles `DesktopSnow.exe` and immediately starts it
   (the C# compiler is built into Windows, there is nothing to install).
   If it "does nothing", delete old `DesktopSnow.exe` / `Snow.exe` from the folder and run it again.
3. Done. It's snowing, and a snowflake tray icon appears near the clock.

Tip: always build from the **newest** unpacked ZIP. The tray tooltip shows the version
(`DesktopSnow vX.Y.Z - effects on your desktop`) — use it to verify you run a fresh build.

## Tray controls (right-click the snowflake icon near the clock)

- **Effects over all windows** — screensaver mode: effects are drawn above every window.
  Default is the opposite: **desktop-only mode**, where snow sits at the bottom of the window
  stack — visible on the desktop and behind any open windows and the taskbar.
- **Hide effects behind icons (experimental)** — attaches the effects to the wallpaper layer
  behind the desktop icons. If Windows refuses the wallpaper trick, the app keeps desktop-only
  mode and tells you in a balloon tip.
- **Summon Santa** — call the sleigh immediately, no waiting for the random visit.
- **Fireworks!** — three rockets launch from the snow and burst into colorful stars.
- **More effects / Fewer effects** — adjust the effect density (20–620).
- **Clear all effects** — instantly clears drifts, window caps, stuck drops, snowballs and
  snowmen. Same via the **Ctrl+Alt+S** hotkey from anywhere, even when the tray is not reachable
  (e.g. when a snowball is stuck to the cursor).
- **Settings...** — full dark settings window (see below).
- **Diagnostics (open log)** — opens `DesktopSnow.log` next to the exe.
- **Start with Windows (autostart)** — on/off,
  key `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\DesktopSnow`.
- **Exit**.

## Settings window

Tray icon → **Settings...** — a dark window with purple buttons, draggable by the header
(closes with the × button or Esc). Sections:

- **SEASON** — four season buttons: **Snow / Petals & Butterflies / Rain / Dry Leaves**
  (names describe what you actually see, not calendar names; the active season is highlighted).
  The **ACTIONS** and **CHARACTERS & EFFECTS** sections switch together with the season, so you
  always see the settings for the season you selected.
- **Season: Snow** — actions: Summon Santa, Summon Grinch, Summon Rabbit, Fireworks!,
  Clear all effects. Toggles: Santa auto (random visits), Grinch, Rabbit,
  Fireworks & shooting stars (auto), Snowballs, Icicles, Snow on window tops, Sparkles,
  New Year countdown, Start with Windows (autostart).
- **Season: Petals & Butterflies** — actions: Fireworks!, Clear all effects.
  Toggles: Butterfly, Snow on window tops, Sparkles, New Year countdown, Start with Windows.
- **Season: Rain** — actions: Fireworks!, Clear all effects.
  Toggles: Bird, Sparkles, New Year countdown, Start with Windows (no window caps in rain).
- **Season: Dry Leaves** — actions: Fireworks!, Clear all effects.
  Toggles: Bird, Snow on window tops, Sparkles, New Year countdown, Start with Windows.
- **EFFECT DENSITY** — slider from 20 to 620 particles.

All settings are stored in the registry (`HKCU\Software\DesktopSnow`) and restored on start.
The settings window is excluded from the roof list — snow never piles up on its buttons.
Drifts and leaves falling off windows are tinted to match the active season and always happen
behind windows — nothing flies above your apps.

## What pile-up looks like

- Snow accumulates as a thin smooth layer at the bottom of the screen (soft cap ~15 px,
  hard limit 35 px). Below 20 px the bottom of the layer slowly, invisibly melts away, so the
  drift stays neat instead of growing into a white strip.
- The layer top is drawn as **smooth rounded arcs**, never triangles.
- Non-maximized windows get **snow caps on their title bars** (up to 15 px). An empty roof
  shows no line; caps are rounded and wavy, with **icicles** that shimmer and drip.
- Moving a window **dumps its snow/leaves** off as real particles — a whole soft sheet, nothing
  left behind. Caps on lower windows hide behind higher overlapping windows (so a roof on a
  window below your browser is not visible through it).
- Maximized windows don't collect snow; while any maximized window is on screen the caps
  freeze (the desktop is considered hidden). **Fullscreen apps auto-pause** all effects to keep
  games smooth and the GPU idle.
- Falls (gifts, trees, leaves) reaching a window land on it — and land **inside** the snow cap
  of that window, not on top of it.

## Winter evening

- A **moon** with a softly pulsing glow shines in the top-right corner of the desktop.
- Every ~40–95 seconds a **shooting star** with a golden sparkling tail streaks across the sky
  — cartoon-style, bright and shiny. Make a wish.
- About every 45–90 seconds a **fireworks** rocket rises from the drift and bursts into
  **multicolored** stars (red, yellow, blue, green, orange, pink, cyan) with a bright flash.
  Always available manually from the tray menu (a season-independent standing effect).

## Snow (winter) season

Snowfall with drifts, Santa & Grinch, icicles, moon, shooting stars, snowballs and a snowman.

## Santa

Every 30 seconds there is a 25% chance that the sleigh flies across the screen on a wavy
trajectory with a rolling lean — it drops gifts and small trees, which fall spinning at random
tilts, get stuck in the drift with a snow splash, and slowly sink into the snow.

- Click a gift — a confetti explosion.
- Dropped trees carry a twinkling colored garland.
- Santa waves his hand, the reindeer run through the air with moving legs and bobbing heads,
  golden sparks trail behind the sleigh.
- **Grab a tree** (hold LMB on any point of a fallen tree): snow particles fly off it.
- While holding a tree, swing the mouse to **spin it with inertia**; releasing it makes it fly
  away while still spinning.
- **Throw a tree into the flying sleigh** and Santa is knocked down: confetti, flying snow,
  his gifts scatter in the air and he rolls off-screen. A snowball throw can knock him down too.
- Sometimes the **Grinch** walks in from the screen edge brandishing gifts: stolen boxes stack
  into a visible column he carries (no teleporting). Click him — he flinches, falls flat with a
  snow splash, drops the stolen gift, slides away, and is slowly buried **behind** the drift.
- The tray menu has **Summon Santa** for an instant visit.

## Snowballs & snowman

- **Hold LMB on the drift** to mold a snowball. It **grows only while rolled over the drift**
  (slower than before), and the snow beneath it is **scraped away** with a comb-like clean path.
  Rolling over open air does nothing, and the taskbar never picks up snowballs.
- Release gently — the ball settles into the snow; the next ball can be rolled right onto it.
  Balls can also be stacked on unsupported tops — the upper ball checks what it lands on.
- Three balls stack into a **snowman**: carrot nose, coal buttons, stick arms and a red bucket
  on the head. Two snowmen can live on the desktop at the same time.
- Release the ball sharply — it flies like a projectile. A hit **kills the Grinch**
  (same cinematic death) and can **knock Santa off the sleigh**. A big ball slamming into the
  drift adds snow and splashes some around.

## Rabbit

Once the Grinch is fully buried, a **rabbit** hops in from the screen edge, excitedly digs
through the snow (with snow spray) and **digs out the stolen gift** — it bounces up and drops
back into the drift. Then the rabbit hops away.

## New Year countdown

- A **countdown plate "New Year in: Nd HH:MM:SS"** (330 px wide) sits in the bottom-right corner
  above the drift, updated every second from the system clock. Winter season only.
- On the night of January 1st (if the app is running at midnight **or** started before 06:00
  on January 1st) — a big fireworks show and a **"Happy New Year!"** banner in the center of
  the screen. The plate hides itself on January 1st.

## Petals & Butterflies (spring)

Pink **sakura petals** fall slowly instead of snow and pile into a **dense carpet of whole
landed petals** along the bottom and on window tops — the same landed-deco system the autumn
leaves use (same clipping behind windows, ~1200 cap, auto-expire). A **butterfly** with
flapping wings flutters above the desktop. Drift/leaf shedding off windows is tinted pink.

## Rain (summer)

**Rain**: fast translucent drops, a glassy wet layer along the bottom, splash bursts on impact.
No window caps (nothing should hide your work), and the drops always land behind windows.

## Dry Leaves (autumn)

Autumn leaves fall in a 5-tint warm palette, get caps and pile at the bottom into a **dense
carpet of whole landed leaves** (the same shapes/tints as the falling leaves — no mush or
"leaves puree"). A wind gust occasionally lifts a few old leaves back into the air, and the
**bird** visits. Old landed leaves **expire automatically** (each has a lifetime of 90–150 s),
and the carpet is capped at ~1200 leaves so performance stays flat. Leaves falling off windows
shed as whole leaves in the same palette.

## Performance

- Everything is dt-based physics at ~60 fps, with per-window clipping, cached and frozen
  geometry/brushes, and caps rebuilt only when the snow depth actually changes.
- Maximized/fullscreen apps trigger an **auto-pause**: only a light background refresh runs,
  the GPU stays free for games.
- Particle counts are capped (flakes, decos, sparkles), and there are no per-tick allocations
  in the hot paths. If you still see high GPU use, lower **EFFECT DENSITY** in settings.

## If the snow is not visible

The "behind icons" mode relies on the Progman/WorkerW wallpaper trick and can fail on some
Windows builds. In that case the app itself falls back to desktop-only mode, or switch modes
manually in the tray menu.

## If nothing happens

1. Look for **`DesktopSnow.log`** next to `DesktopSnow.exe`, open it in Notepad: it shows the
   step where the app stumbled. A start line `program start, version ...` means the build
   actually launched.
2. If a second instance is started, a message appears:
   "DesktopSnow is already running (check the tray icon near the clock)." — close the old one
   via tray → Exit first.
3. If there is no window and no log, the exe itself failed to start — check what `build.bat`
   printed, and if an error window flashes, send its text/screenshot.

## Files

- `Snow.cs` — all app code (WPF, single file, no dependencies). All user-facing strings are
  English; code comments and internal log lines stay minimal and ASCII.
- `build.bat` — builds and runs. Kills old `DesktopSnow.exe` / `Snow.exe` first, detects a
  preview-only ZIP, and uses the C# compiler already built into Windows.
