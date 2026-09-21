using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Autohand;
using Nox.CCK.XR;
using Cysharp.Threading.Tasks;
using Nox.Avatars;
using Nox.Avatars.Camera;
using Nox.Avatars.Controllers;
using Nox.CCK.Players;
using Nox.CCK.Utils;
using Nox.Audio.Players;
using Nox.Sessions;
using UnityEngine;
using Logger = Nox.CCK.Utils.Logger;
using Transform = UnityEngine.Transform;
using Nox.Controllers;
using Nox.Players;
using Nox.XR.Runtime.Connectors;
using Nox.XR.Runtime.Providers;
using UnityEngine.EventSystems;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR;
using Unity.XR.CoreUtils;

namespace Nox.XR.Runtime {
	public partial class XRController {

	/// <summary>
	/// Interface side of the XR controller: what IController, IControllerAvatar and IXRController
	/// require - capabilities, view height and recentering, tracked parts and their mapping to the
	/// avatar rig parts.
	/// </summary>

		[NoxPublic(NoxAccess.Method)]
		public string GetId()
			=> DefaultId;

		[NoxPublic(NoxAccess.Method)]
		public int GetPriority()
			=> DefaultPriority;

		[NoxPublic(NoxAccess.Method)]
		public Camera GetCamera()
			=> player.headCamera;

		public EventSystem GetEventSystem()
			=> eventSystem;

		[NoxPublic(NoxAccess.Method)]
		public Collider GetCollider()
			=> player.bodyCollider;

		#region View Recentering (IXRController)

		private XROrigin GetXrOrigin() {
			if (_xrOrigin)
				return _xrOrigin;

			_xrOrigin = GetComponent<XROrigin>();
			if (!_xrOrigin)
				_xrOrigin = XROriginSetter.GlobalOrigin;

			return _xrOrigin;
		}

		[NoxPublic(NoxAccess.Method)]
		public float GetViewHeight()
			=> player && player.headCamera ? player.headCamera.transform.position.y : transform.position.y;

		[NoxPublic(NoxAccess.Method)]
		public float GetRecommendedHeight() {
			// minMaxHeight.y est renseigné depuis la taille de l'avatar (ScaleAvatarModule)
			// par AvatarLoaderConnector/AvatarSyncConnector. Tant que ce n'est pas fait, la
			// valeur reste le défaut du prefab Autohand, qui n'est pas une taille de joueur.
			if (player && player.minMaxHeight.y > 0f
			           && !Mathf.Approximately(player.minMaxHeight.y, AutoHandDefaultMaxHeight))
				return player.minMaxHeight.y;

			return defaultViewHeight > 0f ? defaultViewHeight : DefaultViewHeight;
		}

		[NoxPublic(NoxAccess.Method)]
		public void ReHeight(float height = -1f) {
			if (!player) {
				Logger.LogError($"{nameof(ReHeight)}: no AutoHandPlayer on this XR proxy, cannot adjust the view height.", this);
				return;
			}

			var camera = player.headCamera;
			if (!camera) {
				Logger.LogError($"{nameof(ReHeight)}: no head camera, cannot adjust the view height.", this);
				return;
			}

			if (height <= 0f)
				height = GetRecommendedHeight();

			// AutoHandPlayer applique heightOffset au trackingContainer (caméra, mains et
			// avatar) : c'est le levier prévu pour corriger la hauteur de vue. Contrairement
			// à un déplacement direct du transform, il est ré-appliqué à chaque frame.
			var delta = height - camera.transform.position.y;
			if (Mathf.Abs(delta) < 0.001f)
				return;

			player.heightOffset += delta;
			Logger.LogDebug($"ReHeight: view → {height:F2} m (heightOffset {player.heightOffset:+0.###;-0.###} m)", this);
		}

		[NoxPublic(NoxAccess.Method)]
		public void ReCenter()
			=> ReCenter(transform.position, transform.forward);

		[NoxPublic(NoxAccess.Method)]
		public void ReCenter(Vector3 worldPosition, Vector3 forward) {
			var camera = GetCamera();
			if (!camera) {
				Logger.LogError($"{nameof(ReCenter)}: no camera on this XR rig, cannot recenter the view.", this);
				return;
			}

			var flatForward = Vector3.ProjectOnPlane(forward, Vector3.up);
			// Y courant conservé : la verticale est gérée par ReHeight().
			var target = new Vector3(worldPosition.x, camera.transform.position.y, worldPosition.z);

			var origin = GetXrOrigin();
			if (origin) {
				// XROrigin gère les deux modes de tracking (Floor et Device) : on le laisse
				// placer/faire tourner le rig plutôt que de manipuler les transforms à la main.
				origin.MoveCameraToWorldLocation(target);

				if (flatForward.sqrMagnitude > 0.0001f)
					origin.MatchOriginUpCameraForward(Vector3.up, flatForward.normalized);

				Logger.LogDebug($"ReCenter: view → ({target.x:F2}, {target.z:F2}) forward {flatForward}", this);
				return;
			}

			// Repli sans XROrigin : rotation dans l'espace monde, puis translation du rig.
			var camForward = Vector3.ProjectOnPlane(camera.transform.forward, Vector3.up).normalized;
			if (flatForward.sqrMagnitude > 0.0001f && camForward.sqrMagnitude > 0.0001f)
				transform.rotation = Quaternion.FromToRotation(camForward, flatForward.normalized) * transform.rotation;

			var delta = target - camera.transform.position;
			delta.y = 0f;
			transform.position += delta;

			Logger.LogDebug($"ReCenter (fallback): view → ({target.x:F2}, {target.z:F2}) forward {flatForward}", this);
		}

		[NoxPublic(NoxAccess.Method)]
		public void ReCenterAndReHeight(float height = -1f) {
			ReCenter();
			ReHeight(height);
		}

		#endregion

		public UniTask Restore(IController controller) {
			foreach (var ability in controller.GetAbilities())
				SetAbilities(ability.Key, ability.Value);

			// Un loader fraîchement créé n'a pas encore d'avatar : le charger ici
			// ferait démarrer un chargement avant SetupAvatar(), qui en lancerait un
			// second en parallèle (annulations et avatars détruits en cascade).
			if (controller is IControllerAvatar ca && avatarLoader?.GetAvatar() != null) {
				var identifier = ca.GetAvatar()?.Identifier ?? Identifier.Invalid;
				if (identifier.IsValid())
					SetAvatar(identifier).Forget();
			}

			return UniTask.CompletedTask;
		}

		public bool TryGetPart(ushort index, out TransformObject tr) {
			var parts = GetParts();
			if (parts.TryGetValue(index, out var t)) {
				var rb = t.TryGetComponent<Rigidbody>(out var r) ? r : null;
				tr = new TransformObject(t, rb);
				return true;
			}

			tr = new TransformObject();
			return false;
		}

		[NoxPublic(NoxAccess.Method)]
		public Dictionary<string, object> GetAbilities()
			=> new() {
				{ "pushing", player.IsPushing() },
				{ "grounded", player.IsGrounded() },
				{ "climbing", player.IsClimbing() },
				{ "pushing_up", player.IsPushingUp() },
				{ "immobilized", !player.useMovement },
				{ "crouching", player.crouching },
				{ "flying", !player.useGrounding },
				{ "may_fly", mayFly },
				{ "max_move_speed", player.maxMoveSpeed },
				{ "move_acceleration", player.moveAcceleration }
			};

		[NoxPublic(NoxAccess.Method)]
		public void SetAbilities(string key, object value) {
			if (!GetAbilities().ContainsKey(key))
				return;
			switch (key) {
				case "immobilized":
					player.useMovement = !(bool)value;
					break;
				case "crouching":
					player.crouching = (bool)value;
					break;
				case "flying":
					if (!player.useGrounding != (bool)value)
						player.ToggleFlying();
					break;
				case "may_fly":
					mayFly = (bool)value;
					if (!player.useGrounding && !mayFly)
						player.ToggleFlying();
					break;
				case "max_move_speed":
					player.maxMoveSpeed = (float)value;
					break;
				case "move_acceleration":
					player.moveAcceleration = (float)value;
					break;
			}
		}

		private static readonly (FingerEnum finger, PlayerRig proximal, PlayerRig intermediate, PlayerRig distal)[] _fingerMap = {
			(FingerEnum.thumb,  PlayerRig.LeftThumb,  PlayerRig.LeftThumbNail,  PlayerRig.LeftThumbTip),
			(FingerEnum.index,  PlayerRig.LeftIndex,  PlayerRig.LeftIndexNail,  PlayerRig.LeftIndexTip),
			(FingerEnum.middle, PlayerRig.LeftMiddle, PlayerRig.LeftMiddleNail, PlayerRig.LeftMiddleTip),
			(FingerEnum.ring,   PlayerRig.LeftRing,   PlayerRig.LeftRingNail,   PlayerRig.LeftRingTip),
			(FingerEnum.pinky,  PlayerRig.LeftPinky,  PlayerRig.LeftPinkyNail,  PlayerRig.LeftPinkyTip),
		};

		private static readonly (FingerEnum finger, PlayerRig proximal, PlayerRig intermediate, PlayerRig distal)[] _fingerMapRight = {
			(FingerEnum.thumb,  PlayerRig.RightThumb,  PlayerRig.RightThumbNail,  PlayerRig.RightThumbTip),
			(FingerEnum.index,  PlayerRig.RightIndex,  PlayerRig.RightIndexNail,  PlayerRig.RightIndexTip),
			(FingerEnum.middle, PlayerRig.RightMiddle, PlayerRig.RightMiddleNail, PlayerRig.RightMiddleTip),
			(FingerEnum.ring,   PlayerRig.RightRing,   PlayerRig.RightRingNail,   PlayerRig.RightRingTip),
			(FingerEnum.pinky,  PlayerRig.RightPinky,  PlayerRig.RightPinkyNail,  PlayerRig.RightPinkyTip),
		};

		private static void AddFingerParts(Dictionary<ushort, Transform> parts, Autohand.Hand hand,
			(FingerEnum finger, PlayerRig proximal, PlayerRig intermediate, PlayerRig distal)[] map) {
			if (hand == null || hand.fingers == null || hand.fingers.Length == 0) return;
			foreach (var entry in map) {
				var finger = System.Array.Find(hand.fingers, f => f.fingerType == entry.finger);
				if (finger == null) continue;
				if (finger.knuckleJoint) parts[entry.proximal.ToIndex()]     = finger.knuckleJoint;
				if (finger.middleJoint)  parts[entry.intermediate.ToIndex()] = finger.middleJoint;
				if (finger.distalJoint)  parts[entry.distal.ToIndex()]       = finger.distalJoint;
			}
		}

		private Dictionary<ushort, Transform> GetParts() {
			if (!player) return new Dictionary<ushort, Transform>();

			var parts = new Dictionary<ushort, Transform> {
				{ PlayerRig.Base.ToIndex(), player.transform },
				{ PlayerRig.Head.ToIndex(), player.headCamera.transform }
			};

			if (player.handLeft) {
				parts.Add(PlayerRig.LeftHand.ToIndex(), player.handLeft.transform);
				AddFingerParts(parts, player.handLeft, _fingerMap);
			}

			if (player.handRight) {
				parts.Add(PlayerRig.RightHand.ToIndex(), player.handRight.transform);
				AddFingerParts(parts, player.handRight, _fingerMapRight);
			}

			return parts;
		}

		IReadOnlyDictionary<ushort, TransformObject> IController.GetParts() {
			var parts = GetParts().ToDictionary(
				kv => kv.Key,
				kv => new TransformObject(kv.Value, kv.Value.GetComponent<Rigidbody>())
			);

			// `PlayerRig.Head` carries the head TARGET pose, not the eye pose: it is written on the rig's
			// head IK target (`VRIK_Head`), which expects the head bone position - the avatar's eye → head
			// offset is what separates the two. The local and remote drivers therefore write the same thing,
			// and the head bone lands on the head bone on every client.
			var headId = PlayerRig.Head.ToIndex();
			if (parts.TryGetValue(headId, out var head)
			    && TryGetHeadTargetPose(out var headPosition, out var headRotation)) {
				head.SetPosition(headPosition);
				head.SetRotation(headRotation);
			}

			return parts;
		}

		/// <summary>
		/// Head target pose for the rig: the headset pose moved back onto the avatar's head bone, using the
		/// eye → head offset declared by the avatar author (see <see cref="ICameraModule"/>).
		/// </summary>
		private bool TryGetHeadTargetPose(out Vector3 position, out Quaternion rotation) {
			position = Vector3.zero;
			rotation = Quaternion.identity;

			var camera = player != null ? player.headCamera : null;
			if (camera == null)
				return false;

			var cameraTransform = camera.transform;
			position = cameraTransform.position;
			rotation = cameraTransform.rotation;

			var module = avatarLoader?.GetAvatar()?.Descriptor
				?.GetModules<ICameraModule>()
				.FirstOrDefault();
			var anchor = module != null ? module.GetAnchor() : null;
			if (anchor != null)
				position -= anchor.TransformDirection(module.GetOffset());

			return true;
		}

		[NoxPublic(NoxAccess.Method)]
		public IPlayer GetPlayer()
			=> _attachedPlayer;

		public void SetPart(ushort index, TransformObject tr) {
			Rigidbody rb;

			if (index == PlayerRig.Base.ToIndex()) {
				if (tr.Flags.HasFlag(TransformFlags.Position) && !tr.IsSamePosition(player.transform.position))
					player.SetPosition(tr.GetPosition());

				if (tr.Flags.HasFlag(TransformFlags.Rotation) && !tr.IsSameRotation(player.transform.rotation))
					player.SetRotation(tr.GetRotation());

				rb = player.body;

				if (rb && tr.Flags.HasFlag(TransformFlags.Velocity) && !tr.IsSameVelocity(rb.linearVelocity))
					rb.linearVelocity = tr.GetVelocity();

				if (rb && tr.Flags.HasFlag(TransformFlags.Angular) && !tr.IsSameAngular(rb.angularVelocity))
					rb.angularVelocity = tr.GetAngular();
				return;
			}

			var part = GetParts()
				.FirstOrDefault(p => p.Key == index);

			if (!part.Value)
				return;

			var hasRb = part.Value.TryGetComponent<Rigidbody>(out rb);

			if (tr.Flags.HasFlag(TransformFlags.Position) && !tr.IsSamePosition(part.Value.position)) {
				part.Value.position = tr.GetPosition();
				if (hasRb && rb)
					rb.position = tr.GetPosition();
			}

			if (tr.Flags.HasFlag(TransformFlags.Rotation) && !tr.IsSameRotation(part.Value.rotation)) {
				part.Value.rotation = tr.GetRotation();
				if (hasRb && rb)
					rb.rotation = tr.GetRotation();
			}

			if (tr.Flags.HasFlag(TransformFlags.Scale) && !tr.IsSameScale(part.Value.localScale))
				part.Value.localScale = tr.GetScale();

			if (hasRb && rb) {
				if (tr.Flags.HasFlag(TransformFlags.Velocity) && !tr.IsSameVelocity(rb.linearVelocity))
					rb.linearVelocity = tr.GetVelocity();

				if (tr.Flags.HasFlag(TransformFlags.Angular) && !tr.IsSameAngular(rb.angularVelocity))
					rb.angularVelocity = tr.GetAngular();
			}
		}
	}
}
