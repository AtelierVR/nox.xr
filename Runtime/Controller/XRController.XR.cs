using System.Collections.Generic;
using System.Linq;
using Autohand;
using Cysharp.Threading.Tasks;
using Nox.Avatars.Camera;
using Nox.Avatars.Controllers;
using Nox.CCK.Players;
using Nox.CCK.Utils;
using UnityEngine;
using Logger = Nox.CCK.Utils.Logger;
using Transform = UnityEngine.Transform;
using Nox.Controllers;
using Nox.Players;
using UnityEngine.EventSystems;

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

		[NoxPublic(NoxAccess.Method)]
		public float GetViewHeight()
			=> player && player.headCamera ? player.headCamera.transform.position.y : transform.position.y;

		[NoxPublic(NoxAccess.Method)]
		public float GetRecommendedHeight() {
			// minMaxHeight.y comes from the avatar size; until it does it is the Autohand prefab default.
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

			// The lever is the `trackers` node, never `player.heightOffset`: that offset is applied to the
			// container, which holds the avatar, so using it would move the avatar with the view.
			if (!trackers) {
				Logger.LogError($"{nameof(ReHeight)}: no trackers node on this XR proxy (see {nameof(trackers)}), cannot adjust the view height.", this);
				return;
			}

			var delta = height - camera.transform.position.y;
			if (Mathf.Abs(delta) < 0.001f)
				return;

			// Local to the container, so the container writes of AutoHandPlayer do not overwrite it.
			trackers.position += Vector3.up * delta;
			Logger.LogDebug($"ReHeight: view → {height:F2} m (trackers {delta:+0.###;-0.###} m)", this);
		}

		/// <summary>
		/// Recentres the view over the player: only the X/Z are corrected, the look direction and the height are
		/// kept (see <see cref="ReHeight"/>).
		/// </summary>
		[NoxPublic(NoxAccess.Method)]
		public void ReCenter() {
			// The proxy root stays where it was created, the AutoHandPlayer capsule is what moves with the player.
			var position = player ? player.transform.position : transform.position;
			var forward  = player && player.headCamera ? player.headCamera.transform.forward : transform.forward;
			ReCenter(position, forward);
		}

		/// <summary>
		/// Recentres the view on a world position, keeping the current height: the trackers node (the tracking
		/// space, see <see cref="trackers"/>) is moved, so the body and the avatar do not follow the view.
		/// </summary>
		[NoxPublic(NoxAccess.Method)]
		public void ReCenter(Vector3 worldPosition, Vector3 forward) {
			var camera = GetCamera();
			if (!camera) {
				Logger.LogError($"{nameof(ReCenter)}: no camera on this XR rig, cannot recenter the view.", this);
				return;
			}

			if (!trackers) {
				Logger.LogError($"{nameof(ReCenter)}: no trackers node on this XR proxy (see {nameof(trackers)}), cannot recenter the view.", this);
				return;
			}

			var flatForward = Vector3.ProjectOnPlane(forward, Vector3.up);
			var camPosition = camera.transform.position;

			if (flatForward.sqrMagnitude > 0.0001f) {
				var camForward = Vector3.ProjectOnPlane(camera.transform.forward, Vector3.up);
				if (camForward.sqrMagnitude > 0.0001f)
					trackers.RotateAround(camPosition, Vector3.up, Vector3.SignedAngle(camForward, flatForward, Vector3.up));
			}

			// The current Y is kept: the vertical is handled by ReHeight().
			var delta = new Vector3(worldPosition.x - camPosition.x, 0f, worldPosition.z - camPosition.z);
			trackers.position += delta;

			Logger.LogDebug($"ReCenter: view → ({worldPosition.x:F2}, {worldPosition.z:F2}) forward {flatForward}", this);
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

			// A freshly created loader has no avatar yet: loading it here would start a
			// load before SetupAvatar(), which would start a second one in parallel.
			if (controller is IControllerAvatar ca && avatarLoader?.GetAvatar() != null) {
				var identifier = ca.GetAvatar()?.Identifier ?? Identifier.Invalid;
				if (identifier.IsValid())
					SetAvatar(identifier).Forget();
			}

			return UniTask.CompletedTask;
		}

		/// <summary>
		/// <c>PlayerRig.Base</c> part of this controller: the pose of the avatar's root.
		/// <para>
		/// The avatar is parented to <see cref="avatarContainer"/>, a child of the AutoHandPlayer's container:
		/// that node <b>is</b> the avatar root. It carries the container's locomotion and yaw - the only
		/// transform a snap/smooth turn rotates - while the <c>AvatarSyncConnector</c> cancels the container's
		/// vertical offset on it, so its pose is exactly the one the local avatar has (unlike the container's,
		/// whose Y carries the view height, and unlike the body capsule, which keeps its own yaw).
		/// </para>
		/// </summary>
		private TransformObject GetBasePart() {
			if (!player)
				return new TransformObject(transform, transform.TryGetComponent<Rigidbody>(out var own) ? own : null);

			// Body transform supplies the scale and the body's velocity/angular velocity; the pose is then
			// overridden with the avatar root's.
			var tr = new TransformObject(player.transform, player.body);

			if (avatarContainer) {
				tr.SetPosition(avatarContainer.position);
				tr.SetRotation(avatarContainer.rotation);
			}

			return tr;
		}

		public bool TryGetPart(ushort index, out TransformObject tr) {
			if (index == PlayerRig.Base.ToIndex()) {
				tr = GetBasePart();
				return true;
			}

			// Full-body targets (hips, feet, ...) are controller parts too: the calibration publishes the very
			// pose it applies locally, so a viewer replays the same target instead of guessing it.
			if (FullBody.FullBodyCalibration.TryGetDriverTarget(index, out var fbtPart)) {
				tr = fbtPart;
				return true;
			}

			var parts = GetParts();
			if (parts.TryGetValue(index, out var t)) {
				var rb = t.TryGetComponent<Rigidbody>(out var r) ? r : null;
				tr = new TransformObject(t, rb);

				if (index == PlayerRig.Head.ToIndex()
				    && TryGetHeadTargetPose(out var headPosition, out var headRotation)) {
					tr.SetPosition(headPosition);
					tr.SetRotation(headRotation);
				}

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

		public Dictionary<ushort, Transform> GetParts() {
			if (!player) return new Dictionary<ushort, Transform>();

			// `PlayerRig.Base` is the avatar ROOT: <see cref="avatarContainer"/>, the node the loaded avatar is
			// parented to. `player.transform` is the AutoHandPlayer (body capsule) and keeps its own yaw, so using
			// it as the base desynced the replayed body from the head/hands (cf. <see cref="GetBasePart"/>).
			var parts = new Dictionary<ushort, Transform> {
				{ PlayerRig.Base.ToIndex(), avatarContainer ? avatarContainer : player.transform },
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

			// `PlayerRig.Base` is the avatar root pose (tracking container), NOT the AutoHandPlayer body:
			// replace the raw entry so the sent values carry the container pose and the body velocity.
			parts[PlayerRig.Base.ToIndex()] = GetBasePart();

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

			// Full-body tracking targets enter the same dictionary (cf. TryGetPart): what the controller tracks
			// is what travels, and the rig is written from this single source on both the owner and the viewers.
			foreach (var id in FullBody.FullBodyCalibration.DriverTargetIds)
				if (FullBody.FullBodyCalibration.TryGetDriverTarget(id, out var fbtTarget))
					parts[id] = fbtTarget;

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
