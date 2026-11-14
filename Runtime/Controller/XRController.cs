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
using Nox.XR.Runtime.Providers;
using UnityEngine.EventSystems;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
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
		/// Get the proxy mod API, or <c>null</c> when the loader is not initialized yet (edit mode, start-up) or when
		/// the controllers mod is missing: every caller works with a missing API instead of throwing.
		/// </summary>
		private static IControllerAPI ControllerAPI
			=> Client.CoreAPI?.ModAPI
				.GetMod("controllers")
				?.GetInstance<IControllerAPI>();

		/// <summary>
		/// Check if the current proxy is better than XR proxy.
		/// </summary>
		/// <returns></returns>
		private static bool IsBetterThanCurrent() {
			var controller = ControllerAPI?.Current;
			return controller == null
				|| controller.GetPriority() < DefaultPriority
				|| controller.GetId() == DefaultId;
		}

		/// <summary>
		/// Check if the current proxy is the XR proxy. <c>false</c> when the controllers API cannot be read (not
		/// playing, mod not initialized): the answer is only meaningful while the game runs.
		/// </summary>
		/// <returns></returns>
		internal static bool IsCurrent() {
			var controller = ControllerAPI?.Current;
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
		/// Creates the XR proxy (the AutoHand rig), makes it the current controller and starts its avatar.
		/// </summary>
		/// <param name="cancellationToken">
		/// Owns the creation: the proxy and everything it instantiates (its menu) must not be built once
		/// the caller is gone. Cancelled, the half-built proxy is dropped.
		/// </param>
		public static async UniTask<bool> Make(CancellationToken cancellationToken = default) {
			// Idempotent: the proxy is already the current controller.
			if (IsCurrent()) {
				Logger.LogDebug("XR proxy already current, nothing to create.", tag: nameof(XRController));
				return true;
			}

			if (!IsBetterThanCurrent()) {
				Logger.LogError("XR is not better than the current.");
				return false;
			}

			using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			wait.CancelAfter(TimeSpan.FromSeconds(30));

			await Client.Instance.WaitReady(wait.Token)
				.SuppressCancellationThrow();

			if (cancellationToken.IsCancellationRequested)
				return false;

			if (!XRLoaderManager.IsRunning) {
				Logger.LogError("XR system failed to initialize, cannot create XR proxy");
				return false;
			}

			var prefab = Client.CoreAPI.AssetAPI.GetAsset<GameObject>("xr_proxy.prefab");
			if (!prefab) {
				Logger.LogError("Failed to load XR proxy prefab");
				return false;
			}

			XRController xr;
			try {
				xr = await prefab.InstantiateAsync<XRController>(cancellationToken: cancellationToken);
			} catch (OperationCanceledException) {
				// The caller went away before the prefab was ready: nothing to drop.
				return false;
			}

			if (!xr) {
				Logger.LogError("Failed to get XR proxy component");
				return false;
			}

			if (!xr.Menu) {
				Logger.LogError("XR menu provider is not configured in the prefab");
				xr.gameObject.Destroy();
				return false;
			}

			xr.gameObject.name = $"[{xr.GetType().Name}] {ComponentExtension.GetId(xr)}";

			if (xr.eventSystem)
				xr.eventSystem.enabled = false;

			var isCurrent = false;
			try {
				if (!await xr.Menu.Generate(cancellationToken)) {
					Logger.LogError("Failed to generate the XR proxy menu");
					xr.gameObject.Destroy();
					return false;
				}

				await UniTask.NextFrame(cancellationToken: cancellationToken);
				cancellationToken.ThrowIfCancellationRequested();

				if (!await ControllerAPI.SetCurrent(xr)) {
					Logger.LogError("Failed to set XR proxy as current");
					xr.Menu.Dispose();
					xr.gameObject.Destroy();
					return false;
				}
				isCurrent = true;

				await UniTask.DelayFrame(3, cancellationToken: cancellationToken);
			} catch (OperationCanceledException) {
				// The caller went away: drop the proxy instead of finishing its creation.
				xr.Menu.Dispose();

				if (isCurrent && ControllerAPI != null)
					await ControllerAPI.SetCurrent(null);

				xr.gameObject.Destroy();
				return false;
			}

			if (xr.eventSystem)
				xr.eventSystem.enabled = true;

			xr.gameObject.DontDestroyOnLoad();

			if (!xr.avatarLoader) {
				Logger.LogError("XR avatar loader is not configured in the prefab");
			} else {
				xr.avatarLoader.SetupAvatar().Forget();
			}

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

		/// <summary>Below this height the view is considered to start under the floor.</summary>
		private const float MinPlausibleViewHeight = 0.5f;

		/// <summary>Max wait (seconds) for the avatar before the automatic height correction.</summary>
		private const float AutoFixWaitSeconds = 5f;


		/// <summary>
		/// Disposes the proxy and completes once its avatar is destroyed: the avatar writes what it has to
		/// persist while it is disposed, and that write must end before the mods (and their table) go away.
		/// </summary>
		public UniTask Dispose() {
			DisposeNameplate();

			if (XRInputs.Provider is AutoHandProvider)
				XRInputs.Provider = null;

			avatarLoader?.ClearRig();

			var avatar = avatarLoader != null
				? avatarLoader.Dispose()
				: UniTask.CompletedTask;

			Menu.Dispose();

			// Deferred: the avatar is a child of this object and finishes its write on this frame.
			gameObject.Destroy();

			return avatar;
		}

		private void Awake() => SetupNameplate();

		private void Start() {
			StartupAutoHand().Forget();
			AutoFixViewHeight().Forget();
		}
	}
}
