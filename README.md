# Splatviewer_VR

`Splatviewer_VR` is a VR-focused fork of the Unity Gaussian Splatting viewer. The repository keeps the reusable Unity package, a VR sample project, and packaged Windows builds for release workflows.

This fork is based on [aras-p/UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting) and adds a standalone VR viewer with runtime loading for Gaussian splat files.

https://github.com/user-attachments/assets/4665c9fa-1f44-4b2c-a258-9eb19ebe854e

## What This Fork Adds

- A dedicated Unity project at `projects/Splatviewer_VR`.
- Runtime loading for splat files instead of requiring prebuilt Unity assets.
- VR locomotion, in-scene splat manipulation, and desktop fallback controls.
- File cycling and a world-space browser with favorites, preload control, and movie playback.
- Windows release builds under `projects/Splatviewer_VR/Release/`.

## Repository Layout

- `package/`: reusable Unity Gaussian Splatting package.
- `projects/Splatviewer_VR/`: Unity project for the VR viewer.
- `docs/`: upstream documentation for package integration and splat editing.

## Viewer Features

- Runtime loading of `.ply`, `.spz`, `.spx`, and bundled PlayCanvas `.sog` splat files.
- Command-line file opening for `.ply`, `.spz`, `.spx`, and `.sog` shell associations.
- OpenXR-based VR support.
- Smooth locomotion with continuous turning and vertical fly movement.
- Runtime splat rotation, reset, flip, and uniform scaling in both VR and desktop modes.
- Controller and keyboard shortcuts for moving between splat files.
- In-world file browser with favorites, current-file tracking, and direct folder browsing.
- Browser preload caching with a configurable RAM budget.
- Folder movie mode with progressive loading and adjustable playback FPS.
- Full desktop mode when running without a headset.
- Fullscreen windowed startup for desktop and VR mirror view.
- IMPORTANT: put your Quest splat files here: `Quest 3\internal memory\Android\data\com.enndee.splatviewervr.android\files\Splats`

## Controls

- Left stick: move.
- Right stick X: continuous turn.
- Right stick Y: move up and down.
- Right controller `B` / `secondaryButton`: next splat.
- Right controller `A` / `primaryButton`: previous splat.

#### VR file browser

- Left controller `Y`: open / close browser.
- Left or right stick: browse entries and switch pane.
- Left or right trigger, or right controller `A`: open folder / load file.
- Right controller `B`: go to parent folder.
- Left stick click: add / remove favorite.
- Left controller `X`: toggle preload caching.
- Right stick click: start movie mode from the current folder.
- During movie playback, left stick left / right: decrease / increase FPS.
- During movie playback, left controller `Y`: stop movie playback.

#### VR splat controls

- Hold left grip + use right stick: rotate splat.
- Hold both grips + move controllers apart / together: scale splat.
- Hold left grip + left controller `X`: flip splat.
- Hold left grip + right controller `A`: reset splat rotation.

### Desktop fallback

- `W A S D`: move.
- Mouse: look around.
- `SPACE / C`: move up / down.
- `Shift`: sprint.
- `R / F`: next / previous splat.
- `Q / E`: rotate the current splat.
- Mouse wheel: scale the current splat.
- `Home`: reset splat rotation.
- `End`: flip the current splat.
- Hold left or right mouse button and move mouse: drag camera.
- `Esc / Tab`: open / close file browser.
- `Enter`: open folder / load file in browser.
- `Backspace`: go to parent folder in browser.
- `F`: add / remove favorite in browser.
- `P`: toggle preload caching in browser.
- `M`: start / stop movie mode.
- `Left / Right`: decrease / increase movie FPS during playback.
- `Esc`: release mouse cursor.
- Left click: capture mouse cursor again.

## Unity Project

Open `projects/Splatviewer_VR` in Unity. The project includes `Assets/GSTestScene.unity`, and `Assets/Editor/BuildSetup.cs` ensures that scene is present in the build settings.

Recommended environment:

- Unity 6 (`6000.0.69f1`).
- Windows.
- D3D12-capable GPU.
- OpenXR-compatible headset runtime.

## Runtime Splat Loading

This fork includes runtime loading changes for the Gaussian splat package and VR-side runtime scripts.

Highlights:

- `GaussianSplatAsset` supports runtime-provided byte buffers.
- `GaussianSplatRenderer` uses those runtime buffers when present.
- `RuntimeSplatLoader` reads binary little-endian PLY data at runtime.
- Splats are reordered and uploaded in a GPU-friendly layout.

The source tree does not include sample splat data. Add your own `.ply`, `.spz`, `.spx`, or bundled `.sog` files and point the viewer to the folder you want to browse.

If the viewer is launched with a `.ply`, `.spz`, `.spx`, or `.sog` file path on the command line, it will automatically load that file on startup. This is the basis for Windows Explorer file associations.

PlayCanvas `.sog` support targets bundled `.sog` archives with WebP-backed property images as described in the PlayCanvas format specification.

## Windows File Association

You can register `.ply`, `.spz`, `.spx`, and `.sog` to open with the viewer by running either the root-level release copy or the `tools/` copy:

- `Register-SplatviewerFileAssociations.bat`
- `Register-SplatviewerFileAssociations.ps1`
- `tools/Register-SplatviewerFileAssociations.bat`
- `tools/Register-SplatviewerFileAssociations.ps1`

The helper writes per-user file associations under `HKCU\Software\Classes`, so administrator rights are not required.

If needed, pass a custom executable path to the PowerShell script:

- `Register-SplatviewerFileAssociations.ps1 -ExecutablePath "C:\Path\To\SplatViewer_VR.exe"`
- `Register-SplatviewerFileAssociations.ps1 -Unregister`

The 1.4 Windows release package includes the `.bat` and `.ps1` helpers next to `SplatViewer_VR.exe`.

## Building A Release

The packaged Windows player is built from `projects/Splatviewer_VR/Builds/`.

For GitHub releases, publish a zip package generated from that build output. Release archives are written under `projects/Splatviewer_VR/Release/`. The repository ignores `projects/**/Release/` so source control stays focused on source, project config, tracked build artifacts, and documentation.

## Release Files

- Release notes: `RELEASE_NOTES.md`
- Latest GitHub release: `1.6.1`
- Release package output pattern: `projects/Splatviewer_VR/Release/Splatviewer_VR_v<version>_Windows_x64.zip`

## Upstream Credits

This work builds on the original Unity Gaussian Splatting implementation and the original 3D Gaussian Splatting research.

- Upstream package: [aras-p/UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting)
- Paper: [3D Gaussian Splatting for Real-Time Radiance Field Rendering](https://repo-sam.inria.fr/fungraph/3d-gaussian-splatting/)

## License

The repository retains the upstream MIT-licensed Unity integration code. Review the original Gaussian Splatting training software license separately if your splat assets were produced with tooling that has additional restrictions.
