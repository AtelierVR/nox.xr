using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Autohand;
using Nox.CCK.XR;
using Cysharp.Threading.Tasks;
using Nox.Avatars;
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
using Nox.XR.Connectors;
using Nox.XR.Providers;
using UnityEngine.EventSystems;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR;
using Unity.XR.CoreUtils;

namespace Nox.XR {
	[DefaultExecutionOrder(15)]
	public class XRController : MonoBehaviour, IController, IControllerAvatar, IXRController, INoxObject {
		private static readonly List<InputDevice> _devices = new();

		/// <summary>
		/// Check if a headset is currently connected directly via Unity XR API
		/// This is used during initialization when XRInputs.Provider might not be set yet
		/// </summary>
		private static bool HasHeadset() {
			_devices.Clear();
			InputDevices.GetDevicesAtXRNode(XRNode.Head, _devices);
			return _devices.Count > 0;
		}

		private static int DefaultPriority
			=> Client.Instance.IsXRInitialized() && HasHeadset()
				? Config.Load().Get("settings.controller.xr_priority", IController.DefaultPriority + 1)
				: IController.DefaultPriority - 1;

		private const string DefaultId = "xr";

		/// <summary>
		/// Get the proxy mod API.
		/// </summary>
		private static IControllerAPI ControllerAPI
			=> Client.CoreAPI.ModAPI
				.GetMod("controllers")
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
			// Deux Make() concurrents (plusieurs évènements deviceConnected pendant l'init
			// XR) créent deux proxys qui se détruisent l'un l'autre : le proxy détruit
			// garde un Start() en attente et le contrôleur courant devient introuvable
			// pour les autres mods (relay, keybindings, ...).
			if (_creating) {
				Logger.LogDebug("XR proxy creation already in progress, skipping duplicate request.");
				return false;
			}

			_creating = true;
			try {
				return await MakeInternal();
			} finally {
				_creating = false;
			}
		}

		private static bool _creating;

		static async private UniTask<bool> MakeInternal() {
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

			// Générer le menu AVANT SetCurrent : SetCurrent déclenche Restore() sur
			// l'ancien contrôleur, qui peut pousser un avatar dans notre loader alors
			// qu'il n'est pas encore prêt.
			await xr.Menu.Generate();

			xr.gameObject.name = $"[{xr.GetType().Name}_{xr.GetEntityId().GetHashCode()}]";
			DontDestroyOnLoad(xr);

			// Activer AVANT SetCurrent :
			//  - Main.SetCurrent affecte EventSystem.current, et Unity refuse un
			//    EventSystem encore inactif (modules non enregistrés) ->
			//    "Failed setting EventSystem.current ... No module".
			//  - le proxy doit être actif pour que Restore() puisse le configurer.
			instance.SetActive(true);
			await UniTask.NextFrame(cancellationToken: cancellationTokenSource.Token);

			if (!await ControllerAPI.SetCurrent(xr)) {
				Logger.LogError("Failed to set XR proxy as current");
				Destroy(instance);
				return false;
			}

			// Attendre plusieurs frames avant d'activer pour permettre au système XR de s'initialiser
			await UniTask.DelayFrame(3, cancellationToken: cancellationTokenSource.Token);

			// Réactiver l'EventSystem après activation
			if (xr.eventSystem)
				xr.eventSystem.enabled = true;

			// Ne charger l'avatar que si Restore()/SetupAvatar() ne l'a pas déjà fait :
			// deux chargements concurrents s'annulent mutuellement et laissent le
			// loader sans avatar.
			if (xr.avatarLoader?.GetAvatar() == null)
				await xr.avatarLoader.SetupAvatar();

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

		[Header("View")]
		[Tooltip("Replace automatiquement la vue à la hauteur recommandée si elle démarre sous le sol "
		         + "(aucun casque / pose de tête non suivie). Sans effet si la hauteur est déjà plausible.")]
		[SerializeField] private bool autoFixViewHeight = true;

		[Tooltip("Hauteur (m) utilisée comme cible tant qu'aucun avatar n'a fourni sa taille.")]
		[SerializeField] private float defaultViewHeight = DefaultViewHeight;

		/// <summary>Hauteur de vue par défaut quand l'avatar n'a pas encore renseigné sa taille.</summary>
		private const float DefaultViewHeight = 1.7f;

		/// <summary>
		/// Défaut du prefab Autohand pour <see cref="AutoHandPlayer.minMaxHeight"/> : tant que
		/// l'avatar ne l'a pas remplacé, cette valeur ne représente pas une taille de joueur.
		/// </summary>
		private const float AutoHandDefaultMaxHeight = 2.5f;

		/// <summary>
		/// En dessous de cette hauteur, la vue est considérée comme née sous le sol.
		/// </summary>
		private const float MinPlausibleViewHeight = 0.5f;

		/// <summary>
		/// Délai max (s) d'attente de l'avatar avant la correction automatique de hauteur.
		/// </summary>
		private const float AutoFixWaitSeconds = 5f;

		private XROrigin _xrOrigin;

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

		IReadOnlyDictionary<ushort, TransformObject> IController.GetParts()
			=> GetParts().ToDictionary(kv => kv.Key, kv => new TransformObject(kv.Value, kv.Value.GetComponent<Rigidbody>()));

		public IRuntimeAvatar GetAvatar()
			=> avatarLoader?.GetAvatar();

		public async UniTask<bool> SetAvatar(IRuntimeAvatar runtimeAvatar)
			=> avatarLoader != null && await avatarLoader.SetAvatar(runtimeAvatar);

		[NoxPublic(NoxAccess.Method)]
		public IPlayer GetPlayer()
			=> _attachedPlayer;

		private void Start() {
			StartupAutoHand().Forget();
			AutoFixViewHeight().Forget();
		}

		/// <summary>
		/// Replace la vue à la hauteur recommandée si elle est née sous le sol (aucun casque,
		/// simulateur, ou OpenXR rapportant une origine au sol). Ne touche à rien si la hauteur
		/// mesurée est plausible, afin de ne jamais déplacer un vrai casque correctement suivi.
		/// </summary>
		private async UniTask AutoFixViewHeight() {
			if (!autoFixViewHeight)
				return;

			// La hauteur recommandée vient de la taille de l'avatar : on attend qu'il soit
			// chargé, sans bloquer indéfiniment s'il n'en arrive aucun.
			using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(AutoFixWaitSeconds + 1));
			var token = timeout.Token;

			// Laisser le tracking et l'initialisation XR se stabiliser avant de juger la hauteur.
			await UniTask.Delay(TimeSpan.FromSeconds(1), cancellationToken: token)
				.SuppressCancellationThrow();

			await UniTask.WaitUntil(
					() => !this || !player || (avatarLoader && avatarLoader.GetAvatar() != null),
					cancellationToken: token
				)
				.SuppressCancellationThrow();

			if (!this || !player)
				return;

			var height = GetViewHeight();
			if (height >= MinPlausibleViewHeight)
				return;

			Logger.LogWarning(
				$"XR view started at {height:F2} m (below {MinPlausibleViewHeight:F2} m), "
				+ $"recentering to the recommended height ({GetRecommendedHeight():F2} m).",
				this
			);
			ReCenterAndReHeight();
		}

		private async UniTask StartupAutoHand() {
			// Vérification des références nulles
			if (!player) {
				// Un proxy détruit avant son premier Start() (bascule de contrôleur pendant
				// l'init XR) est un cas normal : on ne logue que si le composant est vivant.
				if (!this)
					return;

				// Sans aucune référence configurée, ce n'est pas le proxy : c'est le
				// XRController vide ajouté automatiquement par un [RequireComponent] sur un
				// GameObject enfant du proxy. On le neutralise sans bruit.
				if (avatarLoader == null && Menu == null && microphone == null) {
					Logger.LogWarning(
						$"{nameof(XRController)} on '{name}' is not configured (no player, avatarLoader, Menu "
						+ "or microphone): redundant component, likely auto-added by [RequireComponent] on a "
						+ "child of the proxy. It is disabled — remove it from the prefab.",
						this
					);
					enabled = false;
					return;
				}

				Logger.LogError($"XRController.player is null in StartupAutoHand on '{name}'.", this);
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

		private void SynchronizeControllerFromPlayer() {
			if (_attachedPlayer == null)
				return;
			Logger.LogDebug($"Synchronizing controller from player at {_attachedPlayer.Position} with rotation {_attachedPlayer.Rotation}");
			player.SetPosition(_attachedPlayer.Position);
			player.SetRotation(_attachedPlayer.Rotation);
		}

	}
}