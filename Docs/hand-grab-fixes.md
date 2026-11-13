# Nox XR hand grab - what was fixed

Follows the audit in `hand-grab-audit.md`. Same numbering.

## 1. Grab pose (edge and corner grabs)

1. **Box face chosen by what the hand touched** - **fixed**
   `GrabSurface.TryContact(collider, point, facing, out contact, out normal)` replaces `TryNormal`. `facing` is the
   direction the grab looks at the object with (the opposite of the palm normal) and picks the face: the `+axis`
   and `-axis` normal that is most aligned with it wins. The geometric reach ratio is only used when the grab comes
   in along the surface (`|dot| < 0.05`). A grab on the border of a panel now holds the panel, never its rim.
2. **Contact point clamped to the silhouette** - **fixed**
   The point is projected onto the chosen face and clamped inside it, so the contact is the border of that face
   rather than a rim point, and a palm inside the box still resolves to the face it looks at.
3. **180-degree turn about an arbitrary axis** - **fixed**
   `PutSurfaceInPalm` detects `dot(faceNormal, -palmNormal) < -0.9995` and turns explicitly by 180 degrees around
   the palm normal.
4. **Explicit-target grabs had no contact** - **fixed**
   `Hand.FindContact(target)` resolves the contact against the closest collider of that target, used by
   `TryGrab(IGrabbable)`.

## 2. Palm frame (holding point and normal)

5. **Holding point was an AABB centre** - **fixed**
   `HoldingPoint` now returns `collider.transform.TransformPoint(box.center)`: the collider's own centre turned
   with it, exact for a rotated hand and not one physics step behind.
6. **Distance measured from the controller origin** - **fixed**
   `FindGrabbable` searches and measures from `HoldingPoint`, the same point `PutSurfaceInPalm` lands the contact
   on.
7. **Palm normal needed a rigged hand** - **fixed**
   Without an avatar hand it falls back on the controller axes (`-right` on the right side, `+right` on the left,
   measured on the controller template).
13. **Palm normal sign and drift** - **fixed**
   `TryGetPalmNormal` is now side aware: a hand is mirrored, so the same cross product answers the palm on one side
   and the back of the hand on the other (`Cross(side, forward)` right, `Cross(forward, side)` left).

## 3. Fingers

8. **Finger shaping mis-measured** - **fixed**
   - `Physics.SyncTransforms()` runs before the sweep, so the colliders are at the pose written this frame and not
     at last physics step's (the panel landing in the palm used to be probed where it was, so every finger missed
     and closed to a fist).
   - The fan is zeroed for the sweep and restored after: it swung the fingertip sideways out of the surface.
   - `TipPosition` uses the whole last phalanx instead of half of it.
   - The re-measure test also compares the rotation of the object in the hand (5 degrees).

## 4. Timing and lifecycle

10. **Second hand and driver handover** - **fixed**
    `PlayerMenu.BeginHold` only drives the pose for the first grabber; `EndHold` calls `DriveGrab` on the new first
    holder when the driver lets go, instead of leaving the panel at the offset of a hand that no longer holds it.
11. **Transition trailing the palm** - **fixed**
    `DriveGrab` captures the start and destination poses **in the frame of the grab point** once, and
    `UpdateGrabPose` interpolates between them: the panel ends exactly at the grab pose whatever the hand did
    during the travel. A release mid-transition finishes the travel before the panel is left where it is.
12. **Duplicated throw velocity** - **fixed**
    `Hand.Release` no longer adds the hand velocity on top of the one the grabbable measures itself.

## Still open

- Item 9 (menu collider list contains two disabled capsule handles) is harmless: `ShapeOn` and `FindContact` skip
  disabled colliders.
- The palm centre is still the centre of the hand box; placing it on the palm skin (half the box thickness along
  the palm normal) is a tuning change, not a bug.
