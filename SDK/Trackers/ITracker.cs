using System;
using Nox.CCK.Entities;

namespace Nox.XR.Trackers {
	/// <summary>What a tracker can provide.</summary>
	[Flags]
	public enum TrackerFeatures {
		None = 0,
		Position = 1 << 0,
		Rotation = 1 << 1,
		Velocity = 1 << 2,
		AngularVelocity = 1 << 3,

		/// <summary>Pose can be expressed with the calibration offsets.</summary>
		Calibrated = 1 << 4,

		/// <summary>The device also reports hand data: finger curls and a pinch value (see <see cref="TrackerKeys"/>).</summary>
		Skeleton = 1 << 5,

		/// <summary>The device carries controller inputs (grip, trigger, stick): it is a physical controller.</summary>
		Buttons = 1 << 6,

		/// <summary>The device is tracked as a bare hand, without any controller body.</summary>
		Handed = 1 << 7,

		Pose = Position | Rotation,
		Motion = Velocity | AngularVelocity,
		All = Pose | Motion,
	}

	/// <summary>
	/// What a hand can follow, derived from <see cref="TrackerFeatures"/>. A loader declaring both
	/// <see cref="TrackerFeatures.Skeleton"/> and <see cref="TrackerFeatures.Buttons"/> carries hand
	/// tracking on a controller (a hand-tracking accessory such as the Ultraleap UDCAP on an Index
	/// controller, or a headset tracking the hands while the controllers stay in the player's hands).
	/// </summary>
	public enum HandTrackingMode {
		/// <summary>Only a physical controller: the fingers come from its grip/trigger (simple blend).</summary>
		Controller = 0,

		/// <summary>Only a tracked hand: the fingers come from the skeleton.</summary>
		Hand = 1,

		/// <summary>Controller with hand tracking: the fingers blend both sources.</summary>
		ControllerAndHand = 2,
	}

	/// <summary>Which pose a caller wants.</summary>
	public enum TrackerPoseMode {
		/// <summary>Pose as reported by the runtime, in the tracking space of the loader.</summary>
		Raw,

		/// <summary>
		/// Calibrated pose, when the loader can produce it. A loader working in its own tracking space cannot: the
		/// offsets are measured on the avatar skeleton in world space, so nox.xr composes them itself (see
		/// <c>TrackerResolver</c>) and both modes return the raw pose from an <c>InputDevice</c> provider.
		/// </summary>
		Calibrated,
	}

	/// <summary>
	/// One tracked device exposed by a loader (HMD, controller, tracker...).
	/// <para>
	/// The tracker owns its rig slot; loaders create them, nox.xr only reads their pose.
	/// The slot is an <c>ushort</c> so a loader can expose custom values (a tracker attached to a
	/// prop, for instance) in addition to the well-known <see cref="EntityRig"/> slots.
	/// The calibration offsets published by <c>Nox.XR.Calibration</c> are composed by nox.xr, in world space,
	/// once the pose of the loader was brought to world space: a loader only reports device poses.
	/// </para>
	/// </summary>
	public interface ITracker {
		/// <summary>
		/// Rig slot served by this tracker (an <see cref="EntityRig"/> value, or a custom id), or
		/// <see cref="EntityRig.None"/> for an unassigned device.
		/// </summary>
		ushort Rig { get; }

		/// <summary>Stable identifier of the device (serial number when available, otherwise a runtime id).</summary>
		string Id { get; }

		/// <summary>Human-readable device name given by the runtime.</summary>
		string Name { get; }

		/// <summary>Device manufacturer, or an empty string when the runtime does not expose it.</summary>
		string Manufacturer { get; }

		/// <summary>Device model, or an empty string when the runtime does not expose it.</summary>
		string Model { get; }

		/// <summary>Whether the device is physically connected (it can be connected but not tracking).</summary>
		bool IsConnected { get; }

		/// <summary>Whether the device is currently tracking.</summary>
		bool IsTracking { get; }

		/// <summary>Values the tracker can provide.</summary>
		TrackerFeatures Features { get; }

		/// <summary>
		/// Reads a tracker value by key (see <see cref="TrackerKeys"/>), like <c>IBinding.Get</c> for inputs.
		/// Returns <c>default</c> when the runtime does not expose that value.
		/// </summary>
		T Get<T>(string key) where T : struct;

		/// <summary>Reads the pose in the requested mode.</summary>
		bool TryGetPose(TrackerPoseMode mode, out TrackerPose pose);

		/// <summary>
		/// Reads the hand skeleton when the device declares <see cref="TrackerFeatures.Skeleton"/>:
		/// every joint with its pose, plus the curls. Returns <c>false</c> when no hand is tracked.
		/// </summary>
		bool TryGetSkeleton(out HandSkeleton skeleton);

		/// <summary>One-line state, for diagnostics.</summary>
		string Describe();
	}
}
