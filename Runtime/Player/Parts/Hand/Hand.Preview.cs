using System.Collections.Generic;
using Nox.Avatars.Hand;
using Nox.Interactions;
using Nox.XR.Runtime.Settings;
using UnityEngine;

namespace Nox.XR.Player {
	public struct GrabPreviewPose {
		public Vector3 HandPosition;
		public Vector3 ObjectDelta;
	}

	public partial class Hand {
		private const int FINGER_JOINTS = 4;

		private static readonly Vector3[] _chainPoints = new Vector3[FINGER_JOINTS];
		private static readonly Transform[] _chainJoints = new Transform[FINGER_JOINTS];
		private static readonly Quaternion[] _openPose = new Quaternion[FINGER_JOINTS];
		private static readonly Quaternion[] _closedPose = new Quaternion[FINGER_JOINTS];
		private static readonly List<Collider> _previewColliders = new();

		private void UpdatePreviewPose() {
			if (!PreviewPoseSettings.Enabled
				|| _grabState != GrabState.Free
				|| _grabTarget == null
				|| _conformTarget != null) {
				ClearPreviewConform();
				return;
			}

			if (!TryPreviewGrab(_grabTarget, _grabHit, null, null, out var pose))
				return;

			PreviewConform(_grabTarget, pose.ObjectDelta);
		}

		public Vector3 ReachOffsetTo(in RaycastHit contact) {
			if (!contact.collider)
				return Vector3.zero;

			var delta = contact.point - Palm.position;

			return delta.magnitude > maxReachOffset
				? delta.normalized * maxReachOffset
				: delta;
		}

		public bool TryPreviewGrab(IGrabbable grabbable, in RaycastHit contact, Vector3[] joints, int[] counts, out GrabPreviewPose pose) {
			pose = default;

			if (grabbable?.Target == null)
				return false;

			var handDelta = Vector3.zero;
			var objectDelta = Vector3.zero;

			if (ReachesTarget(grabbable)) {
				handDelta = ReachOffsetTo(contact);
			} else if (contact.collider) {
				var landing = TryGetPalmSurface(contact.point, out var surface) ? surface : HoldingPoint;
				objectDelta = landing - contact.point;
			}

			pose = new GrabPreviewPose {
				HandPosition = transform.position + handDelta,
				ObjectDelta = objectDelta
			};

			if (joints != null && counts != null)
				FillPreviewFingers(grabbable, handDelta, objectDelta, joints, counts);

			return true;
		}

		private void FillPreviewFingers(IGrabbable grabbable, Vector3 handDelta, Vector3 objectDelta, Vector3[] joints, int[] counts) {
			var fingers = PoseHand?.Fingers;

			if (fingers == null)
				return;

			var pinch = grabbable is IGrabAttachment { PoseType: GrabPoseType.Pinch };
			var colliders = PreviewColliders(grabbable);

			for (var i = 0; i < fingers.Length && i < counts.Length; i++) {
				counts[i] = 0;

				var finger = fingers[i];
				if (finger == null || (i + 1) * FINGER_JOINTS > joints.Length)
					continue;

				var bend = SweepBend(finger, pinch, handDelta, objectDelta, colliders);
				var count = BuildChain(finger, pinch, bend < 0f ? 0f : bend);

				for (var j = 0; j < count; j++)
					joints[i * FINGER_JOINTS + j] = _chainPoints[j] + handDelta;

				counts[i] = count;
			}
		}

		private bool ReachesTarget(IGrabbable target) {
			if (target is IGrabAttachment { InstantGrab: true })
				return false;

			return ResolveGrabType(target) == GrabType.HandToGrabbable
				|| !CanPullToHand(target);
		}

		private static IReadOnlyList<Collider> PreviewColliders(IGrabbable grabbable) {
			if (grabbable is IGrabColliders holder && holder.GrabColliders != null && holder.GrabColliders.Count > 0)
				return holder.GrabColliders;

			_previewColliders.Clear();

			var root = grabbable.Target;
			if (root)
				root.GetComponentsInChildren(false, _previewColliders);

			return _previewColliders;
		}

		private float SweepBend(IFinger finger, bool pinch, Vector3 handDelta, Vector3 objectDelta, IReadOnlyList<Collider> colliders) {
			if (colliders == null || colliders.Count == 0)
				return -1f;

			if (Overlaps(finger, pinch, 0f, handDelta, objectDelta, colliders))
				return -1f;

			var steps = Mathf.Max(conformSteps, 4);
			var coarse = 5f / steps;

			for (var value = coarse; value <= 1f; value += coarse) {
				if (!Overlaps(finger, pinch, value, handDelta, objectDelta, colliders))
					continue;

				var from = Mathf.Max(0f, value - coarse);
				var fine = 1f / steps;

				for (var reach = from; reach <= value; reach += fine)
					if (Overlaps(finger, pinch, reach, handDelta, objectDelta, colliders))
						return reach;

				return value;
			}

			return -1f;
		}

		private static bool Overlaps(IFinger finger, bool pinch, float bend, Vector3 handDelta, Vector3 objectDelta, IReadOnlyList<Collider> colliders) {
			var count = BuildChain(finger, pinch, bend);

			if (count == 0)
				return false;

			var probe = _chainPoints[count - 1] + handDelta - objectDelta;
			var radius = finger.TipRadius > 0f ? finger.TipRadius : InteractionConstants.DefaultTipRadius;

			for (var i = 0; i < colliders.Count; i++) {
				var collider = colliders[i];
				if (!collider || !collider.enabled || collider is MeshCollider { convex: false })
					continue;

				var closest = collider.ClosestPoint(probe);

				if ((closest - probe).sqrMagnitude <= radius * radius)
					return true;
			}

			return false;
		}

		private static int BuildChain(IFinger finger, bool pinch, float bend) {
			var proximal = finger.Proximal;

			if (!proximal || !proximal.parent)
				return 0;

			_chainJoints[0] = proximal;
			_chainJoints[1] = finger.Intermediate;
			_chainJoints[2] = finger.Distal;
			_chainJoints[3] = finger.Tip;

			var pinched = pinch
						 && ReadPose(finger, FingerCurl.PinchedOpened, _openPose)
						 && ReadPose(finger, FingerCurl.PinchedClosed, _closedPose);

			if (!pinched && !(ReadPose(finger, FingerCurl.Opened, _openPose) && ReadPose(finger, FingerCurl.Closed, _closedPose)))
				for (var i = 0; i < _chainJoints.Length; i++) {
					var joint = _chainJoints[i];
					_openPose[i] = _closedPose[i] = joint ? joint.localRotation : Quaternion.identity;
				}

			var matrix = proximal.parent.localToWorldMatrix;
			var count = 0;

			for (var i = 0; i < _chainJoints.Length; i++) {
				var joint = _chainJoints[i];
				if (!joint)
					continue;

				matrix *= Matrix4x4.TRS(joint.localPosition, Quaternion.Slerp(_openPose[i], _closedPose[i], bend), joint.localScale);
				_chainPoints[count++] = matrix.GetColumn(3);
			}

			return count;
		}

		private static bool ReadPose(IFinger finger, FingerCurl curl, Quaternion[] values) {
			var poses = finger.Poses;

			if (poses == null)
				return false;

			for (var i = 0; i < poses.Length; i++) {
				var pose = poses[i];
				if (pose == null || pose.Curl != curl)
					continue;

				var source = pose.Values;
				if (source == null || source.Length < FINGER_JOINTS)
					return false;

				for (var j = 0; j < FINGER_JOINTS; j++)
					values[j] = source[j];

				return true;
			}

			return false;
		}
	}
}
