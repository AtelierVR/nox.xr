using UnityEngine;
using UnityEngine.XR;

namespace Nox.CCK.XR {
	/// <summary>
	/// Interface for providing XR input data to the XRInputs static class.
	/// </summary>
	public interface IXRInputProvider {
		/// <summary>
		/// Checks if a device of the specified XRNode type is currently connected.
		/// </summary>
		/// <param name="node"></param>
		/// <returns></returns>
		public bool HasDevice(XRNode node);

		/// <summary>
		/// Tries to get the current position and rotation of a device of the specified XRNode type.
		/// </summary>
		/// <param name="node"></param>
		/// <param name="position"></param>
		/// <param name="rotation"></param>
		/// <returns></returns>
		public bool TryGetDevicePose(XRNode node, out Vector3 position, out Quaternion rotation);
	}
}