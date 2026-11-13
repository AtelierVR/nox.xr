# Nox XR hand grab - problem report

Audit of the hand grab path (`Hand` -> `GrabPose` -> `PlayerMenu.Grabbable` -> `FingerBend`).
Problems are ordered by severity inside each group.
Severities: **blocker** = grabbing is visibly broken, **major** = wrong or unstable pose, **minor** = degraded but usable.

## 1. Grab pose (edge and corner grabs)

1. **Box face chosen by normalized distance, not by what the hand touched** - **blocker**
   - Where: `Packages/nox.cck/Interaction/GrabPose.cs:141` (`TryBoxNormal`), logic at `:151`.
   - What is wrong: the face is the axis with the biggest `|local| / halfSize` ratio. On the menu panel (BoxCollider 0.124 x 0.547 x 0.549) a grab near a border or on a side face wins on a side axis.
   - Player-visible: the panel is turned 90 degrees and stands edge-on through the hand instead of lying flat on the palm.
   - Fix: choose the face from the direction the hand comes from (dot product with the palm approach axis), falling back on the reach ratio only for ties.

2. **Contact point clamped to the silhouette, or undefined when the palm is inside** - **major**
   - Where: `Packages/nox.xr/Runtime/Player/Parts/Hand.cs:617` (`collider.ClosestPoint(palm)`), inside `FindGrabbable` at `:595`.
   - What is wrong: with the palm outside the panel, `ClosestPoint` returns the border point (up to `grabRadius` away) and the panel is shifted by that full distance. With the palm inside the box it returns the input point, distance 0, and the face is undefined, so the first collider in the `OverlapSphere` buffer wins.
   - Player-visible: grabbing at the rim makes the panel leap sideways; grabbing in the middle gives a random orientation.
   - Fix: raycast from the palm along the palm approach into the grabbable, and use the ray hit point and normal as the contact, keeping `ClosestPoint` only as a fallback.

3. **180-degree turn taken about an arbitrary axis** - **major**
   - Where: `Packages/nox.cck/Interaction/GrabPose.cs:92` (`Quaternion.FromToRotation(contact.Normal, -palmNormal)`).
   - What is wrong: when contact normal and palm normal are nearly antiparallel, `FromToRotation` has no defined rotation axis and produces an arbitrary one.
   - Player-visible: the object can land flipped/twisted in an unpredictable direction.
   - Fix: detect the antiparallel case and build the turn explicitly around an axis perpendicular to the normal (and to `target.up`), otherwise use `FromToRotation`.

4. **Explicit-target grabs never build a contact** - **major**
   - Where: `Packages/nox.xr/Runtime/Player/Parts/Hand.cs:537` (`TryGrab(IGrabbable)`, `_contact` only filled at `:545`).
   - What is wrong: `_contact` is reset and only set in the `target == null` branch, so `PutSurfaceInPalm` bails out on `!contact.IsValid`.
   - Player-visible: a scripted/targeted grab leaves the object at its old offset instead of coming into the palm.
   - Fix: when a target is passed, compute the contact (closest point + normal against the target's colliders) before `OnGrab`.

## 2. Palm frame (holding point and normal)

5. **Holding point is the collider's world AABB centre** - **major**
   - Where: `Packages/nox.xr/Runtime/Player/Parts/Hand.cs:641` (`HoldingPoint`).
   - What is wrong: `bounds` is the world axis-aligned box of the collider (an AABB of a rotated box, and stale by one physics step); `HandPhysics.bodyCenter` is `(0, 0, 0.02)`, and `BodyCollider` is any collider found on the hand GameObject, triggers included.
   - Player-visible: the object is buried in the hand (placed at the box centre, not on the skin) and jitters as the hand rotates.
   - Fix: expose an explicit palm anchor from `HandPhysics` (a local point on the palm skin, resolved with `TransformPoint`) and use it as the holding point.

6. **Distance measured from the controller origin, contact placed on the palm** - **major**
   - Where: `Packages/nox.xr/Runtime/Player/Parts/Hand.cs:601` (`var palm = point.position;`) vs `GrabPose.cs:95` (`grabber.HoldingPoint`).
   - What is wrong: `FindGrabbable` measures distances and computes the normal from `GrabPoint.position`, but `PutSurfaceInPalm` lands the contact on `HoldingPoint`, so the two frames differ by the palm offset.
   - Player-visible: the grabbed point does not end up under the palm; the error grows with the controller-to-palm offset (larger hands, different rigs).
   - Fix: use `HoldingPoint` as the origin for both the overlap and the contact computation.

7. **Palm normal requires a rigged avatar hand** - **minor**
   - Where: `Packages/nox.xr/Runtime/Player/Parts/Hand.cs:678` (`TryGetPalmNormal`, needs Index/Middle/Pinky), consumed at `GrabPose.cs:88`.
   - What is wrong: without a bound avatar hand the method returns `false`, the turn stays identity, and `PutSurfaceInPalm` silently keeps the object's old rotation.
   - Player-visible: on an avatar-less or finger-less setup the grabbed object is not aligned to the palm at all.
   - Fix: fall back on the controller axes (palm normal = `GrabPoint` up or forward) when `TryGetPalmNormal` fails.

## 3. Fingers

8. **Finger shaping is a single shared curl, mis-measured** - **major**
   - Where: `Packages/nox.xr/Runtime/Player/Parts/FingerBend.cs:437` (`ShapeOn`), `:469` (`return 1f`), `:263` (`_fan` reset), `:493` (`TipPosition`), `:254` (re-measure guard).
   - What is wrong: (a) when no curl step touches anything the finger closes to a full fist instead of staying open; (b) it runs before `_fan` is reset while `Pose` applies the fan at the knuckle, so the measured fingertip is not the posed one, and `TipPosition` is ~1.5-2 cm short of the real tip; (c) the re-measure compares only the object origin in hand space with a 1 cm tolerance and ignores rotation.
   - Player-visible: fingers punch through thin panels or fist around nothing; a small object re-shaped by a rotation does not re-conform.
   - Fix: keep the last non-touching pose (open hand) instead of returning 1, reset the fan before measuring and measure at the true distal tip, and trigger the re-measure on any object pose delta (position + rotation angle).

## 4. Timing and lifecycle

9. **No `Physics.SyncTransforms()` after writing the panel pose** - **blocker**
   - Where: `Packages/nox.xr/Runtime/Player/Connectors/Menu/PlayerMenu.Grabbable.cs:205` (`SetPositionAndRotation`) vs `FingerBend.cs:485` (`ClosestPoint`).
   - What is wrong: the finger sweep reads collider poses from the last physics step, so on the frame the panel lands in the palm the fingers test where the panel was.
   - Player-visible: on the landing frame every finger measures a miss and closes to a full fist.
   - Fix: call `Physics.SyncTransforms()` after `SetPositionAndRotation` (or shape the fingers one frame later, once the panel pose is settled).

10. **Second holder is only appended, then promoted with a stale local pose** - **major**
    - Where: `Packages/nox.xr/Runtime/Player/Connectors/Menu/PlayerMenu.Grabbable.cs:211` (`BeginHold`, `_grabLocal*` set only for `_holders.Count == 0`), `:170` (`_holders[0].GrabPoint`).
    - What is wrong: a second holder never brings the panel to its palm but still shapes its fingers on it; when the first releases, the second becomes `_holders[0]` while the captured local pose still describes the first hand.
    - Player-visible: the panel jumps to the second hand, and the second hand's fingers are closed on nothing until then.
    - Fix: recompute `PutSurfaceInPalm` and the local pose on promotion (`EndHold` when `_holders.Count` drops to 1), and skip finger shaping for non-primary holders.

11. **Throw velocity applied twice** - **major**
    - Where: `Packages/nox.xr/Runtime/Player/Parts/Hand.cs:567` (`Release`), `:575` (`OnRelease`) and `:576` (`+= _linearVelocity`).
    - What is wrong: `target.OnRelease` already applies the measured throw velocity, then `Release` adds `_linearVelocity` again.
    - Player-visible: thrown objects fly roughly twice as fast as the hand moved.
    - Fix: let `OnRelease` own the throw velocity and drop the extra `+=`, or pass the velocity in and apply it in one place only.

12. **Lerp transition recomputes its target every frame** - **minor**
    - Where: `Packages/nox.xr/Runtime/Player/Connectors/Menu/PlayerMenu.Grabbable.cs:179-186`.
    - What is wrong: the end pose follows the moving grab point while `_from*` stays fixed, so the panel trails the palm for `grabDuration`; releasing mid-transition freezes it half-way and half-turned.
    - Player-visible: the panel lags and swims during the grab, and stays crooked if the grab is released quickly.
    - Fix: capture the destination pose once at `BeginHold` and interpolate to that fixed pose; on release, snap to the current destination.

## 5. Misc

13. **Palm normal drifts with the live rig, no fallback** - **minor**
    - Where: `Packages/nox.xr/Runtime/Player/Parts/Hand.cs:678` (rebuilt from live knuckle positions each call), used at `GrabPose.cs:88`.
    - What is wrong: the normal is derived from the animated knuckles, so any avatar grip/finger motion changes it (and therefore the turn), and there is no controller-axis fallback for a rig without fingers.
    - Player-visible: an object already in the hand slowly rotates as the fingers curl.
    - Fix: sample the palm normal once at grab time and store it on the hand, with the controller-axis fallback from item 7.

## Design invariants

- The hand is the master of its own position and rotation: a grab never moves and never rotates the hand (nor the controller behind it).
- The grabbed object is moved and rotated so that the point the grab touched lands on the palm centre, and the touched face lies flat against the palm.
- Fingers conform per finger, like AutoHand's `BendFingerUntilHit`: each finger sweeps its own curl until its tip reaches the object, so a flat panel holds a flat hand and an edge curls the fingers around it.
