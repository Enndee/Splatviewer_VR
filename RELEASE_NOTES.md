# Splatviewer_VR Release Notes

## Version 1.4

Adds runtime `.spx` support, preload caching, movie playback, lossless import naming cleanup, and runtime loading performance improvements.

### Added

- Runtime loading for `.spx` Gaussian splat files.
- Browser-controlled preload caching with a RAM-budget limit.
- Folder-based movie mode with progressive loading and adjustable playback FPS.
- Windows file association helpers included directly in the release package.

### Changed

- The editor import preset previously called `VeryHigh` is now `Lossless`.
- Release file association helpers now register `.spx` in addition to `.ply`, `.spz`, and `.sog`.
- The release package now ships as `releases/Splatviewer_VR_v1.4_Windows_x64.zip`.

### Fixed

- Movie mode now loads files from the folder currently shown in the VR browser.
- Runtime movie loading no longer competes with preload caching for I/O.
- Runtime packing and PLY parsing allocate substantially less temporary memory during loading.

## Version 1.2

Adds runtime PlayCanvas `.sog` loading support, desktop browser access from `Esc`, and follow-up fixes for camera reset and SOG decoding.

### Added

- Runtime loading for bundled PlayCanvas `.sog` files using an embedded WebP decoder.
- Desktop access to the in-scene file browser with `Esc`, plus keyboard browsing with arrow keys, `Enter`, and `Backspace`.

### Changed

- Desktop camera reset now correctly restores the initial look direction when loading a new splat.
- Desktop file browser input now cleanly blocks movement and file cycling shortcuts while open.

### Fixed

- Optional higher-order spherical harmonics handling for `.sog` files that only contain `sh0` data.
- `.sog` scale decoding now converts stored log-scale values back to linear scale before rendering.
- SOG parser metadata section inheritance compile errors.

## Version 1.0

Initial public release of the VR-focused Gaussian splat viewer.

### Included

- Windows standalone build.
- OpenXR VR support.
- Runtime loading for `.ply`, `.spz`, and bundled PlayCanvas `.sog` splat files.
- Command-line opening of `.ply`, `.spz`, and `.sog` files.
- VR locomotion with smooth movement and snap turn.
- Controller-based splat switching.
- Desktop mode with mouse and keyboard controls when no headset is active.
- Fullscreen windowed startup.

### Controls

#### VR

- Left stick: move.
- Right stick X: snap turn.
- Right stick Y: move up and down.
- Right controller `B`: next splat.
- Right controller `A`: previous splat.

#### Desktop fallback

- `W A S D`: move.
- Mouse: look.
- `SPACE / C`: move down / up.
- `R / F`: next / previous splat.
- `Q / E`: rotate the current splat.
- `Home`: reset splat rotation.
- `End`: flip upside down.

### Notes

- Bring your own Gaussian splat files.
- A D3D12-capable Windows system and an OpenXR runtime are recommended.
- The repository contains source and project files; the downloadable release package contains the built player.
- Windows file associations can launch the viewer directly with `.ply`, `.spz`, or `.sog` files when the executable is registered as the open command.