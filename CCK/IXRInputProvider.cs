using UnityEngine;
using UnityEngine.Events;
using UnityEngine.XR;

namespace Nox.CCK.XR {
	/// <summary>
	/// Interface for providing XR input data to the XRInputs static class.
	/// </summary>
	public interface IXRInputProvider {
		/// <summary>
		/// Devices XR du provider : c'est lui qui sait d'où ils viennent (<see cref="InputDevices"/>,
		/// SteamVR, AutoHand…) et qui prévient quand ils apparaissent, disparaissent ou changent de
		/// configuration. Les consommateurs s'y abonnent via <see cref="XRInputs.DeviceConnected"/>.
		/// </summary>
		UnityEvent<InputDevice> DeviceConnected { get; }

		/// <summary>Device XR disparu.</summary>
		UnityEvent<InputDevice> DeviceDisconnected { get; }

		/// <summary>Configuration d'un device XR changée (rôle, modèle…).</summary>
		UnityEvent<InputDevice> DeviceConfigChanged { get; }

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