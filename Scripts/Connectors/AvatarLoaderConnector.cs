using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Autohand;
using Cysharp.Threading.Tasks;
using Nox.Avatars;
using Nox.Avatars.AutoHand;
using Nox.Avatars.Hand;
using Nox.Avatars.Parameters;
using Nox.Avatars.Players;
using Nox.Avatars.Scale;
using Nox.CCK.XR;
using Nox.CCK.Avatars;
using Nox.CCK.Mods.Events;
using Nox.CCK.Utils;
using Nox.Users;
using UnityEngine;
using UnityEngine.Serialization;
using Logger = Nox.CCK.Utils.Logger;
using NoxHandType = Nox.Avatars.Hand.HandType;
using RootMotion.FinalIK;

namespace Nox.XR.Connectors {
    // Pas de [RequireComponent(typeof(XRController))] : dans le proxy XR ce loader vit sur
    // le GameObject enfant "Avatar" (c'est son transform qui sert de parent à l'avatar
    // chargé). L'attribut y ajoutait automatiquement un second XRController vide, qui
    // loguait "XRController.player is null in StartupAutoHand" et faussait
    // GetComponent<XRController>() — on résout donc le contrôleur via GetComponentInParent.
    public class AvatarLoaderConnector : MonoBehaviour {
		public AutoHandPlayer player;

		// Le prefab (et les bundles déjà construits) sérialise ce champ sous le nom
		// "handConnector" : sans cet attribut, Unity ignore la valeur et `connector`
		// reste null — ce qui désactive tout le rig des mains (fallbacks visibles,
		// avatar en T-pose) sans le moindre message d'erreur.
		[FormerlySerializedAs("handConnector")]
		public PlayerHandConnector connector;

		private IRuntimeAvatar _runtime;
		private CancellationTokenSource _context;
		private EventSubscription _onUserUpdate;
		private Dictionary<string, object> _parameters;
		private bool _settingUp;

		private void Awake()
			=> _parameters = new Dictionary<string, object> {
				["source"] = GetComponentInParent<XRController>(),
				["xr"]     = true,
				["local"]  = true
			};

		public void StartUserTracking() {
			_onUserUpdate = Client.CoreAPI.EventAPI.Subscribe("user_update", OnUserUpdate);
		}

		public void Dispose() {
			if (_onUserUpdate != null) {
				Client.CoreAPI.EventAPI.Unsubscribe(_onUserUpdate);
				_onUserUpdate = null;
			}
			_context?.Cancel();
			_context?.Dispose();
			_context = null;
			_settingUp = false;
			ClearRig();
			_runtime?.Dispose();
			_runtime = null;
		}

		public IRuntimeAvatar GetAvatar()
			=> _runtime;

		private async UniTask ApplyRig(IRuntimeAvatar runtime) {
			if (_runtime?.Descriptor == null)
				return;

			if (!connector) {
				// Sans ce connecteur, aucune main n'est convertie en AutoHand, les mains de
				// fallback ne sont jamais masquées et l'IK des bras n'est pas configuré
				// (avatar en T-pose). On le signale plutôt que de retourner en silence.
				Logger.LogError(
					$"{nameof(AvatarLoaderConnector)}.connector is null: the avatar hand rig will not be applied "
					+ "(fallback hands stay visible and the arms remain in T-pose). "
					+ "Verify the 'connector' reference on the XR proxy prefab.",
					this
				);
				return;
			}

			var handModule = _runtime.Descriptor.GetModules<IHandModule>().FirstOrDefault();
			if (handModule == null) {
				Logger.LogWarning(
					$"{nameof(AvatarLoaderConnector)}: avatar '{runtime.Descriptor}' has no {nameof(IHandModule)}, "
					+ "fallback hands will remain active.",
					runtime.Descriptor.Anchor
				);
				return;
			}

			var leftData  = Array.Find(handModule.Hands, h => h.Type == NoxHandType.Left);
			var rightData = Array.Find(handModule.Hands, h => h.Type == NoxHandType.Right);

			if (leftData == null || rightData == null) {
				Logger.LogWarning(
					$"{nameof(AvatarLoaderConnector)}: avatar '{runtime.Descriptor}' exposes "
					+ $"{handModule.Hands?.Length ?? 0} hand(s) but not both Left and Right "
					+ $"({nameof(NoxHandType.Left)}={leftData != null}, {nameof(NoxHandType.Right)}={rightData != null}). "
					+ "Fallback hands will remain active and the arms may stay in T-pose.",
					runtime.Descriptor.Anchor
				);
			}

			var left  = leftData != null 
				? HandToAutoHand.Convert(leftData) 
				: null;
			var right = rightData != null 
				? HandToAutoHand.Convert(rightData) 
				: null;

			connector.Set(left, right);

			#if HAS_FINALIK
			var vrik = runtime.Descriptor.Anchor.GetComponentInChildren<VRIK>();
			if (vrik && left && right) {
				var autovrik = vrik.GetOrAddComponent<NoxAutoHandVRIK>();

				// Les contrôleurs suivis doivent être la POSE BRUTE des contrôleurs : le « follow » d'une
				// main de remplacement porte l'offset de rotation du prefab AutoHand (85° en X), que le
				// pivot de l'avatar (HandOffset) applique déjà de son côté.
				autovrik.leftSource         = leftData;
				autovrik.leftTrackedController  = connector.GetTrackedController(true);
				autovrik.rightSource        = rightData;
				autovrik.rightTrackedController = connector.GetTrackedController(false);

				// Les mains physiques sont des duplicatas des mains de l'avatar (mêmes colliders, pokes
				// et échelle), créés par NoxAutoHandVRIK dans le dossier « Hands » à côté des mains de
				// remplacement. L'armature ne garde que ses os, écrits par VRIK.
				autovrik.physicalHandsRoot = connector.Fallbacks[1] ? connector.Fallbacks[1].transform.parent : null;

				// Wait for NoxAutoHandVRIK to be fully initialized so we have access to physical hands
				await UniTask.WaitUntil(() => autovrik.rightPhysical != null && autovrik.leftPhysical != null);

				// Load NearFarInteractor prefab asynchronously using GetAssetAsync
				var near = await Client.CoreAPI.AssetAPI.GetAssetAsync<GameObject>("near_far_interactor.prefab");
				if (near != null) {
					static Transform EndBone(IFinger finger) {
						if (finger.Tip)
							return finger.Tip;
						if (finger.Distal)
							return finger.Distal;
						if (finger.Intermediate)
							return finger.Intermediate;
						if (finger.Proximal)
							return finger.Proximal;
						return null;
					}
					
					async UniTask<NearFarInteractor> SetupNearFar(Hand physical, IHand source) {
						Transform parent;
						Vector3 position;
						Quaternion rotation;

						if (source.NearFar == null) {
    parent = source.Anchor;
    var thumb = source.Fingers.FirstOrDefault(f => f.Type == FingerType.Thumb);
    var index = source.Fingers.FirstOrDefault(f => f.Type == FingerType.Index);
    
    var endThumb = EndBone(thumb)
        ?? (thumb is MonoBehaviour mb0 ? mb0.transform : null)
        ?? parent;
    var endIndex = EndBone(index)
        ?? (index is MonoBehaviour mb1 ? mb1.transform : null)
        ?? parent;
    
    var thumbLocal = parent.InverseTransformPoint(endThumb.position);
    var indexLocal = parent.InverseTransformPoint(endIndex.position);

    // 1. Calcul de la position :
    // On prend un point situé entre le pouce et l'index sur les axes X et Y (ex: 50% ou 60%),
    // mais on force impérativement l'axe Z à matcher celui du bout de l'index.
    const float blendWeight = 0.75f; // Ajustez entre 0.0 (aligné sur l'index) et 1.0 (aligné sur le pouce)
    
    position = new Vector3(
        Mathf.Lerp(indexLocal.x, thumbLocal.x, blendWeight),
        Mathf.Lerp(indexLocal.y, thumbLocal.y, blendWeight),
        indexLocal.z // Reste au même niveau (profondeur/hauteur Z) que l'index
    );

    // 2. Orientation basée sur l'orientation de l'index :
    var indexLocalRotation = Quaternion.Inverse(parent.rotation) * endIndex.rotation;
    var dir = indexLocalRotation * Vector3.up;
    var pitch = Mathf.Atan2(-dir.y, -dir.x) * Mathf.Rad2Deg;
    rotation = Quaternion.Euler(pitch, 270f, 0f);
} else {
							parent = source.NearFar;
							position = Vector3.zero;
							rotation = Quaternion.identity;
						}

						var instance = await near.InstantiateAsync<NearFarInteractor>(parent);
						instance.Hand = source.Type == NoxHandType.Left
							? UnityEngine.XR.Interaction.Toolkit.Interactors.InteractorHandedness.Left
							: UnityEngine.XR.Interaction.Toolkit.Interactors.InteractorHandedness.Right;
						instance.transform.SetLocalPositionAndRotation(position, rotation);
						return instance;
					}
					
					await SetupNearFar(autovrik.rightPhysical, autovrik.rightPhysicalSource);
					await SetupNearFar(autovrik.leftPhysical,  autovrik.leftPhysicalSource);
				} else {
					Logger.LogError("NearFarInteractor prefab not found at prefabs/near_far_interactor.prefab", this);
				}
			}
			#endif
		}

		public void ClearRig()
			=> connector?.Clear();

		public async UniTask<bool> SetAvatar(IRuntimeAvatar runtime) {
			Logger.LogDebug("Setting avatar for XRController");

			if (!this || !gameObject) {
				Logger.LogError("AvatarLoaderConnector has been destroyed, cannot set avatar");
				return false;
			}

			if (runtime == _runtime)
				return true;

			var old = _runtime;
			_runtime = runtime;

			if (_runtime == null) {
				Logger.LogWarning("Setting avatar to null, removing current avatar.");
				_runtime = old;
				return false;
			}

			var descriptor = _runtime.Descriptor;
			if (descriptor == null) {
				Logger.LogError("Avatar descriptor is null, cannot set avatar.");
				_runtime = old;
				return false;
			}

			var root = descriptor.Anchor;
			if (!root) {
				Logger.LogError("Avatar descriptor root is null, cannot set avatar.");
				_runtime = old;
				return false;
			}

			root.name += $" {runtime.Identifier.ToString()} XR";

			if (old != null)
				await old.Dispose();

			Logger.LogDebug($"Attaching avatar to {_runtime.Descriptor}", runtime.Descriptor.Anchor);
			root.transform.SetParent(transform, false);
			root.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);

            var scaleModule = _runtime.Descriptor.GetModules<IScaleAvatarModule>().FirstOrDefault();
			player.minMaxHeight = new Vector2(player.minMaxHeight.x, scaleModule?.Height ?? 1.7f);

			var parameterModule = _runtime?.Descriptor
				?.GetModules<IParameterModule>()
				.FirstOrDefault();

			if (parameterModule == null) {
				Logger.LogWarning("Avatar has no parameter module, cannot configure tracking parameters.");
				root.SetActive(true);
				Client.CoreAPI.EventAPI.Emit("controller_avatar_changed", this, _runtime);
				return true;
			}

			var animator = _runtime?.Descriptor?.Animator;
			if (animator && !animator.runtimeAnimatorController) {
				Logger.LogDebug("Waiting for Animator to be ready...");
				await UniTask.WaitUntil(() => animator.runtimeAnimatorController);
			}

			var parameters = parameterModule.GetParameters();
				foreach (var param in parameters)
					switch (param.GetName()) {
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
						case "tracking/right_foot/active":
						case "tracking/left_toes/active":
						case "tracking/right_toes/active":
							param.Set(false);
							break;
						case "VRMode" or "in_vr":
						case "IsLocal" or "local":
						case "rig/ik/head/target":
							param.Set(true);
							break;
					}

			ApplyRig(_runtime).Forget();
			root.SetActive(true);

			Client.CoreAPI.EventAPI.Emit("controller_avatar_changed", this, _runtime);
			return true;
		}

		public async UniTask<IRuntimeAvatar> SetAvatar(Identifier identifier, Action<string, float> progress = null, bool forceReload = false) {
			Logger.LogDebug($"Loading avatar for identifier {identifier.ToString()}");

			if (this == null || gameObject == null)
				return null;

			var playerAvatar = GetComponentInParent<XRController>()?.GetPlayer() as ILocalPlayerAvatar;

			if (!identifier.IsValid()) {
				if (playerAvatar != null)
					await playerAvatar.OnAvatarFailed(new Exception("Invalid avatar identifier."));
				return null;
			}

			// _runtime est null tant qu'aucun avatar n'a été chargé (proxy XR fraîchement créé).
			// Le chemin Restore() -> SetAvatar(identifier) arrive avant SetupAvatar().
			if (!forceReload && _runtime != null && _runtime.Arguments.ContainsKey("error") && identifier.Equals(_runtime.Identifier)) {
				if (playerAvatar != null)
					await playerAvatar.OnAvatarReady();
				return _runtime;
			}

			_context?.Cancel();
			_context = new CancellationTokenSource();

			var version = identifier.GetVersion();
			if (version == ushort.MaxValue) {
				var avatarData = await Client.AvatarAPI.Fetch(identifier)
					.AttachExternalCancellation(_context.Token);
				version = avatarData.Release.Value;
			}

			var req = new AssetSearchRequest {
				Engines   = new[] { EngineExtensions.CurrentEngine.GetEngineName() },
				Platforms = new[] { PlatformExtensions.CurrentPlatform.GetPlatformName() },
				Versions  = new[] { version },
				Limit     = 1
			};

			var asset = (await Client.AvatarAPI.SearchAssets(identifier, req)
					.AttachExternalCancellation(_context.Token)).Items
				.FirstOrDefault();
			if (_context.IsCancellationRequested)
				return null;

			if (asset == null) {
				Logger.LogWarning($"Avatar asset not found for identifier {identifier.ToString()}");
				var err = await Client.AvatarAPI.LoadError(_parameters);
				err.Identifier = identifier;
				await SetAvatar(err);
				err.Arguments.Add("error", new[] { "asset_not_found" });
				if (playerAvatar != null)
					await playerAvatar.OnAvatarFailed(new Exception("Avatar asset not found."));
				return null;
			}

			if (!Client.AvatarAPI.HasInCache(asset.Hash)) {
				var download = Client.AvatarAPI.DownloadToCache(
					asset.Url,
					hash: asset.Hash,
					progress: p => progress?.Invoke($"Downloading avatar {identifier.ToString()}", p),
					token: _context.Token
				);
				await download.Start();
				if (_context.IsCancellationRequested)
					return null;
			}

			var avatar = await Client.AvatarAPI.LoadFromCache(
				asset.Hash,
				_parameters,
				progress: p => progress?.Invoke($"Loading avatar {identifier.ToString()}", p),
				token: _context.Token
			);
			if (_context.IsCancellationRequested)
				return null;

			if (avatar == null && Client.AvatarAPI.HasInCache(asset.Hash)) {
				Logger.LogWarning($"Corrupt cache entry for avatar {identifier.ToString()}, re-downloading...");
				Client.AvatarAPI.RemoveFromCache(asset.Hash);
				var reDownload = Client.AvatarAPI.DownloadToCache(
					asset.Url,
					hash: asset.Hash,
					progress: p => progress?.Invoke($"Re-downloading avatar {identifier.ToString()}", p),
					token: _context.Token
				);
				await reDownload.Start();
				if (_context.IsCancellationRequested)
					return null;
				avatar = await Client.AvatarAPI.LoadFromCache(
					asset.Hash,
					_parameters,
					progress: p => progress?.Invoke($"Loading avatar {identifier.ToString()}", p),
					token: _context.Token
				);
				if (_context.IsCancellationRequested)
					return null;
			}

			if (avatar == null) {
				Logger.LogError($"Failed to load avatar from cache for identifier {identifier.ToString()}");
				var err = await Client.AvatarAPI.LoadError(_parameters);
				err.Identifier = identifier;
				err.Arguments.Add("error", new[] { "load_failed" });
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

		public async UniTask<IRuntimeAvatar> ReloadAvatar(Action<string, float> progress = null) {
			var identifier = _runtime?.Identifier ?? Identifier.Invalid;
			if (!identifier.IsValid()) {
				Logger.LogWarning("Cannot reload avatar: current avatar identifier is invalid.");
				return null;
			}

			return await SetAvatar(identifier, progress, true);
		}

		public async UniTask SetupAvatar() {
			if (_runtime != null) {
				Logger.LogDebug("Avatar already set for XRController");
				return;
			}

			// SetupAvatar est atteignable en parallèle (MakeInternal le déclenche, le flux
			// user_update peut aussi le relancer). Deux passes concurrentes s'annulent via
			// _context.Cancel() et laissent le loader sans avatar.
			if (_settingUp) {
				Logger.LogDebug("Avatar setup already in progress, skipping duplicate request.");
				return;
			}

			_settingUp = true;
			try {
				Logger.LogDebug("Creating avatar");

				if (Client.AvatarAPI == null) {
					Logger.LogError("AvatarAPI is null, cannot setup avatar");
					return;
				}

				if (Client.UserAPI == null) {
					Logger.LogError("UserAPI is null, cannot setup avatar");
					return;
				}

				var avatar = await Client.AvatarAPI.LoadLoading(_parameters);
				if (avatar == null) {
					Logger.LogError("Failed to create avatar for XRController");
					return;
				}

				if (!this || !gameObject) {
					Logger.LogWarning("AvatarLoaderConnector was destroyed while loading the loading avatar.");
					await avatar.Dispose();
					return;
				}

				if (!await SetAvatar(avatar)) {
					await avatar.Dispose();
					return;
				}

				var currentUser = Client.UserAPI.Current;
				if (currentUser != null)
					LoadAvatarFromUser(currentUser);
				else
					Logger.LogWarning("No current user available for avatar loading");
			} catch (Exception e) {
				Logger.LogError($"Exception in SetupAvatar: {e}");
			} finally {
				_settingUp = false;
			}
		}

		private void OnUserUpdate(EventData context) {
			if (!context.TryGet(0, out ICurrentUser user) || user == null || !XRController.IsCurrent())
				return;
			LoadAvatarFromUser(user);
		}

		private void LoadAvatarFromUser(ICurrentUser user) {
			if (user?.Avatar.IsValid() != true)
				return;
			SetAvatar(user.Avatar).Forget();
		}
	}
}