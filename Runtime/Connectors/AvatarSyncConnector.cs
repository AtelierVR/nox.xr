using System.Linq;
using Autohand;
using Nox.Avatars.Hand;
using Nox.Avatars.Parameters;
using Nox.Avatars.Rigging;
using Nox.Avatars.Scale;
using Nox.CCK;
using Nox.CCK.Players;
using Nox.CCK.XR;
using Nox.Controllers;
using UnityEngine;
using Logger = Nox.CCK.Utils.Logger;
using NoxHandType = Nox.Avatars.Hand.HandType;
using Nox.CCK.Avatars.Rigging;
using Nox.XR.Runtime.Settings;

namespace Nox.XR.Runtime.Connectors {
	public class AvatarSyncConnector : MonoBehaviour {
		public AutoHandPlayer player;
		public AvatarLoaderConnector avatarLoader;

		private IController _controller;
		private bool _controllerSearched;
		private XRController _xr;
		private bool _xrSearched;
		private IRigProvider _rigProvider;

		// ReSharper disable Unity.PerformanceAnalysis
		private void Update() {
			SynchronizeParametersAvatar();
			DriveRigParts();
		}

		/// <summary>
		/// Writes the tracked parts exposed by the controller onto the local rig every frame, with no
		/// interpolation and no threshold. These are the values sent to the network, so a remote client replays
		/// exactly what is written here. <c>PlayerRig.Base</c> is skipped: the local body is already driven by
		/// the rig and the player's physics.
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

		/// <summary>XR proxy of this avatar, holding the serialized rig node references.</summary>
		private XRController Xr {
			get {
				if (_xrSearched)
					return _xr;

				_xrSearched = true;
				_xr = GetComponentInParent<XRController>();
				if (!_xr)
					Logger.LogWarning(
						$"{nameof(AvatarSyncConnector)}: no {nameof(XRController)} above, the player pose is not fitted to the avatar.",
						this
					);

				return _xr;
			}
		}

		private void LateUpdate() {
			RescaleTrackers();
			GroundAvatar();
		}

		/// <summary>
		/// Scales the <c>trackers</c> node so the player's real pose matches the avatar's proportions: the
		/// factor is the avatar's height divided by the player's (<see cref="RealHeightSetting"/>). 1 means the
		/// avatar is the player's size.
		/// </summary>
		private void RescaleTrackers() {
			var trackers = Xr ? Xr.trackers : null;
			if (!trackers)
				return;

			var avatarHeight = AvatarHeight();
			var realHeight   = RealHeightSetting.Value;
			var factor       = avatarHeight > 0.1f && realHeight > 0.1f ? avatarHeight / realHeight : 1f;

			if (Mathf.Approximately(trackers.localScale.y, factor))
				return;

			trackers.localScale = new Vector3(factor, factor, factor);
			Logger.LogDebug($"Trackers ×{factor:F3} (avatar {avatarHeight:F2} m / player {realHeight:F2} m).", this);
		}

		/// <summary>
		/// Keeps the avatar on the ground: the AutoHandPlayer moves its container vertically (view height,
		/// crouching) and the avatar inherits it. That vertical component is cancelled on the
		/// <c>avatarContainer</c> node, which still follows the container in X/Z and rotation.
		/// </summary>
		private void GroundAvatar() {
			var avatar    = Xr ? Xr.avatarContainer : null;
			var container = player ? player.trackingContainer : null;
			if (!avatar || !container || avatar == container)
				return;

			var offset = -player.heightOffset;
			var local  = avatar.localPosition;
			if (Mathf.Approximately(local.y, offset))
				return;

			avatar.localPosition = new Vector3(local.x, offset, local.z);
		}

		/// <summary>
		/// Real (world) height of the avatar in metres, from its scale module. The value is measured on the
		/// model's rest pose, so it does not follow the animation or the trackers.
		/// </summary>
		private float AvatarHeight()
			=> avatarLoader?.GetAvatar()?.Descriptor
				?.GetModules<IScaleAvatarModule>()
				.FirstOrDefault()?.Height ?? 0f;

		/// <summary>
		/// Converts the player's world body velocity into a frame aligned with the look direction (head forward
		/// projected on the horizontal plane).
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
				var n = param.Name;
				switch (n) {
					case "Grounded": {
						var grounded = player.IsGrounded();
						var value    = (bool)param.Value;
						if (value == grounded)
							continue;
						param.Value = grounded;
						break;
					}
					case "VelocityX": {
						var velocity = localVelocity ?? (localVelocity = GetLookVelocity()).Value;
						var value    = param.Value.ToFloat();
						if (Mathf.Approximately(value, velocity.x))
							continue;
						param.Value = velocity.x;
						break;
					}
					case "VelocityY": {
						var velocity = localVelocity ?? (localVelocity = GetLookVelocity()).Value;
						var value    = param.Value.ToFloat();
						if (Mathf.Approximately(value, velocity.y))
							continue;
						param.Value = velocity.y;
						break;
					}
					case "VelocityZ": {
						var velocity = localVelocity ?? (localVelocity = GetLookVelocity()).Value;
						var value    = param.Value.ToFloat();
						if (Mathf.Approximately(value, velocity.z))
							continue;
						param.Value = velocity.z;
						break;
					}
					case "Velocity": {
						var velocity = localVelocity ?? (localVelocity = GetLookVelocity()).Value;
						var value    = param.Value.ToVector3();
						if (value == velocity)
							continue;
						param.Value = velocity;
						break;
					}
					case "VelocityMagnitude": {
						var worldVelocity = player.body?.linearVelocity ?? Vector3.zero;
						var value         = param.Value.ToFloat();
						if (Mathf.Approximately(value, worldVelocity.magnitude))
							continue;
						param.Value = worldVelocity.magnitude;
						break;
					}
					case "tracking/head/active": {
						var active = XRInputs.HasHeadset;
						var value  = param.Value.ToBool();
						if (value == active)
							continue;
						param.Value = active;
						break;
					}
					case "tracking/head/position":
					case "tracking/head/rotation":
						// The head pose travels as the `PlayerRig.Head` part (see `XRController.GetParts`).
						break;
					case "tracking/left_hand/active": {
						var active = XRInputs.HasHandLeft;
						var value  = param.Value.ToBool();
						if (value == active)
							continue;
						param.Value = active;
						break;
					}
					case "tracking/left_hand/position":
					case "tracking/left_hand/rotation":
					case "tracking/right_hand/position":
					case "tracking/right_hand/rotation":
						break;
					case "tracking/right_hand/active": {
						var active = XRInputs.HasHandRight;
						var value  = param.Value.ToBool();
						if (value == active)
							continue;
						param.Value = active;
						break;
					}
				}
			}

			// AutoHand needs the player's height as a bound; the pose scaling happens on the `trackers` node
			// (see RescaleTrackers).
			var avatarHeight = AvatarHeight();
			var maxHeight = avatarHeight > 0.1f
				? avatarHeight
				: player.headCamera
					? player.headCamera.transform.position.y - player.transform.position.y
					: 1.7f;

			if (!Mathf.Approximately(player.minMaxHeight.y, maxHeight))
				player.minMaxHeight = new Vector2(player.minMaxHeight.x, maxHeight);
		}
	}
}
