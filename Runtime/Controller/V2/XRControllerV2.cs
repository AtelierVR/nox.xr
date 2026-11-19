using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Nox.CCK.Entities;
using Nox.CCK.Utils;
using Nox.CCK.XR;
using Nox.Controllers;
using Nox.XR.Menu;
using Nox.XR.Player;
using Nox.XR.Runtime;
using UnityEngine;
using Logger = Nox.CCK.Utils.Logger;
using NoxPlayer = Nox.XR.Player.Player;

namespace Nox.XR.Controller.V2 {
	/// <summary>
	/// XR proxy controller driving a <see cref="NoxPlayer"/> instead of AutoHand's <c>AutoHandPlayer</c>.
	/// <para>
	/// It implements <see cref="IController"/> and <see cref="IXRController"/> so it can replace the
	/// AutoHand proxy as the current controller: the controller layer, the widgets and the other mods only
	/// see these contracts, never the concrete type - which is also why <c>Nox.XR.Runtime</c> can create
	/// this rig from its prefab without referencing this assembly.
	/// </para>
	/// </summary>
	[AddComponentMenu("Nox/XR/Player/XR Controller V2")]
	[DefaultExecutionOrder(15)]
	public partial class XRControllerV2 : MonoBehaviour, IController, IXRController {
		/// <summary>Id of the v2 proxy in the controller layer (v1 is <c>"xr"</c>).</summary>
		public const string Id = "xr2";

		private const float DefaultViewHeight = 1.7f;

		[Header("Player")]
		[SerializeField, Tooltip("Player driven by this controller. Found in the children/parents when empty.")]
		private NoxPlayer player;

		[SerializeField, Tooltip("Component forwarding the XR bindings to the player. Found on the player when empty.")]
		private PlayerMovementDriver driver;

		[SerializeField, Tooltip("Component spawning the tracked hands/feet. Found on the player when empty.")]
		private PlayerPartBinder binder;

		[SerializeField, Tooltip("Component building and showing the main menu. Found on the player when empty.")]
		private PersonalMenu menu;

		[Header("Proxy nodes")]
		[Tooltip("Node holding the player pose (head camera, hands). Defaults to the player tracking container.")]
		[SerializeField] private Transform trackers;

		[Header("View")]
		[SerializeField, Tooltip("View height used while no avatar provides its own size, in meters.")]
		private float defaultViewHeight = DefaultViewHeight;

		/// <summary>Player driven by this controller.</summary>
		public NoxPlayer Player {
			get => player;
			set => player = value;
		}

		public PlayerMovementDriver Driver => driver;

		public PlayerPartBinder Binder => binder;

		/// <summary>Menu connector of this rig, or <c>null</c> when it could not be created.</summary>
		public PersonalMenu Menu => menu;

		/// <summary>Node holding the tracked pose (head camera, hands, feet).</summary>
		public Transform Trackers
			=> trackers ? trackers : player ? player.TrackingContainer : null;

		/// <summary>Camera of the head part, or <c>null</c> while the head is missing.</summary>
		public Camera HeadCamera
			=> player ? player.headCamera : null;

		public Head Head
			=> player ? player.Head : null;

		public Hand LeftHand
			=> player ? player.LeftHand : null;

		public Hand RightHand
			=> player ? player.RightHand : null;

		/// <summary>Every tracked part of the player, keyed by rig slot.</summary>
		public IReadOnlyDictionary<EntityRig, Part> Parts
			=> player ? player.Parts : null;

		public bool IsReady
			=> player && player.Head;

		public Transform TrackingContainer
			=> player ? player.TrackingContainer : null;

		/// <summary>
		/// Instantiates the v2 proxy prefab (<c>controller.prefab</c>) and returns its controller.
		/// <para>
		/// The prefab only contains this component and a <c>Player</c> prefab instance: every reference
		/// (driver, binder, trackers) is resolved at <c>Awake</c>.
		/// </para>
		/// </summary>
		/// <param name="parent">Optional parent of the instance.</param>
		public static XRControllerV2 Make(Transform parent = null) {
			var instance = XRProxySpawner.InstantiateV2(parent);
			if (!instance)
				return null;

			var controller = instance.GetComponent<XRControllerV2>();
			if (!controller) {
				Logger.LogError(
					$"The '{XRProxySpawner.V2Prefab}' prefab has no {nameof(XRControllerV2)} component.",
					instance, tag: nameof(XRControllerV2));
				instance.Destroy();
				return null;
			}

			instance.name = $"[{nameof(XRControllerV2)}] {ComponentExtension.GetId(controller)}";
			return controller;
		}

		private void Awake() {
			if (!player) {
				Logger.LogError($"No {nameof(NoxPlayer)} assigned: the XR controller cannot drive anything.", this, tag: nameof(XRControllerV2));
				return;
			}

			driver.Player = player;
			binder.Player = player;

			if (menu) {
				menu.Origin = player.transform;
				menu.View = player.headCamera;
			}

			SetupUi();
			SetupInteraction();
			SetupCalibration();
			SetupAvatarFollower();

			player.OnPartRegistered.AddListener(OnPartRegistered);
			player.OnPartUnregistered.AddListener(OnPartUnregistered);
			player.OnHeightOffset.AddListener(GroundAvatar);
			player.OnMove.AddListener(UpdateMovementParameters);
			player.OnJump.AddListener(UpdateMovementParameters);
			player.OnGrounded.AddListener(OnGroundedChanged);
		}

		private void OnDestroy() {
			DisposeCalibration();
			DisposeInteraction();
			UnsubscribeFit();

			if (!player)
				return;

			player.OnPartRegistered.RemoveListener(OnPartRegistered);
			player.OnPartUnregistered.RemoveListener(OnPartUnregistered);
			player.OnHeightOffset.RemoveListener(GroundAvatar);
			player.OnMove.RemoveListener(UpdateMovementParameters);
			player.OnJump.RemoveListener(UpdateMovementParameters);
			player.OnGrounded.RemoveListener(OnGroundedChanged);
		}

		private void Start() {
			StartAvatar();
		}

		/* ------------------------------------------------------------------ */
		/* Locomotion (thin facade over the player)                            */
		/* ------------------------------------------------------------------ */

		/// <summary>Sets the move direction for this fixed update.</summary>
		public void Move(Vector2 axis, bool useDeadzone = true, bool useRelativeDirection = false)
			=> player?.Move(axis, useDeadzone, useRelativeDirection);

		/// <summary>Sets the turn axis for this frame.</summary>
		public void Turn(float axis)
			=> player?.Turn(axis);

		/// <summary>Jumps if the player is grounded.</summary>
		public void Jump(float power = 1f)
			=> player?.Jump(power);

		/// <summary>Moves the whole rig to a position (optionally rotating it).</summary>
		public void Teleport(Vector3 position, Quaternion? rotation = null) {
			if (!player)
				return;

			if (rotation.HasValue)
				player.SetPosition(position, rotation.Value);
			else
				player.SetPosition(position);
		}

		/// <summary>Recenters the rig around the head pose.</summary>
		public void Recenter()
			=> player?.Recenter();

		public Vector3 Position
			=> player ? player.transform.position : transform.position;

		public bool IsGrounded
			=> player && player.IsGrounded();

		public bool IsCrouching
			=> player && player.IsCrouching;

		public float HeightOffset {
			get => player ? player.heightOffset : 0f;
			set {
				if (player)
					player.heightOffset = value;
			}
		}

		/* ------------------------------------------------------------------ */
		/* IController                                                         */
		/* ------------------------------------------------------------------ */

		public string GetId()
			=> Id;

		public int GetPriority()
			=> IController.DefaultPriority + 1;

		public Camera GetCamera()
			=> HeadCamera;

		public Collider GetCollider()
			=> player ? player.floorCollider : null;

		public UniTask Dispose() {
			Logger.LogDebug("Disposing the v2 XR proxy.", this, tag: nameof(XRControllerV2));
			UnbindRigProvider();
			gameObject.Destroy();

			return UniTask.CompletedTask;
		}

		public UniTask Restore(IController controller) {
			if (controller != null)
				foreach (var ability in controller.GetAbilities())
					SetAbilities(ability.Key, ability.Value);

			return UniTask.CompletedTask;
		}

		/// <summary>Every tracked part of the rig, in the controller layer wire format.</summary>
		IReadOnlyDictionary<ushort, TransformObject> IController.GetParts() {
			var parts = new Dictionary<ushort, TransformObject>(4);

			if (!player)
				return parts;

			var baseTransform = avatarContainer ? avatarContainer : player.transform;
			parts[EntityRig.Base.ToIndex()] = baseTransform.TryGetComponent(out Rigidbody baseBody)
				? new TransformObject(baseTransform, baseBody)
				: new TransformObject(baseTransform);
			foreach (var pair in player.Parts) {
				var part = pair.Value;
				if (!part)
					continue;

				var partTransform = part.transform;
				parts[pair.Key.ToIndex()] = partTransform.TryGetComponent(out Rigidbody body)
					? new TransformObject(partTransform, body)
					: new TransformObject(partTransform);
			}

			return parts;
		}

		public bool TryGetPart(ushort index, out TransformObject transform) {
			transform = new TransformObject();

			if (!player)
				return false;

			var part = player.GetPart(index.ToEntityRig());
			if (!part)
				return false;

			var partTransform = part.transform;
			transform = partTransform.TryGetComponent(out Rigidbody body)
				? new TransformObject(partTransform, body)
				: new TransformObject(partTransform);
			return true;
		}

		public void SetPart(ushort index, TransformObject transform) {
			if (player)
				player.SetPart(index, transform);
		}

		public Dictionary<string, object> GetAbilities()
			=> player
				? new Dictionary<string, object> {
					{ "grounded", player.IsGrounded() },
					{ "immobilized", !player.useMovement },
					{ "crouching", player.crouching },
					{ "flying", !player.useGrounding },
					{ "max_move_speed", player.maxMoveSpeed },
					{ "move_acceleration", player.moveAcceleration }
				}
				: new Dictionary<string, object>();

		public void SetAbilities(string key, object value) {
			if (!player)
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
				case "max_move_speed":
					player.maxMoveSpeed = (float)value;
					break;
				case "move_acceleration":
					player.moveAcceleration = (float)value;
					break;
			}
		}

		/* ------------------------------------------------------------------ */
		/* IXRController                                                       */
		/* ------------------------------------------------------------------ */

		public float GetViewHeight()
			=> HeadCamera
				? HeadCamera.transform.position.y
				: transform.position.y;

		public float GetRecommendedHeight() {
			var eyeHeight = XRViewHeight.EyeHeight(_avatar?.Descriptor);

			return eyeHeight > 0f
				? eyeHeight
				: defaultViewHeight > 0f
					? defaultViewHeight
					: DefaultViewHeight;
		}

		public void ReHeight(float height = -1f) {
			if (!player)
				return;

			if (height <= 0f)
				height = GetRecommendedHeight();

			var delta = height - GetViewHeight();
			if (Mathf.Abs(delta) < 0.001f)
				return;

			player.heightOffset += delta;
		}

		public void ReCenter() {
			if (!player)
				return;

			var forward = HeadCamera
				? HeadCamera.transform.forward
				: player.transform.forward;
			ReCenter(player.transform.position, forward);
		}

		public void ReCenter(Vector3 worldPosition, Vector3 forward) {
			if (!player)
				return;

			var position = player.transform.position;
			var target = new Vector3(worldPosition.x, position.y, worldPosition.z);
			var flatForward = Vector3.ProjectOnPlane(forward, Vector3.up);

			var rotation = flatForward.sqrMagnitude > 0.0001f
				? Quaternion.LookRotation(flatForward, Vector3.up)
				: player.transform.rotation;

			player.SetPosition(target, rotation);
		}

		public void ReCenterAndReHeight(float height = -1f) {
			ReCenter();
			ReHeight(height);
		}
	}
}
