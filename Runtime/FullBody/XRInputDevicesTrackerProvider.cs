using System.Collections.Generic;
using Nox.CCK.XR;
using Nox.XR.Trackers;
using UnityEngine.XR;

namespace Nox.XR.Runtime.FullBody {
	/// <summary>
	/// Trackers of the active XR runtime, as <see cref="InputDevice"/> exposes them (any runtime publishing
	/// generic trackers, OpenXR included).
	/// <para>
	/// This is the generic fallback of nox.xr: it depends on no runtime API. It also carries the tracker
	/// filter (<see cref="IsCandidate"/>), reused by the diagnostics.
	/// </para>
	/// </summary>
	public sealed class XRInputDevicesTrackerProvider : ITrackerProvider {
		/// <summary>Shared instance (the provider is stateless apart from its buffers).</summary>
		public static readonly XRInputDevicesTrackerProvider Instance = new();

		private readonly List<InputDevice> _devices = new();
		private readonly List<InputDevice> _hardwareTrackers = new();

		private XRInputDevicesTrackerProvider() { }

		public string Id
			=> "xr";

		public bool IsAvailable
			=> XRInputs.HasHeadset;

		public string Describe() {
			_hardwareTrackers.Clear();
			InputDevices.GetDevicesAtXRNode(XRNode.HardwareTracker, _hardwareTrackers);

			return $"XR runtime: {_hardwareTrackers.Count} device(s) exposed on XRNode.HardwareTracker";
		}

		/// <summary>
		/// Whether <paramref name="device"/> can be used as a full-body tracker, and why not when it
		/// cannot.
		/// </summary>
		/// <param name="excludeHanded">Skip devices tracked as left/right hands.</param>
		/// <param name="excludeControllers">
		/// Skip devices flagged <see cref="InputDeviceCharacteristics.Controller"/>. Some runtimes
		/// flag a Vive tracker as a controller when a role is assigned to it; set this to
		/// <c>false</c> to still pick those up.
		/// </param>
		/// <param name="reason"><c>null</c> when the device is usable, otherwise the rejection reason.</param>
		public static bool IsCandidate(InputDevice device, bool excludeHanded, bool excludeControllers, out string reason) {
			var characteristics = device.characteristics;

			if (!characteristics.HasFlag(InputDeviceCharacteristics.TrackedDevice)) {
				reason = "not a TrackedDevice";
				return false;
			}

			if (characteristics.HasFlag(InputDeviceCharacteristics.HeadMounted)) {
				reason = "head-mounted (HMD)";
				return false;
			}

			if (excludeControllers && characteristics.HasFlag(InputDeviceCharacteristics.Controller)) {
				reason = "flagged Controller (the runtime assigned it a controller role)";
				return false;
			}

			if (excludeHanded && (characteristics.HasFlag(InputDeviceCharacteristics.Left) || characteristics.HasFlag(InputDeviceCharacteristics.Right))) {
				reason = "handed (Left/Right): currently used as a hand";
				return false;
			}

			if (!device.isValid) {
				reason = "device is invalid";
				return false;
			}

			if (device.TryGetFeatureValue(CommonUsages.isTracked, out var tracked) && !tracked) {
				reason = "not tracked (isTracked = false)";
				return false;
			}

			if (!device.TryGetFeatureValue(CommonUsages.devicePosition, out _)) {
				reason = "no devicePosition feature";
				return false;
			}

			if (!device.TryGetFeatureValue(CommonUsages.deviceRotation, out _)) {
				reason = "no deviceRotation feature";
				return false;
			}

			reason = null;
			return true;
		}

		/// <summary>Same as <see cref="IsCandidate(InputDevice, bool, bool, out string)"/> with controllers excluded.</summary>
		public static bool IsCandidate(InputDevice device, bool excludeHanded, out string reason)
			=> IsCandidate(device, excludeHanded, true, out reason);

		public void Get(List<TrackerPose> into, bool excludeHanded, bool excludeControllers) {
			_devices.Clear();
			InputDevices.GetDevices(_devices);

			for (var i = 0; i < _devices.Count; i++) {
				var device = _devices[i];
				if (!IsCandidate(device, excludeHanded, excludeControllers, out _))
					continue;

				device.TryGetFeatureValue(CommonUsages.devicePosition, out var position);
				device.TryGetFeatureValue(CommonUsages.deviceRotation, out var rotation);

				// Velocities travel with the pose: a part carries them and the remote side uses them (physics,
				// interpolation). Not every runtime exposes them, hence the guarded reads (0 when missing).
				device.TryGetFeatureValue(CommonUsages.deviceVelocity, out var velocity);
				device.TryGetFeatureValue(CommonUsages.deviceAngularVelocity, out var angularVelocity);

				// Tracking-space poses: nox.xr applies the XR origin to every provider.
				into.Add(new TrackerPose {
					Id              = string.IsNullOrEmpty(device.serialNumber) ? $"{device.name}#{i}" : device.serialNumber,
					Position        = position,
					Rotation        = rotation,
					Velocity        = velocity,
					AngularVelocity = angularVelocity
				});
			}
		}
	}
}
