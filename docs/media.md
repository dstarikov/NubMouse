# README images and demo GIF

Everything in [`images/`](images) is generated, so it can be refreshed whenever the UI changes.

| File | Made by | Used in |
| --- | --- | --- |
| `logo.png`, `logo-paused.png` | `tools\screenshots.ps1` | README header |
| `install-dialog.png` | `tools\screenshots.ps1` | README → Install |
| `tray-menu.png` | `tools\screenshots.ps1` | README → The tray icon |
| `demo.gif` | `tools\record-demo.ps1`, recorded by hand | README, right under the intro |

## Before capturing

1. `git pull`, then exit any running NubMouse (tray menu → **Exit**).
2. Pick the look the images should have: light or dark Windows theme, accent colour, and
   display scaling (100% or 150% gives the crispest text).
3. For the GIF: make the mouse pointer bigger so it reads at README size (*Settings →
   Accessibility → Mouse pointer and touch → Size* 2–3), close anything private, and use a
   browser profile without personal bookmarks or tabs.

## Screenshots

```powershell
.\tools\screenshots.ps1
```

It builds the current sources, shows the install dialog and the tray menu over a plain backdrop
for a few seconds (don't touch the mouse), and writes the PNGs. The version shown is the latest
git tag; pass `-Version 0.2.0` to show another. Nothing is installed or saved.

## Demo GIF

The scrolling has to be done with a real TrackPoint: NubMouse ignores synthetic input, so
the demo can't be scripted. `tools\record-demo.ps1` does the rest.

**Setup.** Run the build you want to show (`.\dist\NubMouse.exe --portable`). Open a
browser window of about 1000×650 (not maximized: a smaller window keeps text readable once the
GIF is scaled to 800 px wide) on a page that is long *and* wider than the window, for example a
Wikipedia article with a wide table, or a GitHub file view with long lines. Keep a link visible
near the top for the click.

**Shot list (about 12 s).** Rehearse it once, then:

1. Rest the pointer on the page. Hold the middle button and push the TrackPoint **down**: the page
   scrolls smoothly, the pointer stays exactly where it is. Push **up** a little.
2. Still holding, push **right** over the wide table or code: it scrolls sideways.
3. Release. Move to a link and **middle-click** it: it opens in a new background tab (the tab
   strip should be in the frame).
4. Optional: middle-click an empty part of the page: no autoscroll cursor sticks around.
5. Stop moving for a second at the end so the loop has a clear break.

**Record.** Start the script, click the browser within the countdown, perform the shots:

```powershell
.\tools\record-demo.ps1 -Seconds 12
```

It records the window in front (with `-Region x,y,w,h` you can record part of it instead), then
writes `docs\images\demo.gif`. Aim for under 5 MB; `-Width`, `-Fps` and `-Seconds` trade size
for quality. Add `-KeepVideo` to keep the MP4, which can also be dragged into a GitHub release
or issue.

Retake until the pointer visibly doesn't move while scrolling: that's the point of the demo.

## README markup

Once the images exist, these go into the README:

```markdown
<p align="center"><img src="docs/images/logo.png" width="96" alt=""></p>

<p align="center"><img src="docs/images/demo.gif" width="800"
  alt="Holding the middle button and pushing the TrackPoint scrolls a web page down and sideways while the pointer stays still; a middle click then opens a link in a new tab"></p>
```

- Install, after step 2: `![The install dialog: Start with Windows, Install for all users, update options, and Install / Just run it / Cancel](docs/images/install-dialog.png)`
- The tray icon, after the bullet list: `![The tray menu](docs/images/tray-menu.png)`
