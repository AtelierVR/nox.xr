using UnityEngine;
using UnityEngine.Events;
using UnityEngine.XR;

namespace Nox.CCK.XR {
	/// <summary>
	/// Static class providing access to XR input data through a common provider interface.
	/// </summary>
	public static class XRInputs {
		private static IXRInputProvider _provider;
		private static IXRInputProvider _defaultProvider;
		private static IXRInputProvider _listening;

		/// <summary>
		/// The current provider for XR input data.
		/// This is set by the XRController when it initializes.
		/// <para>
		/// L'affectation relie aussi les événements de devices de ce provider : un abonné à
		/// <see cref="DeviceConnected"/> n'a donc jamais à suivre le changement de provider.
		/// </para>
		/// </summary>
		public static IXRInputProvider Provider {
			get => _provider;
			set {
				_provider = value;
				Rewire();
			}
		}

		/// <summary>
		/// Provider de repli, installé par nox.xr au démarrage.
		/// <para>
		/// Sans lui, <see cref="Provider"/> reste <c>null</c> tant qu'un provider spécifique
		/// (AutoHand, ...) n'a pas démarré, et <see cref="HasHeadset"/> répondait donc
		/// "pas de casque" même avec un casque connecté.
		/// </para>
		/// </summary>
		public static IXRInputProvider DefaultProvider {
			get => _defaultProvider;
			set {
				_defaultProvider = value;
				Rewire();
			}
		}

		/// <summary>
		/// Provider effectivement interrogé : le provider courant s'il existe, sinon le repli.
		/// </summary>
		public static IXRInputProvider ActiveProvider
			=> _provider ?? _defaultProvider;

		/// <summary>Device XR apparu, relayé par le provider actif.</summary>
		public static readonly UnityEvent<InputDevice> DeviceConnected = new();

		/// <summary>Device XR disparu, relayé par le provider actif.</summary>
		public static readonly UnityEvent<InputDevice> DeviceDisconnected = new();

		/// <summary>Configuration d'un device XR changée, relayée par le provider actif.</summary>
		public static readonly UnityEvent<InputDevice> DeviceConfigChanged = new();

		/// <summary>
		/// Relie les événements du provider effectivement interrogé (et débranche le précédent).
		/// </summary>
		private static void Rewire() {
			var next = ActiveProvider;
			if (ReferenceEquals(next, _listening))
				return;

			if (_listening != null) {
				_listening.DeviceConnected.RemoveListener(ForwardConnected);
				_listening.DeviceDisconnected.RemoveListener(ForwardDisconnected);
				_listening.DeviceConfigChanged.RemoveListener(ForwardConfigChanged);
			}

			_listening = next;

			if (next == null)
				return;

			next.DeviceConnected.AddListener(ForwardConnected);
			next.DeviceDisconnected.AddListener(ForwardDisconnected);
			next.DeviceConfigChanged.AddListener(ForwardConfigChanged);
		}

		private static void ForwardConnected(InputDevice device)
			=> DeviceConnected.Invoke(device);

		private static void ForwardDisconnected(InputDevice device)
			=> DeviceDisconnected.Invoke(device);

		private static void ForwardConfigChanged(InputDevice device)
			=> DeviceConfigChanged.Invoke(device);

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
			=> ActiveProvider?.HasDevice(node) ?? false;

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
			if (ActiveProvider != null)
				return ActiveProvider.TryGetDevicePose(node, out position, out rotation);
			position = Vector3.zero;
			rotation = Quaternion.identity;
			return false;
		}
	}
}