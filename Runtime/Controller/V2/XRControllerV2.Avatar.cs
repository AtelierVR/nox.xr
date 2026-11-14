using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using Nox.Assets;
using Nox.Avatars;
using Nox.Avatars.Camera;
using Nox.Avatars.Hand;
using Nox.Avatars.Parameters;
using Nox.Avatars.Scale;
using Nox.CCK.Entities;
using Nox.CCK.Network.Assets;
using Nox.CCK.Utils;
using Nox.XR.Player;
using Nox.XR.Runtime;
using UnityEngine;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.XR.Controller.V2 {
	/// <summary>
	/// Avatar side of the v2 XR controller: the node the loaded avatar is parented to, and its loader.
	/// <para>
	/// The container is a child of the controller root and follows the player rig: it is never scaled
	/// (the avatar carries its own scale) and never carries the view height, so moving the view cannot
	/// move the avatar by accident.
	/// </para>
	/// <para>
	/// The loader parents the avatar to that container and configures it through its own modules (tracking
	/// parameters, size). The v1 loader (<c>AvatarLoaderConnector</c>) also drives the AutoHand hand rig and
	/// the FinalIK bridge: those parts do not exist yet in the v2 rig, so this loader only handles the
	/// avatar itself.
	/// </para>
	/// </summary>
	public partial class XRControllerV2 {
		[Header("Avatar")]
		[Tooltip("Root node the loaded avatar is parented to, a child of this controller. It follows the "
				 + "player rig and is never scaled: the avatar carries its own scale.")]
		[SerializeField] private Transform avatarContainer;


		private IRuntimeAvatar _avatar;
		private CancellationTokenSource _context;

		/// <summary>Parameter module of the current avatar: it announces the parameters that join it.</summary>
		private IParameterModule _parameterModule;

		/// <summary>Locomotion parameters, resolved once when they are registered (written every frame).</summary>
		private IParameter _velocityParameter;
		private IParameter _groundedParameter;

		/// <summary>Name of the locomotion parameters this controller drives.</summary>
		private const string VelocityName = "Velocity";
		private const string GroundedName = "Grounded";

		/// <summary>Avatar currently loaded by this controller.</summary>
		public IRuntimeAvatar RuntimeAvatar => _avatar;

		/// <summary>Whether an avatar is loaded.</summary>
		public bool HasAvatar => _avatar != null;

		/* ------------------------------------------------------------------ */
		/* Container                                                            */
		/* ------------------------------------------------------------------ */

		/// <summary>
		/// Parents an avatar under the container, placed <b>on</b> it by default.
		/// <para>
		/// The container is the tracking space of the player (a child of the root that holds the head
		/// camera), so placing the avatar on it is what puts the avatar at the player position, under
		/// the camera. It is the same rule as the desktop controller, which parents the avatar to its own
		/// transform and zeroes its local pose. Keeping the load-time world pose instead freezes the
		/// avatar wherever the loader instantiated it, and leaves a constant offset between the avatar
		/// and the view (the rig then solves the bones from the camera, far from the body).
		/// </para>
		/// </summary>
		/// <param name="avatar">Root of the avatar.</param>
		/// <param name="keepWorldPose">Keep the world pose instead of placing the avatar on the container.</param>
		public void AttachAvatar(Transform avatar, bool keepWorldPose = false) {
			if (!avatar) {
				Logger.LogError($"{nameof(AttachAvatar)}: no avatar to attach.", this, tag: nameof(XRControllerV2));
				return;
			}

			if (!avatar.IsChildOf(avatarContainer))
				avatar.SetParent(avatarContainer, keepWorldPose);

			if (!keepWorldPose)
				avatar.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
		}

		/// <summary>Removes the current avatar from the container.</summary>
		public void DetachAvatar() {
			var anchor = _avatar?.Descriptor?.Anchor;
			if (!anchor)
				return;

			anchor.transform.SetParent(null, true);
		}

		/* ------------------------------------------------------------------ */
		/* Grounding                                                            */
		/* ------------------------------------------------------------------ */

		/// <summary>
		/// Keeps the avatar on the ground.
		/// <para>
		/// The player raises its tracking space to place the camera at the right height: the
		/// avatar must not inherit that offset, it would float above the floor and sink into it
		/// when crouching. The offset is cancelled on the avatar container, which still follows
		/// the player in X/Z.
		/// </para>
		/// <para>
		/// The player announces its height offset, so this is applied when the offset changes and
		/// never polled.
		/// </para>
		/// </summary>
		private void GroundAvatar(float heightOffset) {
			var local = avatarContainer.localPosition;
			var y = -heightOffset;
			if (Mathf.Approximately(local.y, y))
				return;

			avatarContainer.localPosition = new Vector3(local.x, y, local.z);
		}

		/// <summary>
		/// Keeps the avatar under the player and facing the same way.
		/// <para>
		/// The body catches up with the head (<c>Player.SyncBodyHead</c>) and turns to face the view, so the centre
		/// of the tracking space is no longer the player pose: without this the avatar would stay at the
		/// play-space origin, with its spawn orientation. Only X/Z and the yaw are written - the height is the one
		/// <see cref="GroundAvatar"/> applies.
		/// </para>
		/// </summary>
		private void LateUpdate() {
			var space = player.TrackingContainer;

			if (!space || avatarContainer.childCount == 0)
				return;

			var pose = player.transform;

			// The avatar follows the head, not the body: the body catches up with the view through physics
			// (SyncBodyHead) and is blocked by walls and by its own colliders, so a body-based avatar stays
			// behind while the player walks in room-scale. The head is always where the player is.
			var head   = player.Head;
			var source = head ? head.Position : pose.position;
			var local  = space.InverseTransformPoint(new Vector3(source.x, space.position.y, source.z));
			var current = avatarContainer.localPosition;

			avatarContainer.localPosition = new Vector3(local.x, current.y, local.z);
			avatarContainer.localRotation = Quaternion.Inverse(space.rotation) * pose.rotation;
		}

		/* ------------------------------------------------------------------ */
		/* Loading                                                              */
		/* ------------------------------------------------------------------ */

		/// <summary>
		/// Starts the avatar loading of the local user: the loading avatar first, then the avatar of the
		/// account once it is resolved and downloaded.
		/// </summary>
		public void StartAvatar() {
			LoadLocalAvatar().Forget();
		}

		/// <summary>
		/// Loads the avatar of the local user and parents it to the avatar container.
		/// <para>
		/// The loading avatar is shown first, then replaced by the avatar of the account when it is
		/// resolved and downloaded.
		/// </para>
		/// </summary>
		public async UniTask<IRuntimeAvatar> LoadLocalAvatar() {
			var avatarAPI = Client.AvatarAPI;
			if (avatarAPI == null) {
				Logger.LogError("No avatar API: cannot load the avatar.", this, tag: nameof(XRControllerV2));
				return null;
			}

			_context?.Cancel();
			_context?.Dispose();
			_context = new CancellationTokenSource();
			var token = _context.Token;

			var arguments = new Dictionary<string, object> {
				["source"] = this,
				["xr"] = true,
				["local"] = true
			};

			try {
				var loading = await avatarAPI.LoadLoading(arguments, null, token);
				if (loading == null) {
					Logger.LogError("Failed to create the loading avatar.", this, tag: nameof(XRControllerV2));
					return null;
				}

				await SetAvatar(loading);

				var identifier = Client.UserAPI?.Current?.Avatar;
				if (identifier == null || !identifier.Value.IsValid()) {
					Logger.LogWarning("The current user has no avatar: keeping the loading avatar.", this, tag: nameof(XRControllerV2));
					return loading;
				}

				var avatar = await LoadAvatar(avatarAPI, identifier.Value, arguments, token);
				if (avatar == null)
					return loading;

				await SetAvatar(avatar);
				return avatar;
			} catch (OperationCanceledException) {
				Logger.LogDebug("Avatar loading cancelled.", this, tag: nameof(XRControllerV2));
				return null;
			} catch (Exception e) {
				Logger.LogException(e, tag: nameof(XRControllerV2));
				return null;
			}
		}

		/// <summary>Resolves, downloads and loads the avatar of an identifier.</summary>
		private async UniTask<IRuntimeAvatar> LoadAvatar(
			IAvatarAPI avatarAPI,
			Identifier identifier,
			Dictionary<string, object> arguments,
			CancellationToken token
		) {
			var asset = await avatarAPI.ResolveBundle(identifier, token);
			if (token.IsCancellationRequested)
				return null;

			if (asset == null) {
				Logger.LogWarning($"Avatar asset not found for identifier {identifier}.", this, tag: nameof(XRControllerV2));
				return null;
			}

			var hash = asset.CacheKey();

			if (!avatarAPI.HasInCache(hash)) {
				var download = avatarAPI.DownloadToCache(new IdentifierCache(identifier, asset), token: token);
				await download.Start();
				if (token.IsCancellationRequested)
					return null;
			}

			var avatar = await avatarAPI.LoadFromCache(hash, arguments, null, token);
			if (token.IsCancellationRequested) {
				if (avatar != null)
					await avatar.Dispose();

				return null;
			}

			if (avatar == null) {
				Logger.LogError($"Failed to load the avatar of {identifier} from the cache.", this, tag: nameof(XRControllerV2));
				return null;
			}

			avatar.Identifier = identifier;
			avatar.Owner = Client.UserAPI?.Current?.Identifier
				?? Identifier.Invalid;
			return avatar;
		}

		/// <summary>
		/// Parents an avatar to the avatar container and configures it (tracking parameters, size).
		/// </summary>
		/// <param name="avatar">Avatar to attach.</param>
		public async UniTask<IRuntimeAvatar> SetAvatar(IRuntimeAvatar avatar) {
			if (avatar == null) {
				Logger.LogWarning("Setting a null avatar on the v2 controller.", this, tag: nameof(XRControllerV2));
				return null;
			}

			var descriptor = avatar.Descriptor;
			if (descriptor == null || !descriptor.Anchor) {
				Logger.LogError("The avatar has no descriptor or anchor: cannot attach it.", this, tag: nameof(XRControllerV2));
				return null;
			}

			var previous = _avatar;

			UnbindRigProvider();
			UnbindMovementParameters();

			_avatar = avatar;

			if (previous != null && previous != avatar)
				await previous.Dispose();

			BindRigProvider(descriptor);

			BindMovementParameters(descriptor);

			AttachAvatar(descriptor.Anchor.transform);

			ApplyScale(descriptor);
			ApplyTrackingParameters(descriptor);
			ApplyEyeOffset(descriptor);
			ApplyHandOffsets(descriptor);

			descriptor.Anchor.SetActive(true);

			BindCalibration(avatar);
			SubscribeFit(descriptor);

			return avatar;
		}

		/// <summary>
		/// Tells the head part where the head bone of this avatar is, relative to the eyes.
		/// <para>
		/// The rig drives the head <b>bone</b>, not the eyes: without this offset the head (and
		/// its mesh) is pushed forward by the distance between the two, and the view ends up
		/// inside the head mesh.
		/// </para>
		/// </summary>
		private void ApplyEyeOffset(IAvatarDescriptor descriptor) {
			var head = player?.Head;
			if (!head)
				return;

			var cameraModule = descriptor.GetModules<ICameraModule>().FirstOrDefault();
			if (cameraModule == null) {
				Logger.LogWarning("The avatar has no camera module: the head keeps the eye pose.", this, tag: nameof(XRControllerV2));
				return;
			}

			var anchor = cameraModule.GetAnchor();
			if (!anchor)
				return;

			if (anchor.lossyScale.y <= 0.0001f) {
				Logger.LogWarning(
					"The head bone of the avatar has a zero scale: the eye → head offset cannot be read, the view keeps the eye pose.",
					this, tag: nameof(XRControllerV2));
				return;
			}

			var offset = cameraModule.GetOffset();

			head.EyeOffset = offset;

			if (offset.sqrMagnitude < 0.000001f)
				Logger.LogWarning(
					$"The camera module of the avatar has no offset: the head bone is driven onto the eyes (about {anchor.name} → camera). Use the scale module gizmo to place it.",
					this, tag: nameof(XRControllerV2));

			Logger.LogDebug(
				$"Eye offset of the avatar: {offset} ({offset.magnitude:0.000} m, head bone scale {anchor.lossyScale.y:0.###}).",
				this, tag: nameof(XRControllerV2));
		}

		/// <summary>
		/// Tells each hand part where the wrist of this avatar sits, relative to the tracked controller.
		/// <para>
		/// Controllers are tracked at their <b>grip</b> pose - the pose OpenXR recommends for a hand or a
		/// held object - while the rig drives the hand <b>bone</b>: <c>IHand.PositionOffset</c> and
		/// <c>IHand.RotationOffset</c> say where the pivot (the controller) sits in the hand bone frame, so
		/// the hand target is their inverse. It is the mapping <c>VRIKCalibrator</c> captures when it
		/// calibrates an arm (target = tracked × inverse of the bone offset), and what the v1 rig did with
		/// its <c>HandOffset</c>.
		/// </para>
		/// </summary>
		private void ApplyHandOffsets(IAvatarDescriptor descriptor) {
			var handModule = descriptor.GetModules<IHandModule>().FirstOrDefault();
			if (handModule == null) {
				Logger.LogWarning("The avatar has no hand module: the rig keeps the raw controller pose for the hands.", this, tag: nameof(XRControllerV2));
				return;
			}

			var sources = handModule.Hands;
			for (var i = 0; i < sources.Length; i++) {
				var source = sources[i];
				if (source == null)
					continue;

				var side = source.Type == HandType.Left ? HandSide.Left : HandSide.Right;
				var hand = player?.GetHand(side);
				if (!hand) {
					// The controllers can be registered after the avatar: the pivot is applied again from
					// OnPartRegistered when the hand part appears.
					Logger.LogDebug($"No {side} hand part yet: its pivot offset waits for the part.", this, tag: nameof(XRControllerV2));
					continue;
				}

				ApplyHandOffset(source, hand);
			}
		}

		/// <summary>
		/// Gives a hand part the pivot of an avatar hand, and the hand itself as its finger source.
		/// <para>
		/// Called for every hand when the avatar arrives, and again for a hand that appears later: the tracked
		/// controllers usually register <b>after</b> the avatar was loaded, and a part without its pivot sits on
		/// the raw controller pose (wrong offset and rotation) and drives no finger.
		/// </para>
		/// </summary>
		private void ApplyHandOffset(IHand source, Hand hand) {
			var anchor = source.Anchor;
			var scale = anchor ? anchor.lossyScale : Vector3.one;

			hand.PivotOffset = Vector3.Scale(source.PositionOffset, scale);
			hand.PivotRotation = source.RotationOffset;
			hand.Source = source;

			Logger.LogDebug(
				$"{hand.Side} hand pivot: {Vector3.Scale(source.PositionOffset, scale)} ({source.PositionOffset.magnitude * scale.y:0.000} m), "
				+ $"rotation {source.RotationOffset.eulerAngles}.",
				this, tag: nameof(XRControllerV2));
		}

		/// <summary>
		/// Gives a hand part the pivot of the avatar hand of its side, when the avatar already carries one.
		/// </summary>
		/// <param name="descriptor">Descriptor of the loaded avatar.</param>
		/// <param name="hand">Hand part that just joined the player.</param>
		private void ApplyHandOffset(IAvatarDescriptor descriptor, Hand hand) {
			var handModule = descriptor?.GetModules<IHandModule>().FirstOrDefault();
			if (handModule == null || !hand)
				return;

			var sources = handModule.Hands;
			for (var i = 0; i < sources.Length; i++) {
				var source = sources[i];
				if (source == null || (source.Type == HandType.Left) != (hand.Side == HandSide.Left))
					continue;

				ApplyHandOffset(source, hand);
				return;
			}
		}

		/// <summary>Applies the avatar size to the player (the view height follows the avatar).</summary>
		private void ApplyScale(IAvatarDescriptor descriptor) {
			var scaleModule = descriptor.GetModules<IScaleAvatarModule>().FirstOrDefault();
			if (scaleModule == null) {
				Logger.LogWarning("The avatar has no scale module: the view height keeps its default.", this, tag: nameof(XRControllerV2));
				return;
			}

			if (!player)
				return;

			var height = scaleModule.Height > 0f
				? scaleModule.Height
				: defaultViewHeight;
			player.minMaxHeight = new Vector2(player.minMaxHeight.x, height);
		}

		/// <summary>Tells the avatar which trackers are active.</summary>
		private void ApplyTrackingParameters(IAvatarDescriptor descriptor) {
			var parameterModule = descriptor.GetModules<IParameterModule>().FirstOrDefault();
			if (parameterModule == null) {
				Logger.LogWarning("The avatar has no parameter module: the tracking parameters are not set.", this, tag: nameof(XRControllerV2));
				return;
			}

			var parameters = parameterModule.GetParameters();
			foreach (var parameter in parameters)
				switch (parameter.Name) {
					case "Tracking/Head/Active":
						parameter.Value = player && player.IsPartEnabled(EntityRig.Head);
						break;

					case "Tracking/LeftHand/Active":
						parameter.Value = player && player.IsPartEnabled(EntityRig.LeftHand);
						break;

					case "Tracking/RightHand/Active":
						parameter.Value = player && player.IsPartEnabled(EntityRig.RightHand);
						break;

					case "Tracking/LeftFoot/Active":
						parameter.Value = player && player.IsPartEnabled(EntityRig.LeftFoot);
						break;

					case "Tracking/RightFoot/Active":
						parameter.Value = player && player.IsPartEnabled(EntityRig.RightFoot);
						break;

					case "Tracking/LeftToes/Active":
					case "Tracking/RightToes/Active":
						parameter.Value = false;
						break;

					case "VRMode":
					case "in_vr":
					case "IsLocal":
					case "local":
					case "Rig/Ik/Head/Target":
						parameter.Value = true;
						break;
				}
		}

		/// <summary>
		/// Pushes the locomotion values the avatar animates with: the velocity is measured on the body and
		/// expressed in the frame of the view, so a locomotion blend tree makes the legs follow the player
		/// instead of leaving the avatar sliding in its idle pose.
		/// <para>
		/// Nothing is looked up here: the parameters are bound when they register (see
		/// <see cref="BindMovementParameters"/>), and the write is driven by the player events
		/// (<c>OnMove</c>, <c>OnJump</c>, <c>OnGroundedChanged</c>) instead of a per-frame poll.
		/// <c>Velocity</c> also carries the speed, its <c>/Magnitude</c> component (see
		/// <c>Vector3ParameterProvider</c>).
		/// </para>
		/// </summary>
		private void UpdateMovementParameters() {
			if (_velocityParameter != null)
				_velocityParameter.Value = ViewVelocity();

			if (_groundedParameter != null && player)
				_groundedParameter.Value = player.IsGrounded();
		}

		/// <summary>The player landed or left the ground: only the grounded parameter changed.</summary>
		private void OnGroundedChanged(bool grounded) {
			if (_groundedParameter != null)
				_groundedParameter.Value = grounded;
		}

		/// <summary>
		/// Binds the parameters this controller drives, from the registrations of the avatar: a parameter
		/// announces when it joins or leaves, so the values are written on the references directly instead
		/// of walking the list of the avatar on every frame.
		/// </summary>
		private void BindMovementParameters(IAvatarDescriptor descriptor) {
			UnbindMovementParameters();

			if (descriptor == null)
				return;

			descriptor.OnModuleRegistered.AddListener(OnMovementModuleRegistered);
			descriptor.OnModuleUnregistered.AddListener(OnMovementModuleUnregistered);

			var modules = descriptor.GetModules<IParameterModule>();
			for (var i = 0; i < modules.Length; i++)
				BindParameterModule(modules[i]);
		}

		/// <summary>Stops listening to the avatar, and forgets the parameters of the previous one.</summary>
		private void UnbindMovementParameters() {
			var descriptor = _avatar?.Descriptor;
			if (descriptor != null) {
				descriptor.OnModuleRegistered.RemoveListener(OnMovementModuleRegistered);
				descriptor.OnModuleUnregistered.RemoveListener(OnMovementModuleUnregistered);
			}

			UnbindParameterModule();
		}

		private void OnMovementModuleRegistered(IAvatarModule module) {
			if (module is IParameterModule parameterModule)
				BindParameterModule(parameterModule);
		}

		private void OnMovementModuleUnregistered(IAvatarModule module) {
			if (module is IParameterModule parameterModule && ReferenceEquals(parameterModule, _parameterModule))
				UnbindParameterModule();
		}

		/// <summary>Listens to the parameters of a module and resolves the ones this controller drives.</summary>
		private void BindParameterModule(IParameterModule module) {
			if (module == null)
				return;

			if (ReferenceEquals(module, _parameterModule)) {
				ResolveMovementParameters();
				return;
			}

			UnbindParameterModule();

			_parameterModule = module;
			module.OnRegistred.AddListener(OnMovementParameterRegistered);
			module.OnUnRegistred.AddListener(OnMovementParameterUnregistered);

			ResolveMovementParameters();
		}

		private void UnbindParameterModule() {
			if (_parameterModule != null) {
				_parameterModule.OnRegistred.RemoveListener(OnMovementParameterRegistered);
				_parameterModule.OnUnRegistred.RemoveListener(OnMovementParameterUnregistered);
			}

			_parameterModule = null;
			_velocityParameter = null;
			_groundedParameter = null;
		}

		/// <summary>
		/// Looks the two parameters up once (the module answers from a dictionary), for the avatar that is
		/// already loaded: nothing is searched again afterwards, the references are written directly.
		/// </summary>
		private void ResolveMovementParameters() {
			if (_parameterModule == null)
				return;

			_velocityParameter = _parameterModule.GetParameter(VelocityName);
			_groundedParameter = _parameterModule.GetParameter(GroundedName);
		}

		private void OnMovementParameterRegistered(IParameter parameter) {
			switch (parameter.Name) {
				case VelocityName:
					_velocityParameter = parameter;
					break;

				case GroundedName:
					_groundedParameter = parameter;
					break;

				default:
					return;
			}

			// A new parameter has no value from this controller yet: give it one now instead of waiting
			// for the next frame.
			UpdateMovementParameters();
		}

		private void OnMovementParameterUnregistered(IParameter parameter) {
			if (ReferenceEquals(parameter, _velocityParameter))
				_velocityParameter = null;
			else if (ReferenceEquals(parameter, _groundedParameter))
				_groundedParameter = null;
		}

		/// <summary>Velocity of the body in the frame of the view (its forward flattened), in m/s.</summary>
		private Vector3 ViewVelocity() {
			var world = player
				? player.LocomotionVelocity
				: Vector3.zero;

			var camera = player?.headCamera;
			var forward = camera
				? camera.transform.forward
				: transform.forward;
			forward.y = 0f;

			if (forward.sqrMagnitude < 1e-6f)
				return world;

			return Quaternion.Inverse(Quaternion.LookRotation(forward.normalized, Vector3.up))
				* world;
		}

		/// <summary>Disposes the loaded avatar.</summary>
		public void ClearAvatar() {
			_context?.Cancel();
			_context?.Dispose();
			_context = null;

			UnbindRigProvider();
			UnbindMovementParameters();

			var avatar = _avatar;
			_avatar = null;
			UnbindCalibration();
			UnsubscribeFit();
			avatar?.Dispose().Forget();
		}
	}
}
