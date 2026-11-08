# FreshStart GitHub Pages

This branch contains the product website for FreshStart v0.2.7.

The page is organised as an editorial product tour: a full-screen introduction,
five switchable product surfaces, a dark execution-model section, environment
handoff, download, and FAQs. The former browser-based app simulator has been
removed; all product imagery is static and does not read system state.

The page uses a one-gesture, one-screen scroll model. A wheel gesture advances
to the next editorial page; inside the product chapter, successive gestures walk
through all five software surfaces before continuing to the next section.
Scroll-linked hero motion, drifting feature cards, and image parallax remain.
Mobile and reduced-motion visitors use gentle native scroll snapping.

Download actions open the dedicated `download.html` page, where the lightweight
and offline installers link directly to their GitHub Release assets without
requiring a detour through the repository page.

On the main site, the download page opens in a same-document overlay and updates
the address bar with History API, so background music continues uninterrupted.
Direct visits to `download.html` still render it as a standalone page.

MiSans is used only for Chinese glyphs through a small WOFF2 subset and a
`unicode-range`; Latin text continues to use the original typefaces.

Product screenshots are stored as 1600px WebP assets for faster loading while
retaining enough resolution for the large desktop presentation.

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
