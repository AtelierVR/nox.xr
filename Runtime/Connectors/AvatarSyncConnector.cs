using System.Linq;
using Autohand;
using Nox.Avatars.Hand;
using Nox.Avatars.Parameters;
using Nox.Avatars.Rigging;
using Nox.CCK;
using Nox.CCK.Players;
using Nox.CCK.XR;
using Nox.Controllers;
using UnityEngine;
using Logger = Nox.CCK.Utils.Logger;
using NoxHandType = Nox.Avatars.Hand.HandType;
using Nox.CCK.Avatars.Rigging;

namespace Nox.XR.Runtime.Connectors {
	public class AvatarSyncConnector : MonoBehaviour {
		public AutoHandPlayer player;
		public AvatarLoaderConnector avatarLoader;

		private IController _controller;
		private bool _controllerSearched;
		private IRigProvider _rigProvider;

		// ReSharper disable Unity.PerformanceAnalysis
		private void Update() {
			SynchronizeParametersAvatar();
			DriveRigParts();
		}

		/// <summary>
		/// Local counterpart of <c>RemotePhysical.Update</c>: writes the tracked parts exposed by the
		/// controller onto the avatar rig every frame, with no interpolation and no threshold.
		/// <para>
		/// The parts read here are the very values that are sent to the network, so a remote client replays
		/// exactly what this writes (theirs interpolated between packets). <c>PlayerRig.Base</c> is skipped:
		/// the local avatar's body is already driven by the rig and the player's physics, the part is only
		/// sent so remote clients know where the body is. Parts the rig does not expose (fingers on a rig
		/// limited to humanoid bones) are ignored by <see cref="RigPartDriver.Write"/>. Arms are written too:
		/// the rig parts are the arm IK targets, so grabbing only swaps the target for the grab point and
		/// the write stays harmlessly behind it.
		/// </para>
		/// </summary>
		private void DriveRigParts() {
			var rig = RigProvider?.GetRig();
			if (rig == null)
				return;

			var controller = Controller;
			if (controller == null)
				return;

			foreach (var (partId, part) in controller.GetParts()) {
				if (partId == PlayerRig.Base.ToIndex())
					continue;
				RigPartDriver.Write(rig, partId, part.GetPosition(), part.GetRotation());
			}
		}

		/// <summary>Controller exposing this rig's tracked parts.</summary>
		private IController Controller {
			get {
				if (_controllerSearched) return _controller;
				_controllerSearched = true;
				_controller = GetComponent<IController>() ?? GetComponentInParent<IController>();
				if (_controller == null)
					Logger.LogWarning(
						$"{nameof(AvatarSyncConnector)}: no {nameof(IController)} found, the rig parts are not driven.",
						this
					);
				return _controller;
			}
		}

		/// <summary>
		/// Rig provider of the current avatar, resolved on demand and re-resolved when the avatar is loaded
		/// or swapped after us (the provider is a MonoBehaviour that can be destroyed).
		/// </summary>
		private IRigProvider RigProvider {
			get {
				if (!(_rigProvider is Object known) || !known)
					_rigProvider = avatarLoader?.GetAvatar()?.Descriptor?.Anchor
						?.GetComponentInChildren<IRigProvider>(true);
				return _rigProvider;
			}
		}

		private void LateUpdate() {
			var anchor = avatarLoader?.GetAvatar()?.Descriptor?.Anchor;
			if (anchor != null && player != null) {
				var pos = anchor.transform.position;
				pos.y = player.transform.position.y;
				anchor.transform.position = pos;
			}
		}

		/// <summary>
		/// Converts the player's world-space body velocity into a reference frame
		/// aligned with the player's look direction (head forward projected onto
		/// the horizontal plane). This fixed the mismatch between the raw
		/// `player.body.linearVelocity` and the avatar's "true angular forward".
		/// </summary>
		private Vector3 GetLookVelocity() {
			var worldVelocity = player.body?.linearVelocity ?? Vector3.zero;
			Vector3 lookForward;
			if (player.headCamera != null) {
				lookForward = player.headCamera.transform.forward;
				lookForward.y = 0f;
				if (lookForward.sqrMagnitude < 1e-6f)
					lookForward = transform.forward;
			} else {
				lookForward = transform.forward;
			}
			lookForward.Normalize();
			var lookRotation = Quaternion.LookRotation(lookForward, Vector3.up);
			return Quaternion.Inverse(lookRotation) * worldVelocity;
		}

		// ReSharper disable Unity.PerformanceAnalysis
		private void SynchronizeParametersAvatar() {
			var avatar = avatarLoader?.GetAvatar();
			var parameterModule = avatar?.Descriptor
				?.GetModules<IParameterModule>()
				.FirstOrDefault();
			var handModule = avatar?.Descriptor
				?.GetModules<IHandModule>()
				.FirstOrDefault();
			var leftHand  = handModule != null ? System.Array.Find(handModule.Hands, h => h.Type == NoxHandType.Left)  : null;
			var rightHand = handModule != null ? System.Array.Find(handModule.Hands, h => h.Type == NoxHandType.Right) : null;
			if (parameterModule == null)
				return;
			var parameters = parameterModule.GetParameters();
			Vector3? localVelocity = null;
			foreach (var param in parameters) {
				var n = param.GetName();
				switch (n) {
					case "Grounded": {
						var grounded = player.IsGrounded();
						var value    = (bool)param.Get();
						if (value == grounded)
							continue;
						param.Set(grounded);
						break;
					}
					case "VelocityX": {
						var velocity = localVelocity ?? (localVelocity = GetLookVelocity()).Value;
						var value    = param.Get().ToFloat();
						if (Mathf.Approximately(value, velocity.x))
							continue;
						param.Set(velocity.x);
						break;
					}
					case "VelocityY": {
						var velocity = localVelocity ?? (localVelocity = GetLookVelocity()).Value;
						var value    = param.Get().ToFloat();
						if (Mathf.Approximately(value, velocity.y))
							continue;
						param.Set(velocity.y);
						break;
					}
					case "VelocityZ": {
						var velocity = localVelocity ?? (localVelocity = GetLookVelocity()).Value;
						var value    = param.Get().ToFloat();
						if (Mathf.Approximately(value, velocity.z))
							continue;
						param.Set(velocity.z);
						break;
					}
					case "Velocity": {
						var velocity = localVelocity ?? (localVelocity = GetLookVelocity()).Value;
						var value    = param.Get().ToVector3();
						if (value == velocity)
							continue;
						param.Set(velocity);
						break;
					}
					case "VelocityMagnitude": {
						var worldVelocity = player.body?.linearVelocity ?? Vector3.zero;
						var value         = param.Get().ToFloat();
						if (Mathf.Approximately(value, worldVelocity.magnitude))
							continue;
						param.Set(worldVelocity.magnitude);
						break;
					}
					case "tracking/head/active": {
						var active = XRInputs.HasHeadset;
						var value  = param.Get().ToBool();
						if (value == active)
							continue;
						param.Set(active);
						break;
					}
					case "tracking/head/position":
					case "tracking/head/rotation":
						// Deliberately not written: the head pose travels as the `PlayerRig.Head` part (see
						// `XRController.GetParts`), which is exactly what remote clients replay through
						// `RemotePhysical.Update`, and the local rig target is written by `DriveRigParts`.
						// Writing it a second time through these parameters only added a second writer, with a
						// different cadence, a different formula (this one used to apply the eye offset) and a
						// 1 mm threshold that held the head target in place.
						break;
					case "tracking/left_hand/active": {
						var active = XRInputs.HasHandLeft;
						var value  = param.Get().ToBool();
						if (value == active)
							continue;
						param.Set(active);
						break;
					}
					case "tracking/left_hand/position":
					case "tracking/left_hand/rotation":
					case "tracking/right_hand/position":
					case "tracking/right_hand/rotation":
						break;
					case "tracking/right_hand/active": {
						var active = XRInputs.HasHandRight;
						var value  = param.Get().ToBool();
						if (value == active)
							continue;
						param.Set(active);
						break;
					}
				}
			}

			var heightP = parameterModule.GetParameter("Height")
				?? parameterModule.GetParameter("EyeHeight");
			float maxHeight;
			if (heightP != null)
				maxHeight = heightP.Get().ToFloat();
			else if (player.headCamera)
				maxHeight = player.headCamera.transform.position.y - player.transform.position.y;
			else
				maxHeight = 1.7f;

			if (!Mathf.Approximately(player.minMaxHeight.y, maxHeight))
				player.minMaxHeight = new Vector2(player.minMaxHeight.x, maxHeight);
		}

	}
}
