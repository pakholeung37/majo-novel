# Sprite animation demo assets

This fixture uses publicly served preset assets from [objection.lol](https://objection.lol/). It is scoped to testing a future Sprite animation Actor, rather than mirroring the entire site.

Run from the repository root:

```sh
python3 -m pip install Pillow
python3 tools/fetch_objection_sprite_assets.py
```

The downloader selects five characters and fifteen poses: Phoenix Wright (stand, desk slam, point), Miles Edgeworth (stand, desk slam, confident shrug), the Judge (stand, negative, positive), Maya Fey's witness variant (stand, think, happy), and Dick Gumshoe (stand, confident, headscratch). It also fetches their courtroom backgrounds, foreground desks, speech blips, timed action sounds, Phoenix and Miles's "Objection!" voices, the shared "Objection!" effect and image, four 2001-era courtroom BGM tracks, and Maya and Dick's themes.

`asset_manifest.json` maps each pose to idle, speaking, and optional intro animations. Every animation retains its original GIF and exports composited transparent PNG frames with GIF timing in milliseconds. Frames use the original canvas and top-left origin; do not trim them independently or the actor will jump during playback. Loop idle and speaking animations; play intro states in order before switching to idle or speaking. `next_pose_delay_ms` and `audio_ticks.time_ms` come from the site's preset metadata.

The layout follows the other demos: `assets/characters/{Phoenix,Miles,Judge,Maya,Dick}/` holds each character's animations, `assets/backgrounds/` holds the courtroom images and desks, `assets/audio/{bgm,sfx,voice}/` holds music and sounds, and `assets/ui/` holds the Objection image. Each animation has `source.gif` and a `frames/` PNG sequence. The manifest points to these paths.

Downloaded images and audio are ignored by Git. The manifest and downloader are tracked so the fixture can be regenerated. These third-party assets are for local prototyping; check the rights for any redistribution or published demo.
