using Nox.Avatars.Hand;
using Nox.CCK.Development;
using Nox.CCK.Interactions;
using Nox.Interactions;
using UnityEngine;
using Gizmos = Nox.CCK.Development.Gizmos;

namespace Nox.XR.Player {
	[Gizmos("xr.grab")]
	public partial class Hand : IGizmos {
		private static readonly Color _queryFree = new(0.55f, 0.55f, 0.55f, 1f);
		private static readonly Color _queryTarget = new(1f, 0.9f, 0.2f, 1f);
		private static readonly Color _queryHeld = new(0.2f, 0.6f, 1f, 1f);
		private static readonly Color _contactColor = new(1f, 0.35f, 0.1f, 1f);
		private static readonly Color _palmColor = new(0.2f, 1f, 0.7f, 1f);
		private static readonly Color _fingerColor = new(0.3f, 1f, 0.3f, 1f);
		private static readonly Color _trackedColor = new(0.2f, 0.85f, 1f, 1f);
		private static readonly Color _objectColor = new(1f, 1f, 1f, 1f);
		private static readonly Color _wantedColor = new(1f, 0.75f, 0.2f, 1f);
		private static readonly Color _ghostColor = new(1f, 0.3f, 1f, 1f);
		private static readonly Color _previewColor = new(0.65f, 1f, 0.35f, 1f);

		private static readonly Vector3[] _fingerPoints = new Vector3[4];
		private static readonly Vector3[] _boxCorners = new Vector3[8];
		private static readonly int[] _boxEdges = { 0, 1, 1, 3, 3, 2, 2, 0, 4, 5, 5, 7, 7, 6, 6, 4, 0, 4, 1, 5, 2, 6, 3, 7 };
		private static Vector3[] _previewJoints = new Vector3[20];
		private static int[] _previewCounts = new int[5];

		public void OnDrawGizmos() {
			if (!isActiveAndEnabled)
				return;

			DrawQuery();
			DrawContact();
			DrawPreview();
			DrawPalm();
			DrawTracked();
			DrawHold();
		}

		private void DrawQuery() {
			var origin = HoldingPoint;
			var forward = transform.forward;
			var reach = Mathf.Max(grabReach, 0.001f);

			Gizmos.Color = _grabState == GrabState.Held
				? _queryHeld
				: _grabTarget != null ? _queryTarget : _queryFree;

			Gizmos.DrawWireSphere(origin, reach);
			Gizmos.DrawSphere(origin, 0.006f);
			Gizmos.DrawLine(origin, origin + forward * reach);
			Gizmos.DrawDottedLine(origin, origin + Vector3.Lerp(forward, transform.right, 0.65f) * reach, 6f);
			Gizmos.DrawArrow(origin + forward * reach, forward, reach * 0.15f, reach * 0.05f);
		}

		private void DrawContact() {
			var origin = HoldingPoint;
			var target = _grabTarget;

			if (target?.Target) {
				Gizmos.Color = _queryTarget;
				Gizmos.DrawWireSphere(target.Target.position, 0.025f);
				Gizmos.DrawDottedLine(origin, target.Target.position, 4f);
			}

			if (!_grabHit.collider)
				return;

			var point = _grabHit.point;
			var normal = _grabHit.normal;

			Gizmos.Color = _contactColor;
			Gizmos.DrawLine(origin, point);
			Gizmos.DrawSphere(point, 0.008f);
			Gizmos.DrawWireDisc(point, normal, 0.02f);
			Gizmos.DrawArrow(point, normal, 0.05f, 0.015f);
		}

		private void DrawPreview() {
			var target = _grabTarget;

			if (target?.Target == null || _grabState != GrabState.Free)
				return;

			var poseHand = PoseHand;
			var fingers = poseHand?.Fingers;
			var count = fingers?.Length ?? 0;

			if (_previewJoints.Length < count * 4)
				_previewJoints = new Vector3[count * 4];

			if (_previewCounts.Length < count)
				_previewCounts = new int[count];

			if (!TryPreviewGrab(target, _grabHit, _previewJoints, _previewCounts, out var pose))
				return;

			var handDelta = pose.HandPosition - transform.position;

			Gizmos.Color = _previewColor;
			Gizmos.DrawWireSphere(pose.HandPosition, 0.02f);
			DrawAxes(pose.HandPosition, transform.rotation, 0.06f);

			if (handDelta != Vector3.zero)
				Gizmos.DrawDottedLine(transform.position, pose.HandPosition, 6f);

			if (pose.ObjectDelta != Vector3.zero) {
				Gizmos.DrawDottedLine(_grabHit.point, _grabHit.point + pose.ObjectDelta, 4f);
				Gizmos.DrawWireSphere(_grabHit.point + pose.ObjectDelta, 0.02f);

				var colliders = PreviewColliders(target);

				for (var i = 0; i < colliders.Count; i++)
					DrawGhostCollider(colliders[i], pose.ObjectDelta);
			}

			var palm = PalmVolume;

			if (palm)
				DrawGhostCollider(palm, handDelta);
			else
				Gizmos.DrawWireCube(transform.position + handDelta, new Vector3(0.08f, 0.03f, 0.1f));

			if (fingers == null)
				return;

			for (var i = 0; i < fingers.Length && i < count; i++) {
				var joints = _previewCounts[i];
				if (joints == 0)
					continue;

				for (var j = 1; j < joints; j++)
					Gizmos.DrawLine(_previewJoints[i * 4 + j - 1], _previewJoints[i * 4 + j]);

				var finger = fingers[i];
				var radius = finger != null && finger.TipRadius > 0f ? finger.TipRadius : 0.004f;

				Gizmos.DrawWireSphere(_previewJoints[i * 4 + joints - 1], Mathf.Max(radius, 0.004f));
			}
		}

		private void DrawPalm() {
			Gizmos.Color = _palmColor;

			var palm = PalmVolume;
			if (palm)
				DrawGhostCollider(palm, Vector3.zero);

			Gizmos.DrawWireSphere(HoldingPoint, 0.01f);

			DrawFingers(PoseHand, transform.position, transform.rotation, _fingerColor);
		}

		private void DrawTracked() {
			if (HasTrackedPose) {
				Gizmos.Color = _trackedColor;
				DrawAxes(TrackedPosition, TrackedRotation, 0.05f);
				Gizmos.DrawDottedLine(TrackedPosition, transform.position, 4f);
			}

			if (!PhysicsFollow)
				return;

			var point = FollowPoint;

			Gizmos.Color = _trackedColor;
			Gizmos.DrawWireSphere(point.position, 0.012f);
			Gizmos.DrawLine(point.position, transform.position);
		}

		private void DrawHold() {
			var held = _held;

			if (held?.Target == null)
				return;

			var target = held.Target;

			Gizmos.Color = _objectColor;
			DrawAxes(target.position, target.rotation, 0.07f);

			if (held is not GrabbableBase grabbable)
				return;

			var wanted = grabbable.TryGetDebugHoldPose(out var holdPosition, out var holdRotation);

			if (wanted) {
				Gizmos.Color = _wantedColor;
				DrawAxes(holdPosition, holdRotation, 0.05f);
				Gizmos.DrawDottedLine(target.position, holdPosition, 5f);
			}

			if (grabbable.TryGetDebugRequestedPose(this, out var requestPosition, out var requestRotation)) {
				Gizmos.Color = _wantedColor;
				Gizmos.DrawWireSphere(requestPosition, 0.02f);
				DrawAxes(requestPosition, requestRotation, 0.03f);

				if (wanted)
					Gizmos.DrawLine(holdPosition, requestPosition);
			}

			if (!grabbable.TryGetDebugHandPose(this, out var handPosition, out var handRotation))
				return;

			if (grabbable.PhysicsHeld)
				return;

			var delta = handRotation * Quaternion.Inverse(transform.rotation);

			Gizmos.Color = _ghostColor;
			DrawAxes(handPosition, handRotation, 0.08f);
			Gizmos.DrawDottedLine(transform.position, handPosition, 8f);

			var palm = PalmVolume;
			if (palm) {
				var bounds = palm.bounds;
				Gizmos.DrawWireCube(handPosition + delta * (bounds.center - transform.position), bounds.size);
			}

			DrawFingers(PoseHand, handPosition, handRotation, _ghostColor);
		}

		private void DrawFingers(IHand poseHand, Vector3 position, Quaternion rotation, Color color) {
			var fingers = poseHand?.Fingers;

			if (fingers == null)
				return;

			var delta = rotation * Quaternion.Inverse(transform.rotation);

			Gizmos.Color = color;

			for (var i = 0; i < fingers.Length; i++) {
				var finger = fingers[i];
				if (finger == null)
					continue;

				var count = 0;

				if (finger.Proximal)
					_fingerPoints[count++] = Ghost(finger.Proximal.position);

				if (finger.Intermediate)
					_fingerPoints[count++] = Ghost(finger.Intermediate.position);

				if (finger.Distal)
					_fingerPoints[count++] = Ghost(finger.Distal.position);

				if (finger.Tip)
					_fingerPoints[count++] = Ghost(finger.Tip.position);

				for (var j = 1; j < count; j++)
					Gizmos.DrawLine(_fingerPoints[j - 1], _fingerPoints[j]);

				if (count == 0)
					continue;

				Gizmos.DrawWireSphere(_fingerPoints[count - 1], Mathf.Max(finger.TipRadius, 0.004f));
			}

			Vector3 Ghost(Vector3 world)
				=> position + delta * (world - transform.position);
		}

		private static void DrawAxes(Vector3 position, Quaternion rotation, float size) {
			Gizmos.DrawLine(position, position + rotation * Vector3.right * size);
			Gizmos.DrawLine(position, position + rotation * Vector3.up * size);
			Gizmos.DrawLine(position, position + rotation * Vector3.forward * size);
			Gizmos.DrawWireSphere(position, size * 0.2f);
		}

		private static void DrawGhostCollider(Collider collider, Vector3 offset) {
			if (!collider)
				return;

			var frame = collider.transform;
			var scale = frame.lossyScale;
			var uniform = Mathf.Max(Mathf.Abs(scale.x), Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(scale.z)));

			switch (collider) {
				case BoxCollider box:
					DrawOrientedBox(frame.TransformPoint(box.center) + offset, frame.rotation, Vector3.Scale(box.size, scale));
					break;

				case SphereCollider sphere:
					Gizmos.DrawWireSphere(frame.TransformPoint(sphere.center) + offset, sphere.radius * uniform);
					break;

				case CapsuleCollider capsule: {
					var axis = capsule.direction switch {
						0 => Vector3.right,
						1 => Vector3.up,
						_ => Vector3.forward
					};

					var centre = frame.TransformPoint(capsule.center) + offset;
					var direction = frame.rotation * axis;
					var half = Mathf.Max(capsule.height * 0.5f - capsule.radius, 0f) * uniform;

					Gizmos.DrawWireCapsule(centre - direction * half, centre + direction * half, capsule.radius * uniform);
					break;
				}

				default:
					Gizmos.DrawWireCube(collider.bounds.center + offset, collider.bounds.size);
					break;
			}
		}

		private static void DrawOrientedBox(Vector3 centre, Quaternion rotation, Vector3 size) {
			var half = size * 0.5f;

			for (var i = 0; i < _boxCorners.Length; i++) {
				var sign = new Vector3(
					(i & 1) == 0 ? -1f : 1f,
					(i & 2) == 0 ? -1f : 1f,
					(i & 4) == 0 ? -1f : 1f);

				_boxCorners[i] = centre + rotation * Vector3.Scale(half, sign);
			}

			for (var i = 0; i < _boxEdges.Length; i += 2)
				Gizmos.DrawLine(_boxCorners[_boxEdges[i]], _boxCorners[_boxEdges[i + 1]]);
		}
	}
}
