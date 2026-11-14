using Nox.CCK.Entities;
using UnityEngine;

namespace Nox.XR.Calibration {
	/// <summary>
	/// Published calibration data, readable by loaders (they live in a separate assembly and cannot
	/// reference the runtime calibration component).
	/// <para>
	/// The runtime <c>CalibrationManager</c> component owns capture/persistence and publishes the
	/// current data here; loaders only read it to turn a tracker id into an <see cref="EntityRig"/>
	/// and to apply the stored offsets.
	/// </para>
	/// </summary>
	public static class Calibration {
		/// <summary>Current calibration data (empty until the runtime publishes it).</summary>
		public static CalibrationData Data { get; private set; } = new();

		/// <summary>Current avatar scale, used to compensate stored offsets.</summary>
		public static float CurrentScale { get; private set; } = 1f;

		public static void Publish(CalibrationData data, float scale = 1f) {
			Data = data ?? new CalibrationData();
			CurrentScale = scale > 0f ? scale : 1f;
		}

		public static void SetScale(float scale)
			=> CurrentScale = scale > 0f ? scale : 1f;

		public static void Clear() {
			Data = new CalibrationData();
			CurrentScale = 1f;
		}

		public static bool TryGetBinding(EntityRig rig, out TrackerBinding binding)
			=> Data.TryGetBinding(rig, out binding);

		public static bool TryGetRig(string trackerId, out EntityRig rig)
			=> Data.TryGetRig(trackerId, out rig);

		public static bool TryGetTrackerId(EntityRig rig, out string trackerId)
			=> Data.TryGetTrackerId(rig, out trackerId);

		public static bool IsTrackerClaimed(string trackerId)
			=> Data.TryGetRig(trackerId, out _);

		/// <summary>
		/// Whether a rig slot accepts calibration offsets. The HMD and the hands are driven by
		/// runtime devices, never by the full-body calibration.
		/// </summary>
		public static bool IsCalibratable(EntityRig rig)
			=> rig != EntityRig.None
			   && rig != EntityRig.Head
			   && rig != EntityRig.LeftHand
			   && rig != EntityRig.RightHand;

		/// <summary>Same as <see cref="IsCalibratable(EntityRig)"/> for a wire rig index.</summary>
		public static bool IsCalibratable(ushort rig)
			=> IsCalibratable(rig.ToEntityRig());

		/// <summary>
		/// Rotation the stored offsets of a binding are expressed in: the rotation the tracker went through since the
		/// calibration, or its yaw only for the hips (a tracker mounted at an angle must not tip or drop the pelvis).
		/// <para>
		/// The delta, not the absolute rotation: the yaw of a device mounted rolled or pitched is meaningless and does
		/// not follow the body, which left the pelvis - and the legs hanging from it - turned away from the player.
		/// The delta is exactly the identity at the calibration, so a confirmed calibration reproduces its pose.
		/// </para>
		/// <para>
		/// The same rotation is used to capture and to apply an offset, for the position <b>and</b> the rotation.
		/// </para>
		/// </summary>
		public static Quaternion Frame(TrackerBinding binding, Quaternion trackerRotation) {
			// Calibration stored before the capture pose was recorded: keep its own frame.
			if (!binding.HasCapture)
				return YawOf(trackerRotation) is { } legacyYaw && binding.Rig == EntityRig.Hips.ToIndex()
					? legacyYaw
					: trackerRotation;

			var delta = trackerRotation * Quaternion.Inverse(binding.CaptureRotation);

			return binding.Rig == EntityRig.Hips.ToIndex() && YawOf(delta) is { } yaw
				? yaw
				: delta;
		}

		/// <summary>Offset (world space) that turns the pose of a tracker into the pose of the bone it drives.</summary>
		public static Vector3 GetOffset(TrackerBinding binding, Quaternion trackerRotation, float currentScale) {
			var scale = binding.Scale > 0f ? currentScale / binding.Scale : 1f;
			return Frame(binding, trackerRotation) * (binding.OffsetPosition * scale);
		}

		public const float PelvisNearOffset = 0.12f;
		public const float PelvisFarOffset  = 0.32f;
		public const float PelvisMinWeight  = 0.6f;

		/// <summary>
		/// Position weight the pelvis may be given: a body tracker strapped away from the pelvis (belly, backpack)
		/// carries a long offset, and following it rigidly folds the spine and locks the legs. Full weight while the
		/// offset stays anatomical, easing down to <see cref="PelvisMinWeight"/> past <see cref="PelvisFarOffset"/>.
		/// </summary>
		public static float PelvisPositionWeight(TrackerBinding binding, Quaternion trackerRotation) {
			var distance = GetOffset(binding, trackerRotation, CurrentScale).magnitude;
			return Mathf.Lerp(1f, PelvisMinWeight, Mathf.InverseLerp(PelvisNearOffset, PelvisFarOffset, distance));
		}

		/// <summary>
		/// Bone pose for a tracker pose, both in <b>world</b> space: the offsets are measured on the avatar skeleton
		/// when the calibration is confirmed, so they can only be composed in the space they were captured in.
		/// Applying them before the tracking space transform (as a loader would have to) rotated them with the
		/// player and stretched them with the avatar fit, which moved the pelvis the instant the calibration started.
		/// </summary>
		public static void Apply(TrackerBinding binding, Vector3 trackerPosition, Quaternion trackerRotation, out Vector3 position, out Quaternion rotation) {
			var frame = Frame(binding, trackerRotation);

			position = trackerPosition + GetOffset(binding, trackerRotation, CurrentScale);
			rotation = frame * binding.OffsetRotation;
		}

		/// <summary>Yaw of a rotation (around the vertical axis), <c>null</c> when it cannot be defined.</summary>
		private static Quaternion? YawOf(Quaternion rotation) {
			var forward = rotation * Vector3.forward;
			forward.y = 0f;
			if (forward.sqrMagnitude < 0.0001f) {
				forward = rotation * Vector3.up;
				forward.y = 0f;
				if (forward.sqrMagnitude < 0.0001f)
					return null;
			}

			return Quaternion.LookRotation(forward.normalized, Vector3.up);
		}
	}
}
