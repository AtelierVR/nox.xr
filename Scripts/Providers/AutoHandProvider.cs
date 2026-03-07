#if HAS_AUTOHAND

using Autohand;
using Nox.CCK.XR;
using UnityEngine;
using UnityEngine.XR;

namespace Nox.XR.Providers {
	public class AutoHandProvider : IXRInputProvider {
		public static AutoHandPlayer Player
			=> AutoHandPlayer.Instance;

		public bool HasDevice(XRNode node)
			=> node switch {
				XRNode.Head      => Player?.headCamera,
				XRNode.LeftHand  => Player?.handLeft,
				XRNode.RightHand => Player?.handRight,
				_                => false
			};

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
				case XRNode.GameController:
				case XRNode.TrackingReference:
				case XRNode.HardwareTracker:
				default:
					return false;
			}
		}
	}
}

#endif