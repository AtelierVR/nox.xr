using UnityEngine;
using UnityEngine.XR;

namespace Nox.CCK.XR {
	/// <summary>
	/// Static class providing access to XR input data through a common provider interface.
	/// </summary>
	public static class XRInputs {
		/// <summary>
		/// The current provider for XR input data.
		/// This is set by the XRController when it initializes.
		/// </summary>
		public static IXRInputProvider Provider;

		/// <summary>
		/// Checks if a headset device is currently connected.
		/// </summary>
		public static bool HasHeadset
			=> HasDevice(XRNode.Head);

		/// <summary>
		/// Checks if a right hand controller is currently connected.
		/// </summary>
		public static bool HasHandRight
			=> HasDevice(XRNode.RightHand);

		/// <summary>
		/// Checks if a left hand controller is currently connected.
		/// </summary>
		public static bool HasHandLeft
			=> HasDevice(XRNode.LeftHand);

		/// <summary>
		/// Checks if either a left or right hand controller is currently connected.
		/// </summary>
		public static bool HasHand
			=> HasHandRight || HasHandLeft;

		/// <summary>
		/// Checks if a device of the specified XRNode type is currently connected.
		/// </summary>
		/// <param name="node"></param>
		/// <returns></returns>
		public static bool HasDevice(XRNode node)
			=> Provider?.HasDevice(node) ?? false;

		/// <summary>
		/// Tries to get the current position and rotation of the headset.
		/// </summary>
		/// <param name="position"></param>
		/// <param name="rotation"></param>
		/// <returns></returns>
		public static bool GetHeadsetPose(out Vector3 position, out Quaternion rotation)
			=> GetDevicePose(XRNode.Head, out position, out rotation);

		/// <summary>
		/// Tries to get the current position and rotation of either hand controller.
		/// </summary>
		/// <param name="position"></param>
		/// <param name="rotation"></param>
		/// <returns></returns>
		public static bool GetHandPose(out Vector3 position, out Quaternion rotation)
			=> GetLeftHandPose(out position, out rotation) || GetRightHandPose(out position, out rotation);

		/// <summary>
		/// Tries to get the current position and rotation of the left hand controller.
		/// </summary>
		/// <param name="position"></param>
		/// <param name="rotation"></param>
		/// <returns></returns>
		public static bool GetLeftHandPose(out Vector3 position, out Quaternion rotation)
			=> GetDevicePose(XRNode.LeftHand, out position, out rotation);

		/// <summary>
		/// Tries to get the current position and rotation of the right hand controller.
		/// </summary>
		/// <param name="position"></param>
		/// <param name="rotation"></param>
		/// <returns></returns>
		public static bool GetRightHandPose(out Vector3 position, out Quaternion rotation)
			=> GetDevicePose(XRNode.RightHand, out position, out rotation);

		/// <summary>
		/// Tries to get the current position and rotation of a device of the specified XRNode type.
		/// </summary>
		/// <param name="node"></param>
		/// <param name="position"></param>
		/// <param name="rotation"></param>
		/// <returns></returns>
		public static bool GetDevicePose(XRNode node, out Vector3 position, out Quaternion rotation) {
			if (Provider != null)
				return Provider.TryGetDevicePose(node, out position, out rotation);
			position = Vector3.zero;
			rotation = Quaternion.identity;
			return false;
		}
	}
}