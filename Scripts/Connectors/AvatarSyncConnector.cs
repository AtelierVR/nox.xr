using System.Linq;
using Autohand;
using Nox.Avatars.Camera;
using Nox.Avatars.Hand;
using Nox.Avatars.Parameters;
using Nox.CCK;
using Nox.CCK.XR;
using UnityEngine;
using NoxHandType = Nox.Avatars.Hand.HandType;

namespace Nox.XR.Connectors {
	public class AvatarSyncConnector : MonoBehaviour {
		public AutoHandPlayer player;
		public AvatarLoaderConnector avatarLoader;

		// ReSharper disable Unity.PerformanceAnalysis
		private void Update() {
			SynchronizeParametersAvatar();
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
			var cameraModule = avatar?.Descriptor
				?.GetModules<ICameraModule>()
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
					case "tracking/head/position": {
						var cPos = player.headCamera.transform.position;
						if (cameraModule != null) {
							var camAnchor = cameraModule.GetAnchor();
							if (camAnchor != null)
								cPos -= camAnchor.TransformDirection(cameraModule.GetOffset());
						}
						var value = param.Get().ToVector3();
						if (Vector3.Distance(value, cPos) < 0.001f)
							continue;
						param.Set(cPos);
						break;
					}
					case "tracking/head/rotation": {
						var cRot  = player.headCamera.transform.rotation;
						var value = param.Get().ToQuaternion();
						if (Quaternion.Angle(value, cRot) < 0.001f)
							continue;
						param.Set(cRot);
						break;
					}
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
