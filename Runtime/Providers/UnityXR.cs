using System.Collections.Generic;
using Nox.CCK.XR;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.XR;

namespace Nox.XR.Runtime.Providers {
	public class UnityXR : IXRInputProvider {
		/// <summary>Événements de devices du runtime XR (le provider est leur seule source).</summary>
		public readonly XRDeviceEvents Devices = new();

		/// <summary>
		/// Reused device buffer: the head pose is now read several times per frame (early, late and right
		/// before rendering), and <see cref="InputDevices.GetDevicesAtXRNode"/> would allocate a list on
		/// every call. Reads happen on the main thread only.
		/// </summary>
		private static readonly List<InputDevice> DeviceBuffer = new();

		public UnityEvent<InputDevice> DeviceConnected
			=> Devices.Connected;

		public UnityEvent<InputDevice> DeviceDisconnected
			=> Devices.Disconnected;

		public UnityEvent<InputDevice> DeviceConfigChanged
			=> Devices.ConfigChanged;

		public bool HasDevice(XRNode node) {
			DeviceBuffer.Clear();
			InputDevices.GetDevicesAtXRNode(node, DeviceBuffer);
			return DeviceBuffer.Count > 0;
		}

		public bool TryGetDevicePose(XRNode node, out Vector3 position, out Quaternion rotation) {
			position = Vector3.zero;
			rotation = Quaternion.identity;

			DeviceBuffer.Clear();
			InputDevices.GetDevicesAtXRNode(node, DeviceBuffer);

			if (DeviceBuffer.Count == 0)
				return false;

			var device      = DeviceBuffer[0];
			var hasPosition = device.TryGetFeatureValue(CommonUsages.devicePosition, out position);
			var hasRotation = device.TryGetFeatureValue(CommonUsages.deviceRotation, out rotation);

			if (!XROriginSetter.GlobalOrigin)
				return hasPosition && hasRotation;

			position = XROriginSetter.GlobalOrigin.transform.TransformPoint(position);
			rotation = XROriginSetter.GlobalOrigin.transform.rotation * rotation;

			return hasPosition && hasRotation;
		}
	}
}