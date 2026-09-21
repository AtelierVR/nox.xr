#if HAS_AUTOHAND

using System.Collections.Generic;
using Autohand;
using Nox.CCK.XR;
using UnityEngine;
using UnityEngine.XR;

namespace Nox.XR.Runtime.Providers {
	public class AutoHandProvider : IXRInputProvider {
		private static readonly List<InputDevice> _devices = new();

		public static AutoHandPlayer Player
			=> AutoHandPlayer.Instance;

		public bool HasDevice(XRNode node)
			=> node switch {
				XRNode.Head            => HasTracked(XRNode.Head),
				XRNode.LeftHand        => HasTracked(XRNode.LeftHand),
				XRNode.RightHand       => HasTracked(XRNode.RightHand),
				XRNode.HardwareTracker => GetTrackers().Count > 0,
				_                      => false
			};

		/// <summary>
		/// Présence <b>réelle</b> d'un device suivi sur ce node.
		/// <para>
		/// AutoHand expose toujours ses mains (<c>Player.handLeft/handRight</c>) et sa caméra :
		/// s'en servir comme test de présence rendait <c>XRInputs.HasHandLeft/HasHandRight</c>
		/// toujours vrais. L'avatar gardait donc <c>tracking/*_hand/active</c> actif, les bras
		/// suivaient les contrôleurs non-trackés — posés à l'origine du rig — au lieu de jouer
		/// l'animation de locomotion.
		/// </para>
		/// </summary>
		private static bool HasTracked(XRNode node) {
			_devices.Clear();
			InputDevices.GetDevicesAtXRNode(node, _devices);
			return _devices.Count > 0;
		}

		public bool TryGetDevicePose(XRNode node, out Vector3 position, out Quaternion rotation) {
			position = Vector3.zero;
			rotation = Quaternion.identity;

			if (!HasDevice(node))
				return false;

			switch (node) {
				case XRNode.LeftEye:
				case XRNode.RightEye:
				case XRNode.CenterEye:
				case XRNode.Head:
					position = Player.headCamera.transform.position;
					rotation = Player.headCamera.transform.rotation;
					return true;
				case XRNode.LeftHand:
					position = Player.handLeft.transform.position;
					rotation = Player.handLeft.transform.rotation;
					return true;
				case XRNode.RightHand:
					position = Player.handRight.transform.position;
					rotation = Player.handRight.transform.rotation;
					return true;
				case XRNode.HardwareTracker: {
					var trackers = GetTrackers();
					if (trackers.Count == 0) return false;
					var device = trackers[0];
					var hasPos = device.TryGetFeatureValue(CommonUsages.devicePosition, out position);
					var hasRot = device.TryGetFeatureValue(CommonUsages.deviceRotation, out rotation);
					if (XROriginSetter.GlobalOrigin) {
						position = XROriginSetter.GlobalOrigin.transform.TransformPoint(position);
						rotation = XROriginSetter.GlobalOrigin.transform.rotation * rotation;
					}
					return hasPos && hasRot;
				}
				case XRNode.GameController:
				case XRNode.TrackingReference:
				default:
					return false;
			}
		}

		/// <summary>
		/// Get all connected tracker devices (non-HMD, non-controller tracked devices).
		/// </summary>
		private static List<InputDevice> GetTrackers() {
			var devices = new List<InputDevice>();
			InputDevices.GetDevices(devices);
			return devices.FindAll(
				d => d.characteristics.HasFlag(InputDeviceCharacteristics.TrackedDevice)
					&& !d.characteristics.HasFlag(InputDeviceCharacteristics.HeadMounted)
					&& !d.characteristics.HasFlag(InputDeviceCharacteristics.Controller)
			);
		}
	}
}

#endif