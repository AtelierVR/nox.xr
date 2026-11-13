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

		/// <summary>Offset of the calibrated part relative to its tracker, in world space.</summary>
		public static Vector3 GetOffset(TrackerBinding binding, Quaternion trackerRotation, float currentScale) {
			var scale = binding.Scale > 0f ? currentScale / binding.Scale : 1f;
			return trackerRotation * (binding.OffsetPosition * scale);
		}
	}
}
