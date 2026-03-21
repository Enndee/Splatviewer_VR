# Splatviewer_VR

`Splatviewer_VR` is a VR-focused fork of the Unity Gaussian Splatting viewer. The repository keeps the reusable Unity package, a VR sample project, and packaged Windows builds for release workflows.

This fork is based on [aras-p/UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting) and adds a standalone VR viewer with runtime loading for Gaussian splat files.

https://github.com/user-attachments/assets/1d65e92e-f472-4bf0-a36a-4ca76a7d70a8

## What This Fork Adds

- A dedicated Unity project at `projects/Splatviewer_VR`.
- Runtime loading for splat files instead of requiring prebuilt Unity assets.
- VR locomotion and controller-driven navigation.
- File cycling and browser support for browsing splat files in-headset.
- Windows release builds under `projects/Splatviewer_VR/Release/`.

## Repository Layout

- `package/`: reusable Unity Gaussian Splatting package.
- `projects/Splatviewer_VR/`: Unity project for the VR viewer.
- `docs/`: upstream documentation for package integration and splat editing.

## Viewer Features

- Runtime loading of `.ply`, `.spz`, `.spx`, and bundled PlayCanvas `.sog` splat files.
- Command-line file opening for `.ply`, `.spz`, `.spx`, and `.sog` shell associations.
- OpenXR-based VR support.
- Smooth locomotion and snap turn.
- Controller buttons for moving between splat files.
- Browser preload caching with a configurable RAM budget.
- Folder movie mode with adjustable playback FPS.
- Full desktop mode when running without a headset.
- Fullscreen windowed startup for desktop and VR mirror view.

## Controls

### VR

- Left stick: move.
- Right stick X: snap turn.
- Right stick Y: move up and down.
- Right controller `B` / `secondaryButton`: next splat.
- Right controller `A` / `primaryButton`: previous splat.

### Desktop fallback

- `W A S D`: move.
- Mouse: look around.
- `SPACE / C`: move up / down.
- `R / F`: next / previous splat.
- `Q / E`: rotate the current splat.
- `Home`: reset splat rotation.
- `End`: flip the current splat.
- `Esc`: release mouse cursor.
- Left click: capture mouse cursor again.

## Unity Project

Open `projects/Splatviewer_VR` in Unity. The project includes `Assets/GSTestScene.unity`, and `Assets/Editor/BuildSetup.cs` ensures that scene is present in the build settings.

Recommended environment:

- Unity 2022.3 LTS.
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

The packaged Windows player for the current release is built into `projects/Splatviewer_VR/Release/1.4`.

For GitHub releases, use the zip package generated from that folder instead of committing the raw build output. The repository ignores `projects/**/Release/` so source control stays focused on source, project config, and documentation.

## Release Files

- Release notes: `RELEASE_NOTES.md`
- Release package output: `releases/Splatviewer_VR_v1.4_Windows_x64.zip`

## Upstream Credits

This work builds on the original Unity Gaussian Splatting implementation and the original 3D Gaussian Splatting research.

- Upstream package: [aras-p/UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting)
- Paper: [3D Gaussian Splatting for Real-Time Radiance Field Rendering](https://repo-sam.inria.fr/fungraph/3d-gaussian-splatting/)

## License

The repository retains the upstream MIT-licensed Unity integration code. Review the original Gaussian Splatting training software license separately if your splat assets were produced with tooling that has additional restrictions.
