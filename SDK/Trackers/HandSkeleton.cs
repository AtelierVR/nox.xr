using Nox.CCK.Entities;
using UnityEngine;

namespace Nox.XR.Trackers {
	/// <summary>Pose of one hand joint, in tracking space.</summary>
	public struct HandJointPose {
		/// <summary>Whether the runtime reported this joint right now.</summary>
		public bool Valid;

		/// <summary>Radius of the joint, in metres (0 when the runtime does not expose it).</summary>
		public float Radius;

		public Vector3 Position;

		public Quaternion Rotation;

		/// <summary>Rotation of this joint relative to its parent, in tracking space.</summary>
		/// <param name="parent">Parent joint pose.</param>
		public Quaternion RelativeTo(in HandJointPose parent)
			=> Quaternion.Inverse(parent.Rotation) * Rotation;
	}

	/// <summary>
	/// Curls of a tracked hand, from 0 (fully extended) to 1 (fully curled), plus the pinch and the
	/// spread. They are a convenience on top of <see cref="HandSkeleton.Joints"/>: the per-joint poses
	/// carry the full motion (flexion, abduction and twist), the curls only summarise it.
	/// </summary>
	public struct HandFingerCurls {
		public float Thumb;
		public float Index;
		public float Middle;
		public float Ring;
		public float Little;

		/// <summary>Thumb tip ↔ index tip pinch, 0 (apart) to 1 (touching).</summary>
		public float Pinch;

		/// <summary>Abduction: angle between the index and the little finger, normalised 0 (together) to 1 (spread).</summary>
		public float Spread;

		/// <summary>Highest curl of the four fingers (palm formed or not).</summary>
		public float Grip
			=> Mathf.Max(Mathf.Max(Index, Middle), Mathf.Max(Ring, Little));
	}

	/// <summary>
	/// Skeleton of a tracked hand, addressed with the rig slots of the entity: the hand bone is the
	/// wrist, and each finger exposes its three bones (see <see cref="HandRig"/>) - the same slots the
	/// avatar is described with, so nothing has to be translated between the tracker and the model.
	/// <para>
	/// The joints are what a consumer poses an avatar with (see the reference-delta retargeting in
	/// <c>HandFingers</c>): each joint gives its full rotation relative to its parent, so flexion,
	/// abduction and twist all come through. <see cref="Joints"/> is owned by the producer and reused
	/// from frame to frame (no allocation per read).
	/// </para>
	/// </summary>
	public struct HandSkeleton {
		/// <summary>Whether a hand is tracked right now.</summary>
		public bool Tracked;

		/// <summary>Whose hand this is.</summary>
		public bool IsLeft;

		/// <summary>Curled summary of the fingers (see <see cref="HandFingerCurls"/>).</summary>
		public HandFingerCurls Curls;

		/// <summary>Pose of every rig slot that can hold a hand joint, indexed by <see cref="EntityRig"/>.</summary>
		public HandJointPose[] Joints;

		/// <summary>
		/// Pose of the tip of each finger (0 = thumb ... 4 = pinky). The rig reuses its "tip" slots for the
		/// last bone of the finger, so the real fingertip has no slot of its own - and it is what tells where
		/// that last bone points.
		/// </summary>
		public HandJointPose[] Tips;

		/// <summary>Pose of the tip of one finger.</summary>
		public bool TryGetTip(int finger, out HandJointPose pose) {
			pose = default;

			if (Tips == null || finger < 0 || finger >= Tips.Length)
				return false;

			pose = Tips[finger];
			return pose.Valid;
		}

		public bool TryGetJoint(EntityRig rig, out HandJointPose pose) {
			pose = default;

			var index = (int)rig;
			if (Joints == null || index < 0 || index >= Joints.Length)
				return false;

			pose = Joints[index];
			return pose.Valid;
		}

		public bool TryGetJoint(EntityRig rig, out Vector3 position, out Quaternion rotation) {
			position = default;
			rotation = default;

			if (!TryGetJoint(rig, out var pose))
				return false;

			position = pose.Position;
			rotation = pose.Rotation;
			return true;
		}
	}

	/// <summary>
	/// Hand slots of <see cref="EntityRig"/>: the hand bone is the wrist, and each finger is three bones.
	/// <para>
	/// The rig lists one finger, in the order Thumb, Index, Middle, Ring, Pinky, then the tips, then the
	/// nails - so a finger answers with <c>start + finger</c> for its proximal bone, <c>+ 5</c> for its
	/// distal one and <c>+ 10</c> for its intermediate one (the naming the rig already uses).
	/// </para>
	/// </summary>
	public static class HandRig {
		/// <summary>Number of fingers a hand has.</summary>
		public const int FingerCount = 5;

		/// <summary>Number of rig slots a hand skeleton can use.</summary>
		public static readonly int SlotCount = Mathf.Max((int)EntityRig.RightPinkyNail, (int)EntityRig.LeftPinkyNail) + 1;

		/// <summary>Wrist bone of a hand.</summary>
		public static EntityRig Wrist(bool left)
			=> left ? EntityRig.LeftHand : EntityRig.RightHand;

		/// <summary>
		/// Bone of a finger: bone 0 is the proximal one, 1 the intermediate one and 2 the distal one
		/// (the tip of the rig).
		/// </summary>
		/// <param name="left">Left hand when true, right hand otherwise.</param>
		/// <param name="finger">0 = thumb, 1 = index, 2 = middle, 3 = ring, 4 = pinky.</param>
		/// <param name="bone">0 = proximal, 1 = intermediate, 2 = distal.</param>
		public static EntityRig Bone(bool left, int finger, int bone) {
			var start = left ? (int)EntityRig.LeftThumb : (int)EntityRig.RightThumb;
			var offset = bone switch {
				0 => 0,  // proximal
				1 => 10, // intermediate (the "nail" slot of the rig)
				2 => 5,  // distal (the "tip" slot of the rig)
				_ => 0
			};

			return (EntityRig)(start + offset + finger);
		}

		/// <summary>Parent slot of a finger bone: the wrist for a proximal bone, the bone above otherwise.</summary>
		public static EntityRig Parent(bool left, int finger, int bone)
			=> bone <= 0 ? Wrist(left) : Bone(left, finger, bone - 1);
	}
}
