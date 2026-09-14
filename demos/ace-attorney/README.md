# Ace Attorney sprite sheet demo

This fixture uses publicly served preset assets from [objection.lol](https://objection.lol/). It tests the sprite sheet character actor rather than mirroring the entire site.

Run from the repository root:

```sh
python3 -m pip install Pillow
python3 tools/fetch_objection_sprite_assets.py
python3 tools/build_ace_attorney_sheets.py
```

Open `res://demos/ace-attorney/AceAttorneyDemo.tscn` in Godot to play the short courtroom story “逆转的最后一声”. Its logical viewport and character positions use the 960×640 defense background's native size. The script uses five stages so Phoenix, Miles, Judge, Maya, and Dick retain their own Defense, Prosecution, Judge, Counsel, and Witness backdrops. Defense, Prosecution, Judge, and Witness have full-canvas desk PNGs placed as foreground object actors; the downloaded Counsel set has no desk. The dedicated presentation scene draws a translucent courtroom dialogue panel and angular blue name plate. A short `audio/sfx/Blip.wav` plays as each dialogue glyph is revealed. The headless smoke mode runs all 90 dialogue/narration lines and checks completion, the five stages, and blip playback:

This demo sets character appearance changes to `cut` by default, so pose changes in the Yarn script omit `via:cut`. Initial character entrances still specify `via:cut` explicitly.

```sh
godot --headless --path . res://demos/ace-attorney/AceAttorneyDemo.tscn -- --ace-attorney-auto
```

`source/characters/{Phoenix,Miles,Judge,Maya,Dick}/animations/` contains the downloaded GIFs and composited PNG frames. This ignored source directory has a `.gdignore` marker so Godot imports only runtime atlases. The build script writes `assets/characters/{name}/sprite_character.json` and `poses/{pose}/atlas_XX.png`. Each character manifest maps idle, speak, and optional intro clips to atlas pages, cropped frame rectangles, original canvas offsets, and GIF frame durations. The runtime loads a pose's pages when it is shown; dialogue starts the temporary speaking state and the text-display-complete event returns the actor to idle. Advancing or stopping the line also clears speaking as a fallback.

Characters are not registered individually in the demo configuration. The sprite sheet plugin resolves `<<character ID>>` from `assets/characters/ID/sprite_character.json`, while `<<backdrop Judge>>` independently resolves `assets/backgrounds/Judge.jpg`.

The downloader selects five characters and fifteen poses: Phoenix Wright (stand, desk slam, point), Miles Edgeworth (stand, desk slam, confident shrug), the Judge (stand, negative, positive), Maya Fey's counsel variant (stand, angry, thinking), and Dick Gumshoe (stand, confident, headscratch). It also fetches their courtroom backgrounds, available foreground desks, speech blips, timed action sounds, Phoenix and Miles's "Objection!" voices, the shared "Objection!" effect and image, four 2001-era courtroom BGM tracks, and Maya and Dick's themes.

`asset_manifest.json` maps each pose to idle, speaking, and optional intro animations. Every animation retains its original GIF and exports composited transparent PNG frames with GIF timing in milliseconds. The packer trims transparent borders while recording each frame's offset within the original canvas. Idle and speaking animations loop; intro animations play once before switching to idle or speaking. `next_pose_delay_ms` and `audio_ticks.time_ms` come from the site's preset metadata. Audio ticks and animated appearance transitions are not yet played by this demo actor.

The runtime asset layout follows the other demos: `assets/characters/{Phoenix,Miles,Judge,Maya,Dick}/` holds each character's manifest and pose atlases, `assets/backgrounds/` holds the courtroom images and desks, `assets/audio/{bgm,sfx,voice}/` holds music and sounds, and `assets/ui/` holds the Objection image plus the tracked dialogue SVGs. For this demo the object resource path points at `assets/`; desk objects use `backgrounds/…` and the objection overlay uses `ui/ObjectionOverlay`, without copying images. The top-level asset manifest points to the downloaded source files.

Downloaded images and audio are ignored by Git. The manifest and downloader are tracked so the fixture can be regenerated. These third-party assets are for local prototyping; check the rights for any redistribution or published demo.

## Short trial: 逆转的最后一声

The linear script stages two reversals around a recorded gunshot, a timestamped photograph, and Gumshoe’s continuous supervision of the defendant. All five characters and all fifteen available appearances participate. Three voiced Objection overlays punctuate the argument (Phoenix twice, Miles once); three desk slams use the source action timing (175 ms for Phoenix, 250 ms for Miles). Actor/overlay shake preserves backdrop coverage. Five BGM tracks follow the argument: Trial2001, Allegro2001, Objection2001, Pursuit2001, and Maya, with deliberate silence before revelations and the verdict. No additional downloaded assets are needed.

The existing actor does not automatically play metadata audio ticks; these three sounds are explicitly timed in Yarn. The case clears the defendant and leaves identification of the real shooter to a renewed investigation.

The atlas builder also converts the original green-key `ui/Objection.png` into `ui/ObjectionOverlay.png` with alpha transparency, preserving the original download and its manifest hash.
