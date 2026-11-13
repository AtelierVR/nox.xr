using System.Collections.Generic;
using Nox.CCK.Entities;
using CalibrationService = Nox.XR.Calibration.Calibration;
using Nox.XR.Trackers;
using UnityEngine;
using UnityEngine.XR;

namespace Nox.XR.Calibration {
	/// <summary>
	/// Trackers exposed by an XR loader: every <see cref="InputDevice"/> of the runtime, the
	/// HMD and controllers included.
	/// <para>
	/// Devices are discovered on construction and whenever the XR runtime signals a connection or
	/// disconnection. Calibrated body parts are resolved by nox.xr's calibration; this provider
	/// only reports raw device poses.
	/// </para>
	/// </summary>
	public abstract class InputDeviceTrackerProviderBase : TrackerProviderBase {
		private readonly List<InputDevice> _devices = new();

		protected InputDeviceTrackerProviderBase() {
			InputDevices.deviceConnected += OnDeviceChanged;
			InputDevices.deviceDisconnected += OnDeviceChanged;
			Sync();
		}

		private void OnDeviceChanged(InputDevice device)
			=> Sync();

		public override void Dispose() {
			InputDevices.deviceConnected -= OnDeviceChanged;
			InputDevices.deviceDisconnected -= OnDeviceChanged;
			base.Dispose();
		}

		/// <summary>Registers every present device, unregisters the ones that disappeared.</summary>
		private void Sync() {
			_devices.Clear();
			InputDevices.GetDevices(_devices);

			var present = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
			for (var i = 0; i < _devices.Count; i++) {
				var device = _devices[i];
				var id = IdOf(device, i);
				present.Add(id);

				if (TryGet(id, out _))
					continue;

				Add(Create(device, id));
			}

			for (var i = Trackers.Count - 1; i >= 0; i--)
				if (!present.Contains(Trackers[i].Id))
					Remove(Trackers[i]);
		}

		private TrackerEntry Create(InputDevice device, string id) {
			bool ReadPose(TrackerPoseMode mode, out TrackerPose pose)
				=> TryGetPose(device, id, RigOf(device, id).ToEntityRig(), mode, out pose);

			bool ReadValue(string key, out object value)
				=> TryReadValue(device, key, out value);

			bool ReadSkeleton(out HandSkeleton skeleton)
				=> HandTrackingSource.TryGetSkeleton(IsLeft(device), out skeleton);

			var rig = RigOf(device, id);
			return new TrackerEntry(
				() => RigOf(device, id),
				FeaturesOf(device, rig.ToEntityRig()),
				ReadPose, () => IsTracked(device),
				id: id,
				name: device.name,
				manufacturer: device.manufacturer,
				model: device.name,
				readConnected: () => device.isValid,
				readValue: ReadValue,
				readSkeleton: ReadSkeleton,
				readFeatures: () => FeaturesOf(device, RigOf(device, id).ToEntityRig())
			);
		}

		private static string IdOf(InputDevice device, int index)
			=> string.IsNullOrEmpty(device.serialNumber)
				? $"{device.name}#{index}"
				: device.serialNumber;

		/// <summary>
		/// What the device can provide: pose and motion always, the buttons of a physical controller, and
		/// the hand skeleton when the runtime tracks a hand for that side.
		/// <para>
		/// The skeleton is served by the runtime subsystem (see <see cref="HandTrackingSource"/>), not by
		/// the device itself: a controller carrying a hand-tracking accessory therefore declares
		/// <see cref="TrackerFeatures.Skeleton"/> + <see cref="TrackerFeatures.Buttons"/>
		/// (<see cref="HandTrackingMode.ControllerAndHand"/>), which is what makes nox.xr blend the two.
		/// </para>
		/// </summary>
		private static TrackerFeatures FeaturesOf(InputDevice device, EntityRig rig) {
			var features = TrackerFeatures.All;

			if (CalibrationService.IsCalibratable(rig))
				features |= TrackerFeatures.Calibrated;

			if (device.characteristics.HasFlag(InputDeviceCharacteristics.Controller)
				|| device.TryGetFeatureValue(CommonUsages.triggerButton, out _)
				|| device.TryGetFeatureValue(CommonUsages.gripButton, out _))
				features |= TrackerFeatures.Buttons;

			var handDevice = device.characteristics.HasFlag(InputDeviceCharacteristics.HandTracking)
							 || device.TryGetFeatureValue(CommonUsages.handData, out _);

			if (handDevice)
				features |= TrackerFeatures.Handed;

			if ((handDevice || rig is EntityRig.LeftHand or EntityRig.RightHand) && HandTrackingSource.IsAvailable)
				features |= TrackerFeatures.Skeleton;

			return features;
		}

		/// <summary>Whether the device is the left hand/controller, from the runtime characteristics.</summary>
		private static bool IsLeft(InputDevice device)
			=> device.characteristics.HasFlag(InputDeviceCharacteristics.Left);

		/// <summary>
		/// Rig slot currently driven by the device. HMD and hands are fixed by the device
		/// characteristics and never calibrated; a standalone tracker becomes a body part once
		/// calibration assigns it.
		/// </summary>
		private static ushort RigOf(InputDevice device, string id) {
			var characteristics = device.characteristics;

			if (characteristics.HasFlag(InputDeviceCharacteristics.HeadMounted))
				return EntityRig.Head.ToIndex();

			if (characteristics.HasFlag(InputDeviceCharacteristics.Left))
				return EntityRig.LeftHand.ToIndex();

			if (characteristics.HasFlag(InputDeviceCharacteristics.Right))
				return EntityRig.RightHand.ToIndex();

			if (CalibrationService.TryGetRig(id, out var calibrated) && CalibrationService.IsCalibratable(calibrated))
				return calibrated.ToIndex();

			return EntityRig.None.ToIndex();
		}

		private bool TryGetPose(InputDevice device, string id, EntityRig rig, TrackerPoseMode mode, out TrackerPose pose) {
			pose = default;

			if (!IsTracked(device)
				|| !device.TryGetFeatureValue(CommonUsages.devicePosition, out var position)
				|| !device.TryGetFeatureValue(CommonUsages.deviceRotation, out var rotation))
				return false;

			device.TryGetFeatureValue(CommonUsages.deviceVelocity, out var velocity);
			device.TryGetFeatureValue(CommonUsages.deviceAngularVelocity, out var angularVelocity);

			// Calibrated mode applies the offset published by nox.xr's calibration, when one exists.
			if (mode == TrackerPoseMode.Calibrated && CalibrationService.IsCalibratable(rig) && CalibrationService.TryGetBinding(rig, out var binding)) {
				position += CalibrationService.GetOffset(binding, rotation, 1f);
				rotation *= binding.OffsetRotation;
			}

			pose = new TrackerPose {
				Id = id,
				Position = position,
				Rotation = rotation,
				Velocity = velocity,
				AngularVelocity = angularVelocity
			};
			return true;
		}

		private static bool IsTracked(InputDevice device) {
			if (!device.isValid)
				return false;

			if (device.TryGetFeatureValue(CommonUsages.isTracked, out var tracked))
				return tracked;

			if (device.TryGetFeatureValue(CommonUsages.trackingState, out InputTrackingState state))
				return state != InputTrackingState.None;

			return true;
		}

		private static bool TryReadValue(InputDevice device, string key, out object value) {
			value = null;

			switch (key) {
				case TrackerKeys.Hand:
					value = HandTrackingSource.TryGetCurls(IsLeft(device), out _);
					return true;
				case TrackerKeys.HandThumb:
				case TrackerKeys.HandIndex:
				case TrackerKeys.HandMiddle:
				case TrackerKeys.HandRing:
				case TrackerKeys.HandLittle:
				case TrackerKeys.HandPinch:
					if (!HandTrackingSource.TryGetCurls(IsLeft(device), out var curls))
						return false;

					value = key switch {
						TrackerKeys.HandThumb => curls.Thumb,
						TrackerKeys.HandIndex => curls.Index,
						TrackerKeys.HandMiddle => curls.Middle,
						TrackerKeys.HandRing => curls.Ring,
						TrackerKeys.HandLittle => curls.Little,
						_ => curls.Pinch
					};
					return true;
				case TrackerKeys.BatteryLevel:
					if (device.TryGetFeatureValue(CommonUsages.batteryLevel, out float battery)) {
						value = battery;
						return true;
					}

					return false;
				case TrackerKeys.IsTracked:
					if (device.TryGetFeatureValue(CommonUsages.isTracked, out bool tracked)) {
						value = tracked;
						return true;
					}

					return false;
				case TrackerKeys.TrackingState:
					if (device.TryGetFeatureValue(CommonUsages.trackingState, out InputTrackingState state)) {
						value = state;
						return true;
					}

					return false;
				case TrackerKeys.PrimaryButton:
					if (device.TryGetFeatureValue(CommonUsages.primaryButton, out bool primaryButton)) {
						value = primaryButton;
						return true;
					}

					return false;
				case TrackerKeys.PrimaryTouch:
					if (device.TryGetFeatureValue(CommonUsages.primaryTouch, out bool primaryTouch)) {
						value = primaryTouch;
						return true;
					}

					return false;
				case TrackerKeys.SecondaryButton:
					if (device.TryGetFeatureValue(CommonUsages.secondaryButton, out bool secondaryButton)) {
						value = secondaryButton;
						return true;
					}

					return false;
				case TrackerKeys.SecondaryTouch:
					if (device.TryGetFeatureValue(CommonUsages.secondaryTouch, out bool secondaryTouch)) {
						value = secondaryTouch;
						return true;
					}

					return false;
				case TrackerKeys.Primary2DAxis:
					if (device.TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 primary2DAxis)) {
						value = primary2DAxis;
						return true;
					}

					return false;
				case TrackerKeys.Secondary2DAxis:
					if (device.TryGetFeatureValue(CommonUsages.secondary2DAxis, out Vector2 secondary2DAxis)) {
						value = secondary2DAxis;
						return true;
					}

					return false;
				case TrackerKeys.Trigger:
					if (device.TryGetFeatureValue(CommonUsages.trigger, out float trigger)) {
						value = trigger;
						return true;
					}

					return false;
				case TrackerKeys.TriggerButton:
					if (device.TryGetFeatureValue(CommonUsages.triggerButton, out bool triggerButton)) {
						value = triggerButton;
						return true;
					}

					return false;
				case TrackerKeys.Grip:
					if (device.TryGetFeatureValue(CommonUsages.grip, out float grip)) {
						value = grip;
						return true;
					}

					return false;
				case TrackerKeys.GripButton:
					if (device.TryGetFeatureValue(CommonUsages.gripButton, out bool gripButton)) {
						value = gripButton;
						return true;
					}

					return false;
				case TrackerKeys.MenuButton:
					if (device.TryGetFeatureValue(CommonUsages.menuButton, out bool menuButton)) {
						value = menuButton;
						return true;
					}

					return false;
				case TrackerKeys.UserPresence:
					if (device.TryGetFeatureValue(CommonUsages.userPresence, out bool presence)) {
						value = presence;
						return true;
					}

					return false;
				case TrackerKeys.Primary2DAxisClick:
					if (device.TryGetFeatureValue(CommonUsages.primary2DAxisClick, out bool primaryAxisClick)) {
						value = primaryAxisClick;
						return true;
					}

					return false;
				case TrackerKeys.Primary2DAxisTouch:
					if (device.TryGetFeatureValue(CommonUsages.primary2DAxisTouch, out bool primaryAxisTouch)) {
						value = primaryAxisTouch;
						return true;
					}

					return false;
				case TrackerKeys.Secondary2DAxisClick:
					if (device.TryGetFeatureValue(CommonUsages.secondary2DAxisClick, out bool secondaryAxisClick)) {
						value = secondaryAxisClick;
						return true;
					}

					return false;
				case TrackerKeys.Secondary2DAxisTouch:
					if (device.TryGetFeatureValue(CommonUsages.secondary2DAxisTouch, out bool secondaryAxisTouch)) {
						value = secondaryAxisTouch;
						return true;
					}

					return false;
				case TrackerKeys.Acceleration:
					if (device.TryGetFeatureValue(CommonUsages.deviceAcceleration, out Vector3 acceleration)) {
						value = acceleration;
						return true;
					}

					return false;
				case TrackerKeys.AngularAcceleration:
					if (device.TryGetFeatureValue(CommonUsages.deviceAngularAcceleration, out Vector3 angularAcceleration)) {
						value = angularAcceleration;
						return true;
					}

					return false;
				case TrackerKeys.LeftEyePosition:
					if (device.TryGetFeatureValue(CommonUsages.leftEyePosition, out Vector3 leftEyePosition)) {
						value = leftEyePosition;
						return true;
					}

					return false;
				case TrackerKeys.LeftEyeRotation:
					if (device.TryGetFeatureValue(CommonUsages.leftEyeRotation, out Quaternion leftEyeRotation)) {
						value = leftEyeRotation;
						return true;
					}

					return false;
				case TrackerKeys.RightEyePosition:
					if (device.TryGetFeatureValue(CommonUsages.rightEyePosition, out Vector3 rightEyePosition)) {
						value = rightEyePosition;
						return true;
					}

					return false;
				case TrackerKeys.RightEyeRotation:
					if (device.TryGetFeatureValue(CommonUsages.rightEyeRotation, out Quaternion rightEyeRotation)) {
						value = rightEyeRotation;
						return true;
					}

					return false;
				case TrackerKeys.CenterEyePosition:
					if (device.TryGetFeatureValue(CommonUsages.centerEyePosition, out Vector3 centerEyePosition)) {
						value = centerEyePosition;
						return true;
					}

					return false;
				case TrackerKeys.CenterEyeRotation:
					if (device.TryGetFeatureValue(CommonUsages.centerEyeRotation, out Quaternion centerEyeRotation)) {
						value = centerEyeRotation;
						return true;
					}

					return false;
				case TrackerKeys.Position:
					if (device.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 position)) {
						value = position;
						return true;
					}

					return false;
				case TrackerKeys.Rotation:
					if (device.TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion rotation)) {
						value = rotation;
						return true;
					}

					return false;
				case TrackerKeys.Velocity:
					if (device.TryGetFeatureValue(CommonUsages.deviceVelocity, out Vector3 velocity)) {
						value = velocity;
						return true;
					}

					return false;
				case TrackerKeys.AngularVelocity:
					if (device.TryGetFeatureValue(CommonUsages.deviceAngularVelocity, out Vector3 angularVelocity)) {
						value = angularVelocity;
						return true;
					}

					return false;
				default:
					// Unknown key: let the runtime answer it as a custom feature usage.
					if (device.TryGetFeatureValue(new InputFeatureUsage<bool>(key), out bool customBool)) {
						value = customBool;
						return true;
					}

					if (device.TryGetFeatureValue(new InputFeatureUsage<float>(key), out float customFloat)) {
						value = customFloat;
						return true;
					}

					if (device.TryGetFeatureValue(new InputFeatureUsage<Vector2>(key), out Vector2 customVector2)) {
						value = customVector2;
						return true;
					}

					if (device.TryGetFeatureValue(new InputFeatureUsage<Vector3>(key), out Vector3 customVector3)) {
						value = customVector3;
						return true;
					}

					if (device.TryGetFeatureValue(new InputFeatureUsage<Quaternion>(key), out Quaternion customQuaternion)) {
						value = customQuaternion;
						return true;
					}

					return false;
			}
		}

	}
}
