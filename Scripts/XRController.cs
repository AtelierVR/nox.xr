using System;
using System.Collections.Generic;
using Nox.CCK;
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
using Nox.Avatars.Scale;
using Nox.CCK.Avatars;
using Nox.CCK.Mods.Events;
using Nox.CCK.Network;
using Nox.CCK.Players;
using Nox.CCK.Utils;
using Nox.Microphone.Players;
using Nox.Sessions;
using UnityEngine;
using Logger = Nox.CCK.Utils.Logger;
using Transform = UnityEngine.Transform;
using Nox.Controllers;
using Nox.Players;
using Nox.Users;
using Nox.XR.Connectors;
using Nox.XR.Providers;
using UnityEngine.EventSystems;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace Nox.XR {
	[UnityEngine.DefaultExecutionOrder(15)]
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

		/// <summary>
		/// Get the proxy mod API.
		/// </summary>
		private static IControllerAPI ControllerAPI
			=> Client.CoreAPI.ModAPI
				.GetMod("controller")
				?.GetInstance<IControllerAPI>();

		private static ISessionAPI SessionAPI
			=> Client.CoreAPI.ModAPI
				.GetMod("session")
				?.GetInstance<ISessionAPI>();

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
		internal static bool IsCurrent() {
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


			xr.gameObject.name = $"[{xr.GetType().Name}_{xr.GetEntityId().GetHashCode()}]";
			DontDestroyOnLoad(xr);

			// Attendre plusieurs frames avant d'activer pour permettre au système XR de s'initialiser
			await UniTask.DelayFrame(3, cancellationToken: cancellationTokenSource.Token);

			// Activer l'instance maintenant que tout est configuré
			instance.SetActive(true);

			// Réactiver l'EventSystem après activation
			if (xr.eventSystem)
				xr.eventSystem.enabled = true;

				if (xr.avatarLoader?.GetAvatar() == null)
				xr.avatarLoader?.SetupAvatar().Forget();

			xr.avatarLoader?.StartUserTracking();
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
		public AvatarLoaderConnector avatarLoader;
		public AvatarSyncConnector avatarSync;
		public bool mayFly;

		public XRMenuProvider Menu;

		public EventSystem eventSystem;
		private IPlayer _attachedPlayer;
		public XRInteractionGroup[] interactions;
		[SerializeField] public MicrophoneConnector microphone;

		private ISessionAPI _sessionApi;


		public void Dispose() {
			_sessionApi?.OnCurrentChanged.RemoveListener(OnSessionChanged);
			microphone?.Unbind();
			if (XRInputs.Provider is AutoHandProvider)
				XRInputs.Provider = null;
			avatarLoader?.ClearRig();
			avatarLoader?.Dispose();
			Keybindings.Clear();
			Menu.Dispose();
			Destroy(gameObject);
		}

		private void Awake() {
			_sessionApi = SessionAPI;
			if (_sessionApi == null) return;
			_sessionApi.OnCurrentChanged.AddListener(OnSessionChanged);
			if (_sessionApi.Current != null && _sessionApi.TryGet(_sessionApi.Current, out var current))
				OnSessionChanged(null, current);
		}

		private void OnSessionChanged(ISession old, ISession next) {
			if (microphone == null) return;
			microphone.Unbind();
			if (next?.LocalPlayer is ILocalPlayerVoice voice)
				microphone.Bind(voice);
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

		IReadOnlyDictionary<ushort, TransformObject> IController.GetParts()
			=> GetParts().ToDictionary(kv => kv.Key, kv => new TransformObject(kv.Value, kv.Value.GetComponent<Rigidbody>()));

		public IRuntimeAvatar GetAvatar()
			=> avatarLoader?.GetAvatar();

		public async UniTask<bool> SetAvatar(IRuntimeAvatar runtimeAvatar)
			=> avatarLoader != null && await avatarLoader.SetAvatar(runtimeAvatar);

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



		public async UniTask<IRuntimeAvatar> SetAvatar(Identifier identifier, Action<string, float> progress = null)
			=> avatarLoader != null ? await avatarLoader.SetAvatar(identifier, progress) : null;

		public async UniTask<IRuntimeAvatar> ReloadAvatar(Action<string, float> progress = null)
			=> avatarLoader != null ? await avatarLoader.ReloadAvatar(progress) : null;


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