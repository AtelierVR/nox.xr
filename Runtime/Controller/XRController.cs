using System;
using System.Threading;
using Autohand;
using Nox.CCK.XR;
using Cysharp.Threading.Tasks;
using Nox.Avatars.Controllers;
using Nox.CCK.Nameplate;
using Nox.CCK.Utils;
using UnityEngine;
using Logger = Nox.CCK.Utils.Logger;
using Nox.Controllers;
using Nox.Players;
using Nox.XR.Runtime.Connectors;
using Nox.XR.Runtime.FullBody;
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
			// Idempotent: the proxy is already the current controller.
			if (IsCurrent()) {
				Logger.LogDebug("XR proxy already current, nothing to create.", tag: nameof(XRController));
				return true;
			}

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

		/// <summary>
		/// Proxy nodes, split by responsibility. The container (<c>AutoHandPlayer.trackingContainer</c>) holds
		/// both and belongs to the AutoHandPlayer: locomotion, rotation and <c>heightOffset</c>.
		/// <code>
		/// xr_proxy
		/// └── Container     ← AutoHandPlayer.trackingContainer
		///     ├── Trackers  ← <see cref="trackers"/>
		///     └── Avatar    ← <see cref="avatarContainer"/>
		/// </code>
		/// </summary>
		[Header("Proxy nodes")]
		[Tooltip("Node holding the player pose inside the container: head camera, controllers and hands. It is "
		         + "the one scaled to fit the player pose to the avatar, and shifted to adjust the view height. "
		         + "Never the container: the container holds the avatar.")]
		public Transform trackers;

		[Tooltip("Root node of the avatar (its container), a child of the AutoHandPlayer container. It follows "
		         + "the container in position and rotation, and is never scaled: the avatar carries its own scale "
		         + "(see IScaleAvatarModule).")]
		public Transform avatarContainer;

		[Header("View")]
		[Tooltip("Replace automatiquement la vue à la hauteur recommandée si elle démarre sous le sol "
		         + "(aucun casque / pose de tête non suivie). Sans effet si la hauteur est déjà plausible.")]
		[SerializeField] private bool autoFixViewHeight = true;

		[Tooltip("Hauteur (m) utilisée comme cible tant qu'aucun avatar n'a fourni sa taille.")]
		[SerializeField] private float defaultViewHeight = DefaultViewHeight;

		/// <summary>Default view height, used until an avatar provides its own size.</summary>
		private const float DefaultViewHeight = 1.7f;

		/// <summary>Autohand prefab default for <see cref="AutoHandPlayer.minMaxHeight"/>.</summary>
		private const float AutoHandDefaultMaxHeight = 2.5f;

		/// <summary>Below this height the view is considered to start under the floor.</summary>
		private const float MinPlausibleViewHeight = 0.5f;

		/// <summary>Max wait (seconds) for the avatar before the automatic height correction.</summary>
		private const float AutoFixWaitSeconds = 5f;

		private XROrigin _xrOrigin;


		public void Dispose() {
			DisposeNameplate();

			if (XRInputs.Provider is AutoHandProvider)
				XRInputs.Provider = null;
			avatarLoader?.ClearRig();
			avatarLoader?.Dispose();
			Menu.Dispose();
			Destroy(gameObject);
		}

		private void Awake() => SetupNameplate();

		private void Start() {
			StartupAutoHand().Forget();
			AutoFixViewHeight().Forget();
		}

		/// <summary>
		/// Brings the view to the recommended height when it starts under the floor (no headset, simulator, or
		/// OpenXR reporting a floor origin). Does nothing when the measured height is plausible.
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
