# SOG Import Rendering Fix

## Problem

Imported `.sog` files could render with jagged edges and visibly stretched splats, while `.spz` and `.spx` files looked correct.

The issue was caused by incorrect quaternion reconstruction in the SOG runtime importer.

## Root Cause

The PlayCanvas SOG format stores compressed rotations in smallest-3 form using component order `(w, x, y, z)`. The SOG reader reconstructed the quaternion in that same order and passed it directly to `GaussianUtils.PackSmallest3Rotation`.

That was wrong for this codebase, because `PackSmallest3Rotation` expects quaternions in Unity order `(x, y, z, w)`.

As a result, SOG splat rotations were decoded incorrectly. The splat ellipsoids were then oriented and scaled incorrectly in the renderer, which broke smooth overlap between neighboring splats and produced jagged, distorted edges.

## Solution

The quaternion reconstruction in the SOG reader was corrected so that each decoded quaternion is converted to `(x, y, z, w)` before being packed for rendering.

File changed:

- `projects/Splatviewer_VR/Assets/Scripts/VR/PlayCanvasSogReader.cs`

## Result

SOG rotations now decode consistently with the rest of the pipeline, matching the conventions already used for PLY, SPZ, and SPX import paths. This removes the incorrect splat orientation that was causing the visible artifacts.