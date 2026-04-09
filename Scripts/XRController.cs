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
using Nox.Avatars.Parameters;
using Nox.Avatars.Players;
using Nox.Avatars.Rigging;
using Nox.Avatars.Runtime.Network;
using Nox.CCK.Avatars;
using Nox.CCK.Mods.Events;
using Nox.CCK.Network;
using Nox.CCK.Players;
using Nox.CCK.Utils;
using UnityEngine;
using Logger = Nox.CCK.Utils.Logger;
using Transform = UnityEngine.Transform;
using Nox.Controllers;
using Nox.Players;
using Nox.Users;
using Nox.XR.Providers;
using RootMotion.FinalIK;
using UnityEngine.EventSystems;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace Nox.XR {
	public class XRController : MonoBehaviour, IController, IControllerAvatar, INoxObject {
		/// <summary>
		/// Check if a headset is currently connected directly via Unity XR API
		/// This is used during initialization when XRInputs.Provider might not be set yet
		/// </summary>
		private static bool HasHeadsetDirect() {
			var devices = new List<UnityEngine.XR.InputDevice>();
			UnityEngine.XR.InputDevices.GetDevicesAtXRNode(UnityEngine.XR.XRNode.Head, devices);
			return devices.Count > 0;
		}

		private static int DefaultPriority
			=> Client.Instance.IsXRInitialized() && HasHeadsetDirect()
				? Config.Load().Get("settings.controller.xr_priority", IController.DefaultPriority + 1)
				: IController.DefaultPriority - 1;

		private const string DefaultId = "xr";

		#if UNITY_EDITOR
		public static bool NoVRFlag {
			get => Config.LoadEditor().Get("no-vr", false);
			set {
				var config = Config.LoadEditor();
				config.Set("no-vr", value);
				config.Save();
			}
		}
		#else
        public static bool NoVRFlag
            => System.Array.Exists(
                System.Environment.GetCommandLineArgs(),
                arg => arg == "--no-vr"
            );
		#endif

		/// <summary>
		/// Get the proxy mod API.
		/// </summary>
		private static IControllerAPI ControllerAPI
			=> Client.CoreAPI.ModAPI
				.GetMod("controller")
				?.GetInstance<IControllerAPI>();

		/// <summary>
		/// Check if the current proxy is better than XR proxy.
		/// </summary>
		/// <returns></returns>
		private static bool IsBetterThanCurrent() {
			var controller = ControllerAPI.Current;
			return controller == null
				|| controller.GetPriority() < DefaultPriority
				|| controller.GetId() == DefaultId;
		}

		/// <summary>
		/// Check if the current proxy is the XR proxy.
		/// </summary>
		/// <returns></returns>
		private static bool IsCurrent() {
			var controller = ControllerAPI.Current;
			return controller != null
				&& controller.GetId() == DefaultId;
		}

		/// <summary>
		/// Remove the current proxy if it is the XR proxy.
		/// </summary>
		static async internal UniTask<bool> Remove() {
			if (!IsCurrent())
				return false;
			await ControllerAPI.SetCurrent(null);
			return true;
		}

		/// <summary>
		/// Create the XR proxy if it is not already created.
		/// </summary>
		/// <returns></returns>
		static async internal UniTask<bool> Make() {
			if (!IsBetterThanCurrent()) {
				Logger.LogDebug(
					"XR proxy is not better than current controller, skipping creation\n"
					+ $"Current: {ControllerAPI.Current?.GetId() ?? "null"} ({ControllerAPI.Current?.GetPriority() ?? -1})\n"
					+ $"XR: {DefaultId} ({DefaultPriority})"
					+ $" - {(Client.Instance.IsReady() ? "XR Ready" : "XR Not Ready")}"
					+ $" - {(XRInputs.HasHeadset ? "Has Headset" : "No Headset")}"
					+ $" ({(Client.Instance.IsXRInitialized() ? "XR Initialized" : "XR Not Initialized")})"
				);
				return false;
			}

			// Attendre que le système XR soit complètement initialisé
			var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));
			await Client.Instance.WaitXRInitialization(cancellationTokenSource.Token)
				.SuppressCancellationThrow();

			if (!Client.Instance.IsXRInitialized()) {
				Logger.LogError("XR system failed to initialize, cannot create XR proxy");
				return false;
			}

			var prefab = Client.CoreAPI.AssetAPI.GetAsset<GameObject>("xr_proxy.prefab");
			if (!prefab) {
				Logger.LogError("Failed to load XR proxy prefab");
				return false;
			}

			GameObject instance = null;
			try {
				// Désactiver le prefab avant l'instanciation pour éviter les problèmes d'enregistrement des pointeurs
				var wasActive = prefab.activeSelf;
				prefab.SetActive(false);

				instance = Instantiate(prefab);
				instance.SetActive(false); // Garder l'instance désactivée
				instance.transform.position   = Vector3.zero;
				instance.transform.rotation   = Quaternion.identity;
				instance.transform.localScale = Vector3.one;

				prefab.SetActive(wasActive);

			} catch (Exception e) {
				Logger.LogError("Failed to instantiate XR proxy prefab: " + e);
				instance?.Destroy();
				return false;
			}

			var xr = instance?.GetComponent<XRController>();

			if (!xr) {
				Logger.LogError("Failed to get XR proxy component");
				Destroy(instance);
				return false;
			}

			// Désactiver l'EventSystem pour éviter les conflits
			if (xr.eventSystem)
				xr.eventSystem.enabled = false;

			await xr.Menu.Generate();

			if (!await ControllerAPI.SetCurrent(xr)) {
				Logger.LogError("Failed to set XR proxy as current");
				Destroy(instance);
				return false;
			}


			xr.gameObject.name = $"[{xr.GetType().Name}_{xr.GetInstanceID()}]";
			DontDestroyOnLoad(xr);

			// Attendre plusieurs frames avant d'activer pour permettre au système XR de s'initialiser
			await UniTask.DelayFrame(3, cancellationToken: cancellationTokenSource.Token);

			// Activer l'instance maintenant que tout est configuré
			instance.SetActive(true);

			// Réactiver l'EventSystem après activation
			if (xr.eventSystem)
				xr.eventSystem.enabled = true;

			// if (xr._attachedRuntimeAvatar == null)
			//  	xr.SetupAvatar().Forget();

			xr._onUserUpdate = Client.CoreAPI.EventAPI.Subscribe("user_update", xr.OnUserUpdate);
			Keybindings.Rebind();

			return true;
		}

		[NoxPublic(NoxAccess.Method)]
		public string GetId()
			=> DefaultId;

		[NoxPublic(NoxAccess.Method)]
		public int GetPriority()
			=> DefaultPriority;

		public AutoHandPlayer player;
		public bool mayFly;

		public XRMenuProvider Menu;

		public EventSystem eventSystem;
		private IPlayer _attachedPlayer;
		public XRInteractionGroup[] interactions;

		// Avatar management fields
		private IRuntimeAvatar _attachedRuntimeAvatar;
		private Identifier _avatarIdentifier;
		private CancellationTokenSource _avatarLoadingCts;
		private EventSubscription _onUserUpdate;

		private XRController()
			=> _avatarParameters = new Dictionary<string, object> {
				["source"] = this,
				["xr"]     = true,
				["local"]  = true
			};


		public void Dispose() {
			if (XRInputs.Provider is AutoHandProvider)
				XRInputs.Provider = null;
			Client.CoreAPI.EventAPI.Unsubscribe(_onUserUpdate);
			Keybindings.Clear();
			Menu.Dispose();
			_onUserUpdate = null;
			_avatarLoadingCts?.Cancel();
			_avatarLoadingCts?.Dispose();
			_avatarLoadingCts = null;
			_attachedRuntimeAvatar?.Dispose();
			_attachedRuntimeAvatar = null;
			Destroy(gameObject);
		}

		[NoxPublic(NoxAccess.Method)]
		public Camera GetCamera()
			=> player.headCamera;

		public EventSystem GetEventSystem()
			=> eventSystem;

		[NoxPublic(NoxAccess.Method)]
		public Collider GetCollider()
			=> player.bodyCollider;

		public UniTask Restore(IController controller) {
			foreach (var ability in controller.GetAbilities())
				SetAbilities(ability.Key, ability.Value);

			if (controller is IControllerAvatar ca) {
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

		private static void AddFingerParts(Dictionary<ushort, Transform> parts, Hand hand,
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

		IReadOnlyDictionary<ushort, TransformObject> IController.GetParts()
			=> GetParts().ToDictionary(kv => kv.Key, kv => new TransformObject(kv.Value, kv.Value.GetComponent<Rigidbody>()));

		public IRuntimeAvatar GetAvatar()
			=> _attachedRuntimeAvatar;

		public async UniTask<bool> SetAvatar(IRuntimeAvatar runtimeAvatar) {
			return false;
			Logger.LogDebug("Setting avatar for XRController");

			// Vérifier que le controller n'est pas détruit
			if (this == null || gameObject == null) {
				Logger.LogError("XRController has been destroyed, cannot set avatar");
				return false;
			}

			if (runtimeAvatar == _attachedRuntimeAvatar)
				return true;

			var old = _attachedRuntimeAvatar;
			_attachedRuntimeAvatar = runtimeAvatar;

			if (_attachedRuntimeAvatar == null) {
				Logger.LogWarning("Setting avatar to null, removing current avatar.");
				_attachedRuntimeAvatar = old;
				return false;
			}

			var descriptor = _attachedRuntimeAvatar.Descriptor;
			if (descriptor == null) {
				Logger.LogError("Avatar descriptor is null, cannot set avatar.");
				_attachedRuntimeAvatar = old;
				return false;
			}

			var root = descriptor.GetAnchor();
			if (!root) {
				Logger.LogError("Avatar descriptor root is null, cannot set avatar.");
				_attachedRuntimeAvatar = old;
				return false;
			}

			root.name += $" {runtimeAvatar.Identifier.ToString()} XR";

			if (old != null)
				await old.Dispose();

			Logger.LogDebug($"Attaching avatar to {runtimeAvatar.Descriptor}", runtimeAvatar.Descriptor.GetAnchor());
			root.transform.SetParent(transform, false);
			root.transform.localPosition = Vector3.zero;
			root.transform.localRotation = Quaternion.identity;

			var parameterModule = _attachedRuntimeAvatar?.Descriptor
				?.GetModules<IParameterModule>()
				.FirstOrDefault();

			if (parameterModule == null) {
				Logger.LogWarning("Avatar has no parameter module, cannot configure tracking parameters.");
				return true;
			}

			var parameters = parameterModule.GetParameters();
			if (parameters != null) {
				foreach (var param in parameters) {
					var n = param.GetName();
					switch (n) {
						case "tracking/head/active":
							param.Set(XRInputs.HasHeadset);
							break;
						case "tracking/left_hand/active":
							param.Set(XRInputs.HasHandLeft);
							break;
						case "tracking/right_hand/active":
							param.Set(XRInputs.HasHandRight);
							break;
						case "tracking/left_foot/active":
							// param.Set(Client.Instance.HasFootLeft());
							param.Set(false);
							break;
						case "tracking/right_foot/active":
							// param.Set(Client.Instance.HasFootRight());
							param.Set(false);
							break;
						case "VRMode" or "in_vr":
						case "IsLocal" or "local":
						case "rig/ik/head/target":
							param.Set(true);
							break;
					}
				}
			}

			root.SetActive(true);

			#if HAS_FINALIK
			if (player != null && root.TryGetComponent<VRIK>(out var component)) {
				var proxy = component.GetOrAddComponent<AutoHandVRIK>();
				if (player.handRight != null) {
					proxy.rightHand              = player.handRight;
					proxy.rightTrackedController = player.handRight.transform;
				}

				if (player.handLeft != null) {
					proxy.leftHand              = player.handLeft;
					proxy.leftTrackedController = player.handLeft.transform;
				}
			}
			#endif

			Client.CoreAPI.EventAPI.Emit("controller_avatar_changed", this, _attachedRuntimeAvatar);

			return true;
		}

		[NoxPublic(NoxAccess.Method)]
		public IPlayer GetPlayer()
			=> _attachedPlayer;

		private void Start()
			=> StartupAutoHand().Forget();

		private async UniTask StartupAutoHand() {
			// Vérification des références nulles
			if (!player) {
				Logger.LogError("XRController.player is null in StartupAutoHand");
				return;
			}

			if (!player.bodyCollider) {
				Logger.LogError("XRController.player.bodyCollider is null in StartupAutoHand");
				return;
			}

			player.bodyCollider.material = new PhysicsMaterial {
				dynamicFriction = 0f,
				staticFriction  = 0f,
				bounciness      = 0f,
				frictionCombine = PhysicsMaterialCombine.Maximum,
				bounceCombine   = PhysicsMaterialCombine.Average
			};

			if (interactions == null || interactions.Length == 0) {
				Logger.LogWarning("XRController.interactions is null or empty in StartupAutoHand");
				return;
			}

			foreach (var interaction in interactions) {
				if (!interaction)
					continue;
				interaction.gameObject.SetActive(false);
				foreach (var member in interaction.startingGroupMembers)
					if (member is MonoBehaviour mb)
						mb.gameObject.SetActive(false);
			}

			await UniTask.NextFrame();

			foreach (var interaction in interactions) {
				if (!interaction)
					continue;
				interaction.gameObject.SetActive(true);
				foreach (var member in interaction.startingGroupMembers)
					if (member is MonoBehaviour mb)
						mb.gameObject.SetActive(true);
			}

			XRInputs.Provider = new AutoHandProvider();
		}

		private void Update() {
			SynchronizeParametersAvatar();
		}

		// private void LateUpdate()
		// 	=> UpdateCamera();

		private void OnUserUpdate(EventData context) {
			if (!context.TryGet(0, out ICurrentUser user) || user == null || !IsCurrent())
				return;
			LoadAvatarFromUser(user);
		}

		private void LoadAvatarFromUser(ICurrentUser user)
			=> SetAvatar(user.Avatar).Forget();

		private readonly Dictionary<string, object> _avatarParameters;

		public async UniTask<IRuntimeAvatar> SetAvatar(Identifier identifier, Action<string, float> progress = null) {
			return null;

			Logger.LogDebug($"Loading avatar for identifier {identifier.ToString()}");

			var playerAvatar = _attachedPlayer as ILocalPlayerAvatar;

			if (!identifier.IsValid()) {
				if (playerAvatar != null)
					await playerAvatar.OnAvatarFailed(new Exception("Invalid avatar identifier."));
				return null;
			}

			if (identifier.Equals(_avatarIdentifier)) {
				if (playerAvatar != null)
					await playerAvatar.OnAvatarReady();
				return _attachedRuntimeAvatar;
			}

			_avatarLoadingCts?.Cancel();
			_avatarLoadingCts = new CancellationTokenSource();

			var req = new AssetSearchRequest {
				Engines   = new[] { EngineExtensions.CurrentEngine.GetEngineName() },
				Platforms = new[] { PlatformExtensions.CurrentPlatform.GetPlatformName() },
				Versions  = new[] { identifier.GetVersion() },
				Limit     = 1
			};

			var asset = (await Client.AvatarAPI.SearchAssets(identifier, req)
					.AttachExternalCancellation(_avatarLoadingCts.Token)).Items
				.FirstOrDefault();
			if (_avatarLoadingCts.IsCancellationRequested)
				return null;

			if (asset == null) {
				Logger.LogWarning($"Avatar asset not found for identifier {identifier.ToString()}");
				var err = await Client.AvatarAPI.LoadError(_avatarParameters);
				err.Identifier = identifier;
				await SetAvatar(err);
				if (playerAvatar != null)
					await playerAvatar.OnAvatarFailed(new Exception("Avatar asset not found."));
				return null;
			}

			if (!Client.AvatarAPI.HasInCache(asset.Hash)) {
				var download = Client.AvatarAPI.DownloadToCache(
					asset.Url,
					hash: asset.Hash,
					progress: p => progress?.Invoke($"Downloading avatar {identifier.ToString()}", p),
					token: _avatarLoadingCts.Token
				);
				await download.Start();
				if (_avatarLoadingCts.IsCancellationRequested)
					return null;
			}

			var avatar = await Client.AvatarAPI.LoadFromCache(
				asset.Hash,
				_avatarParameters,
				progress: p => progress?.Invoke($"Loading avatar {identifier.ToString()}", p),
				token: _avatarLoadingCts.Token
			);
			if (_avatarLoadingCts.IsCancellationRequested)
				return null;

			if (avatar == null) {
				Logger.LogError($"Failed to load avatar from cache for identifier {identifier.ToString()}");
				var err = await Client.AvatarAPI.LoadError(_avatarParameters);
				err.Identifier = identifier;
				await SetAvatar(err);
				if (playerAvatar != null)
					await playerAvatar.OnAvatarFailed(new Exception("Failed to load avatar from cache."));
				return null;
			}

			Logger.LogDebug($"Avatar loaded: {identifier.ToString()}");
			avatar.Identifier = identifier;
			await SetAvatar(avatar);
			if (playerAvatar != null)
				await playerAvatar.OnAvatarReady();
			return avatar;
		}

		private async UniTask SetupAvatar() {
			if (_attachedRuntimeAvatar != null) {
				Logger.LogDebug("Avatar already set for XRController");
				return;
			}

			Logger.LogDebug("Creating avatar");

			try {
				// Vérifier que les APIs sont disponibles
				if (Client.AvatarAPI == null) {
					Logger.LogError("AvatarAPI is null, cannot setup avatar");
					return;
				}

				if (Client.UserAPI == null) {
					Logger.LogError("UserAPI is null, cannot setup avatar");
					return;
				}

				var avatar = await Client.AvatarAPI.LoadLoading(_avatarParameters);
				if (avatar == null) {
					Logger.LogError("Failed to create avatar for XRController");
					return;
				}

				await SetAvatar(avatar);

				var currentUser = Client.UserAPI.Current;
				if (currentUser != null) {
					LoadAvatarFromUser(currentUser);
				} else {
					Logger.LogWarning("No current user available for avatar loading");
				}
			} catch (Exception e) {
				Logger.LogError($"Exception in SetupAvatar: {e}");
			}
		}

		// ReSharper disable Unity.PerformanceAnalysis
		private void SynchronizeParametersAvatar() {
			var parameterModule = _attachedRuntimeAvatar?.Descriptor
				?.GetModules<IParameterModule>()
				.FirstOrDefault();
			var cameraModule = _attachedRuntimeAvatar?.Descriptor
				?.GetModules<ICameraModule>()
				.FirstOrDefault();
			var riggingModule = _attachedRuntimeAvatar?.Descriptor
				?.GetModules<IRiggingModule>()
				.FirstOrDefault();
			if (parameterModule == null)
				return;
			var parameters = parameterModule.GetParameters();
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
						var worldVelocity = player.body?.linearVelocity ?? Vector3.zero;
						var localVelocity = transform.InverseTransformDirection(worldVelocity);
						var value         = param.Get().ToFloat();
						if (Mathf.Approximately(value, localVelocity.x))
							continue;
						param.Set(localVelocity.x);
						break;
					}
					case "VelocityY": {
						var worldVelocity = player.body?.linearVelocity ?? Vector3.zero;
						var localVelocity = transform.InverseTransformDirection(worldVelocity);
						var value         = param.Get().ToFloat();
						if (Mathf.Approximately(value, localVelocity.y))
							continue;
						param.Set(localVelocity.y);
						break;
					}
					case "VelocityZ": {
						var worldVelocity = player.body?.linearVelocity ?? Vector3.zero;
						var localVelocity = transform.InverseTransformDirection(worldVelocity);
						var value         = param.Get().ToFloat();
						if (Mathf.Approximately(value, localVelocity.z))
							continue;
						param.Set(localVelocity.z);
						break;
					}
					case "Velocity": {
						var worldVelocity = player.body?.linearVelocity ?? Vector3.zero;
						var localVelocity = transform.InverseTransformDirection(worldVelocity);
						var value         = param.Get().ToVector3();
						if (value == localVelocity)
							continue;
						param.Set(localVelocity);
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

					// Tracking de la tête - position et rotation
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

						if (riggingModule != null && cameraModule != null) {
							var headBone = riggingModule.GetBone(HumanBodyBones.Head);
							if (headBone == cameraModule.GetAnchor())
								cPos += cameraModule.GetOffset();
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

					// Tracking des mains - position et rotation
					case "tracking/left_hand/active": {
						var active = XRInputs.HasHandLeft;
						var value  = param.Get().ToBool();
						if (value == active)
							continue;
						param.Set(active);
						break;
					}
					case "tracking/left_hand/position": {
						if (!player.handLeft)
							continue;
						var cPos  = player.handLeft.transform.position;
						var value = param.Get().ToVector3();
						if (Vector3.Distance(value, cPos) < 0.001f)
							continue;
						param.Set(cPos);
						break;
					}
					case "tracking/left_hand/rotation": {
						if (!player.handLeft)
							continue;
						var cRot  = player.handLeft.transform.rotation;
						var value = param.Get().ToQuaternion();
						if (Quaternion.Angle(value, cRot) < 0.001f)
							continue;
						param.Set(cRot);
						break;
					}

					case "tracking/right_hand/active": {
						var active = XRInputs.HasHandRight;
						var value  = param.Get().ToBool();
						if (value == active)
							continue;
						param.Set(active);
						break;
					}
					case "tracking/right_hand/position": {
						if (!player.handRight)
							continue;
						var cPos  = player.handRight.transform.position;
						var value = param.Get().ToVector3();
						if (Vector3.Distance(value, cPos) < 0.001f)
							continue;
						param.Set(cPos);
						break;
					}
					case "tracking/right_hand/rotation": {
						if (!player.handRight)
							continue;
						var cRot  = player.handRight.transform.rotation;
						var value = param.Get().ToQuaternion();
						if (Quaternion.Angle(value, cRot) < 0.001f)
							continue;
						param.Set(cRot);
						break;
					}

					// ...existing code for other tracking parameters...
				}
			}
		}


		// ReSharper disable Unity.PerformanceAnalysis
		public void SetPart(ushort index, TransformObject tr) {
			Rigidbody rb;

			if (index == PlayerRig.Base.ToIndex()) {
				if (!tr.IsSamePosition(player.transform.position))
					player.SetPosition(tr.GetPosition());

				if (!tr.IsSameRotation(player.transform.rotation))
					player.SetRotation(tr.GetRotation());

				rb = player.body;

				if (rb && !tr.IsSameVelocity(rb.linearVelocity))
					rb.linearVelocity = tr.GetVelocity();

				if (rb && !tr.IsSameAngular(rb.angularVelocity))
					rb.angularVelocity = tr.GetAngular();
				return;
			}

			var part = GetParts()
				.FirstOrDefault(p => p.Key == index);

			if (!part.Value)
				return;

			if (!tr.IsSamePosition(part.Value.position))
				part.Value.position = tr.GetPosition();

			if (!tr.IsSameRotation(part.Value.rotation))
				part.Value.rotation = tr.GetRotation();

			rb = part.Value.GetComponent<Rigidbody>();

			if (rb && !tr.IsSameVelocity(rb.linearVelocity))
				rb.linearVelocity = tr.GetVelocity();

			if (rb && !tr.IsSameAngular(rb.angularVelocity))
				rb.angularVelocity = tr.GetAngular();
		}

		private void SynchronizeControllerFromPlayer() {
			if (_attachedPlayer == null)
				return;
			Logger.LogDebug($"Synchronizing controller from player at {_attachedPlayer.Position} with rotation {_attachedPlayer.Rotation}");
			player.SetPosition(_attachedPlayer.Position);
			player.SetRotation(_attachedPlayer.Rotation);
		}
	}
}