using System.Collections.Generic;
using Nox.CCK.Entities;
using Nox.XR.Trackers;
using UnityEngine;
using UnityEngine.XR.Hands;

namespace Nox.XR.Calibration {
	/// <summary>
	/// Hand skeleton of the runtime, read from the <see cref="XRHandSubsystem"/> of Unity's XR Hands.
	/// <para>
	/// It is the source every loader shares: OpenXR hand tracking, a hand-tracking accessory mounted on a
	/// controller (UDCAP) or SteamVR's own skeleton all end up in that subsystem, so nox.xr never has to
	/// know which runtime produced the data. A loader advertises the capability through
	/// <see cref="TrackerFeatures.Skeleton"/>; this class only answers "what is the hand doing right now".
	/// </para>
	/// <para>
	/// The joints are exposed on the <see cref="EntityRig"/> slots of the hand (wrist + three bones per
	/// finger, see <see cref="HandRig"/>): the avatar is described with the same slots, so a consumer
	/// poses it without any translation table. The buffers are reused from frame to frame.
	/// </para>
	/// </summary>
	public static class HandTrackingSource {
		// A finger is fully curled at this total flexion (sum of the joint angles, in degrees): measured
		// on a natural fist, it keeps a closed hand at 1 without saturating half-closed fingers.
		private const float FullCurlDegrees = 150f;

		// Pinch range, in metres: below the first value the tips touch, above the second they are apart.
		private const float PinchNear = 0.015f;
		private const float PinchFar = 0.075f;

		// Spread range, in degrees, between the index and the little finger: a relaxed hand measures
		// around 20, a fully fanned hand 50 - the range has to cover both, or a normal open hand saturates.
		private const float SpreadClosed = 5f;
		private const float SpreadOpen = 55f;

		/// <summary>
		/// Runtime joints of each finger, in rig-bone order (proximal, intermediate, distal). The thumb has
		/// no intermediate bone, so its last two are its distal and its tip.
		/// </summary>
		private static readonly XRHandJointID[][] FingerJoints = {
			new[] { XRHandJointID.ThumbProximal,  XRHandJointID.ThumbDistal,        XRHandJointID.ThumbTip },
			new[] { XRHandJointID.IndexProximal,  XRHandJointID.IndexIntermediate,  XRHandJointID.IndexDistal },
			new[] { XRHandJointID.MiddleProximal, XRHandJointID.MiddleIntermediate, XRHandJointID.MiddleDistal },
			new[] { XRHandJointID.RingProximal,   XRHandJointID.RingIntermediate,   XRHandJointID.RingDistal },
			new[] { XRHandJointID.LittleProximal, XRHandJointID.LittleIntermediate, XRHandJointID.LittleDistal }
		};

		/// <summary>Tip of each finger, in the same order: what the last bone of a finger points at.</summary>
		private static readonly XRHandJointID[] FingerTips = {
			XRHandJointID.ThumbTip, XRHandJointID.IndexTip, XRHandJointID.MiddleTip, XRHandJointID.RingTip, XRHandJointID.LittleTip
		};

		private static readonly List<XRHandSubsystem> Subsystems = new();

		// One buffer per hand, reused: the skeleton is read every frame and must not allocate.
		private static readonly HandJointPose[] LeftJoints = NewJoints();
		private static readonly HandJointPose[] RightJoints = NewJoints();
		private static readonly HandJointPose[] LeftTips = NewTips();
		private static readonly HandJointPose[] RightTips = NewTips();

		private static XRHandSubsystem _subsystem;
		private static int _subsystemFrame = -1;

		/// <summary>Whether the runtime exposes a hand skeleton at all.</summary>
		public static bool IsAvailable
			=> Subsystem != null;

		/// <summary>Reads the whole skeleton of one hand; <c>false</c> when that hand is not tracked.</summary>
		/// <param name="isLeft">Left hand when true, right hand otherwise.</param>
		/// <param name="skeleton">Skeleton of the hand; its <c>Joints</c> buffer belongs to this class.</param>
		public static bool TryGetSkeleton(bool isLeft, out HandSkeleton skeleton) {
			skeleton = default;

			var subsystem = Subsystem;
			if (subsystem == null)
				return false;

			var hand = isLeft ? subsystem.leftHand : subsystem.rightHand;
			if (!hand.isTracked)
				return false;

			var joints = isLeft ? LeftJoints : RightJoints;
			var tips = isLeft ? LeftTips : RightTips;
			Fill(hand, isLeft, joints, tips);

			skeleton.Tracked = true;
			skeleton.IsLeft = isLeft;
			skeleton.Joints = joints;
			skeleton.Tips = tips;
			skeleton.Curls = CurlsOf(hand);
			return true;
		}

		/// <summary>Reads only the curls of one hand; <c>false</c> when that hand is not tracked.</summary>
		public static bool TryGetCurls(bool isLeft, out HandFingerCurls curls) {
			if (TryGetSkeleton(isLeft, out var skeleton)) {
				curls = skeleton.Curls;
				return true;
			}

			curls = default;
			return false;
		}

		/// <summary>The subsystem of the running runtime, resolved once and cached.</summary>
		private static XRHandSubsystem Subsystem {
			get {
				if (_subsystem != null && _subsystem.running)
					return _subsystem;

				// The subsystem list only changes when the XR runtime restarts: resolving it once per
				// frame is enough and keeps the per-frame reads allocation free.
				if (_subsystemFrame == Time.frameCount)
					return _subsystem;

				_subsystemFrame = Time.frameCount;
				_subsystem = null;

				Subsystems.Clear();
				SubsystemManager.GetSubsystems(Subsystems);
				for (var i = 0; i < Subsystems.Count; i++) {
					if (Subsystems[i] == null || !Subsystems[i].running)
						continue;

					_subsystem = Subsystems[i];
					break;
				}

				return _subsystem;
			}
		}

		private static HandJointPose[] NewJoints()
			=> new HandJointPose[HandRig.SlotCount];

		private static HandJointPose[] NewTips()
			=> new HandJointPose[HandRig.FingerCount];

		/// <summary>Copies the wrist and every finger bone of the tracked hand into the rig slots.</summary>
		private static void Fill(XRHand hand, bool isLeft, HandJointPose[] joints, HandJointPose[] tips) {
			Slice(hand, HandRig.Wrist(isLeft), joints);

			for (var finger = 0; finger < HandRig.FingerCount; finger++) {
				for (var bone = 0; bone < 3; bone++)
					Slice(hand, HandRig.Bone(isLeft, finger, bone), joints, FingerJoints[finger][bone]);

				tips[finger] = Slice(hand, FingerTips[finger]);
			}
		}

		private static void Slice(XRHand hand, EntityRig rig, HandJointPose[] joints)
			=> Slice(hand, rig, joints, XRHandJointID.Wrist);

		private static void Slice(XRHand hand, EntityRig rig, HandJointPose[] joints, XRHandJointID id) {
			var index = (int)rig;
			if (joints == null || index < 0 || index >= joints.Length)
				return;

			joints[index] = Slice(hand, id);
		}

		/// <summary>Reads one joint, or an invalid pose when the runtime does not report a usable one.</summary>
		private static HandJointPose Slice(XRHand hand, XRHandJointID id) {
			var joint = hand.GetJoint(id);
			var pose = default(HandJointPose);

			// A pose the runtime flags as tracked can still be broken: it must not leak a NaN into the
			// skeleton, where a consumer would freeze on it.
			if (joint.TryGetPose(out var jointPose) && Finite(jointPose.position) && Finite(jointPose.rotation)) {
				pose.Valid = true;
				pose.Position = jointPose.position;
				pose.Rotation = jointPose.rotation;
			}

			if (joint.TryGetRadius(out var radius) && Finite(radius))
				pose.Radius = radius;

			return pose;
		}

		/// <summary>Summarises the finger motion of the tracked hand.</summary>
		private static HandFingerCurls CurlsOf(XRHand hand) {
			var curls = default(HandFingerCurls);

			curls.Thumb = Curl(hand, XRHandJointID.ThumbMetacarpal, XRHandJointID.ThumbProximal, XRHandJointID.ThumbDistal, XRHandJointID.ThumbTip);
			curls.Index = Curl(hand, XRHandJointID.IndexProximal, XRHandJointID.IndexIntermediate, XRHandJointID.IndexDistal, XRHandJointID.IndexTip);
			curls.Middle = Curl(hand, XRHandJointID.MiddleProximal, XRHandJointID.MiddleIntermediate, XRHandJointID.MiddleDistal, XRHandJointID.MiddleTip);
			curls.Ring = Curl(hand, XRHandJointID.RingProximal, XRHandJointID.RingIntermediate, XRHandJointID.RingDistal, XRHandJointID.RingTip);
			curls.Little = Curl(hand, XRHandJointID.LittleProximal, XRHandJointID.LittleIntermediate, XRHandJointID.LittleDistal, XRHandJointID.LittleTip);

			curls.Pinch = Pinch(hand);
			curls.Spread = Spread(hand);
			return curls;
		}

		/// <summary>
		/// Flexion of one finger: the sum of the angles between its consecutive bones, normalised. The
		/// first bone is the one that leaves the palm, so a straight finger is 0 and a fist is 1.
		/// </summary>
		private static float Curl(XRHand hand, XRHandJointID a, XRHandJointID b, XRHandJointID c, XRHandJointID d) {
			if (!TryGetPosition(hand, a, out var pa)
				|| !TryGetPosition(hand, b, out var pb)
				|| !TryGetPosition(hand, c, out var pc)
				|| !TryGetPosition(hand, d, out var pd))
				return 0f;

			var ab = pb - pa;
			var bc = pc - pb;
			var cd = pd - pc;

			// A runtime can report a joint without a usable position: Vector3.Angle of a null segment is
			// a NaN, and a NaN curl would poison the pose of that finger for the rest of the session.
			if (ab.sqrMagnitude < 1e-8f || bc.sqrMagnitude < 1e-8f || cd.sqrMagnitude < 1e-8f)
				return 0f;

			var flexion = Vector3.Angle(ab, bc) + Vector3.Angle(bc, cd);
			return Finite(flexion) ? Mathf.Clamp01(flexion / FullCurlDegrees) : 0f;
		}

		/// <summary>Pinch: how close the thumb tip is to the index tip.</summary>
		private static float Pinch(XRHand hand) {
			if (!TryGetPosition(hand, XRHandJointID.ThumbTip, out var thumb)
				|| !TryGetPosition(hand, XRHandJointID.IndexTip, out var index))
				return 0f;

			var distance = Vector3.Distance(thumb, index);
			return 1f - Mathf.Clamp01((distance - PinchNear) / (PinchFar - PinchNear));
		}

		/// <summary>Abduction: angle between the index and the little finger.</summary>
		private static float Spread(XRHand hand) {
			if (!TryGetPosition(hand, XRHandJointID.IndexProximal, out var indexProximal)
				|| !TryGetPosition(hand, XRHandJointID.IndexTip, out var indexTip)
				|| !TryGetPosition(hand, XRHandJointID.LittleProximal, out var littleProximal)
				|| !TryGetPosition(hand, XRHandJointID.LittleTip, out var littleTip))
				return 0f;

			return Mathf.Clamp01((Vector3.Angle(indexTip - indexProximal, littleTip - littleProximal) - SpreadClosed) / (SpreadOpen - SpreadClosed));
		}

		private static bool TryGetPosition(XRHand hand, XRHandJointID id, out Vector3 position) {
			position = default;

			var joint = hand.GetJoint(id);
			if (!joint.TryGetPose(out var pose))
				return false;

			// A runtime can flag a joint as tracked and still report a broken pose: a NaN position would
			// come out as a NaN curl, and a NaN curl freezes that finger on the avatar for the session.
			if (!Finite(pose.position))
				return false;

			position = pose.position;
			return true;
		}

		private static bool Finite(Vector3 value)
			=> Finite(value.x) && Finite(value.y) && Finite(value.z);

		private static bool Finite(Quaternion value)
			=> Finite(value.x) && Finite(value.y) && Finite(value.z) && Finite(value.w);

		private static bool Finite(float value)
			=> !float.IsNaN(value) && !float.IsInfinity(value);
	}
}
