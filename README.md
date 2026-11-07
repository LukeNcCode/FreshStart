# FreshStart GitHub Pages

This branch contains the product website for FreshStart v0.2.7.

The page is organised as an editorial product tour: a full-screen introduction,
five switchable product surfaces, a dark execution-model section, environment
handoff, download, and FAQs. The former browser-based app simulator has been
removed; all product imagery is static and does not read system state.

The scroll treatment follows the reference site's structure without importing
its paid plugins: inertial wheel movement, scroll-linked hero parallax, a pinned
product sequence that advances through all five surfaces, drifting feature
cards, and image parallax. Mobile and reduced-motion visitors use native scroll.

Background music uses `site-assets/bgm.mp3`. As soon as the loading rail reaches
100% and the `F` begins to form, the track starts at a low volume with a soft
fade-in. A header control remembers the visitor's sound preference; when browser
autoplay is blocked, playback starts on the first user gesture instead.

The first load mirrors the reference site's measured reveal: a minimal loading
rail collapses into a complete `F`. The white fill inside the letter scales and
rotates while fading, revealing the real page through the expanding `F`.

## Local preview

```powershell
python -m http.server 8080
```

Open `http://localhost:8080/`.

## GitHub Pages

Use the `gh-pages` branch and repository root as the GitHub Pages publishing source.
