using System;
using System.Threading;
using Autohand;
using Nox.CCK.XR;
using Cysharp.Threading.Tasks;
using Nox.Avatars.Controllers;
using Nox.CCK.Nameplate;
using Nox.CCK.Utils;
using Nox.Audio.Players;
using Nox.Sessions;
using UnityEngine;
using Logger = Nox.CCK.Utils.Logger;
using Nox.Controllers;
using Nox.Players;
using Nox.XR.Runtime.Connectors;
using Nox.XR.Runtime.Providers;
using UnityEngine.EventSystems;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using Unity.XR.CoreUtils;
using Nox.XR.Runtime.Loaders;

namespace Nox.XR.Runtime {
	[DefaultExecutionOrder(15)]
public partial class XRController : MonoBehaviour, IController, IControllerAvatar, IXRController, INoxObject, INameplateHolder {

		private static int DefaultPriority
			=> XRLoaderManager.IsRunning && XRInputs.HasHeadset
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

		public static async UniTask<bool> Make() {
			if (!IsBetterThanCurrent()) {
				Logger.LogError("XR is not better than the current.");
				return false;
			}

			var wait = new CancellationTokenSource(TimeSpan.FromSeconds(30));
			await Client.Instance.WaitReady(wait.Token)
				.SuppressCancellationThrow();

			if (!XRLoaderManager.IsRunning) {
				Logger.LogError("XR system failed to initialize, cannot create XR proxy");
				return false;
			}

			var prefab = Client.CoreAPI.AssetAPI.GetAsset<GameObject>("xr_proxy.prefab");
			if (!prefab) {
				Logger.LogError("Failed to load XR proxy prefab");
				return false;
			}

			var xr = await prefab.InstantiateAsync<XRController>();
			if (!xr) {
				Logger.LogError("Failed to get XR proxy component");
				return false;
			}

			xr.gameObject.name = $"[{xr.GetType().Name}_{xr.GetEntityId().GetHashCode()}]";

			if (xr.eventSystem)
				xr.eventSystem.enabled = false;

			await xr.Menu.Generate();

			await UniTask.NextFrame(cancellationToken: wait.Token);

			if (!await ControllerAPI.SetCurrent(xr)) {
				Logger.LogError("Failed to set XR proxy as current");
				xr.gameObject.Destroy();
				return false;
			}

			await UniTask.DelayFrame(3, cancellationToken: wait.Token);

			if (xr.eventSystem)
				xr.eventSystem.enabled = true;

			xr.avatarLoader.SetupAvatar().Forget();

			return true;
		}

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
			DisposeNameplate();

			_sessionApi?.OnCurrentChanged.RemoveListener(OnSessionChanged);
			microphone?.Unbind();
			if (XRInputs.Provider is AutoHandProvider)
				XRInputs.Provider = null;
			avatarLoader?.ClearRig();
			avatarLoader?.Dispose();
			Menu.Dispose();
			Destroy(gameObject);
		}

		private void Awake() {
			SetupNameplate();

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

		private void Start() {
			StartupAutoHand().Forget();
			AutoFixViewHeight().Forget();
		}

		/// <summary>
		/// Replace la vue à la hauteur recommandée si elle est née sous le sol (aucun casque,
		/// simulateur, ou OpenXR rapportant une origine au sol). Ne touche à rien si la hauteur
		/// mesurée est plausible, afin de ne jamais déplacer un vrai casque correctement suivi.
		/// </summary>

		private void SynchronizeControllerFromPlayer() {
			if (_attachedPlayer == null)
				return;
			Logger.LogDebug($"Synchronizing controller from player at {_attachedPlayer.Position} with rotation {_attachedPlayer.Rotation}");
			player.SetPosition(_attachedPlayer.Position);
			player.SetRotation(_attachedPlayer.Rotation);
		}
	}
}
