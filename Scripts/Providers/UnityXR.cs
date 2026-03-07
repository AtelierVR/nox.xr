using System.Collections.Generic;
using Nox.CCK.XR;
using UnityEngine;
using UnityEngine.XR;

namespace Nox.XR.Providers {
	public class UnityXR : IXRInputProvider {

		public bool HasDevice(XRNode node) {
			var devices = new List<InputDevice>();
			InputDevices.GetDevicesAtXRNode(node, devices);
			return devices.Count > 0;
		}

		public bool TryGetDevicePose(XRNode node, out Vector3 position, out Quaternion rotation) {
			position = Vector3.zero;
			rotation = Quaternion.identity;

			var devices = new List<InputDevice>();
			InputDevices.GetDevicesAtXRNode(node, devices);

			if (devices.Count == 0)
				return false;

			var device      = devices[0];
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