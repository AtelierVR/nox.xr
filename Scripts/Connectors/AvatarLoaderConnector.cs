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
using Logger = Nox.CCK.Utils.Logger;
using NoxHandType = Nox.Avatars.Hand.HandType;

namespace Nox.XR.Connectors {
    [RequireComponent(typeof(XRController))]
    public class AvatarLoaderConnector : MonoBehaviour {
		public AutoHandPlayer player;
		public PlayerHandConnector connector;

		private IRuntimeAvatar _runtime;
		private CancellationTokenSource _context;
		private EventSubscription _onUserUpdate;
		private Dictionary<string, object> _parameters;

		private void Awake()
			=> _parameters = new Dictionary<string, object> {
				["source"] = GetComponent<XRController>(),
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
			ClearRig();
			_runtime?.Dispose();
			_runtime = null;
		}

		public IRuntimeAvatar GetAvatar()
			=> _runtime;

		private void ApplyRig(IRuntimeAvatar runtime) {
			if (_runtime?.Descriptor == null || !connector)
				return;

			var handModule = _runtime.Descriptor.GetModules<IHandModule>().FirstOrDefault();
			if (handModule == null)
				return;

			var leftData  = Array.Find(handModule.Hands, h => h.Type == NoxHandType.Left);
			var rightData = Array.Find(handModule.Hands, h => h.Type == NoxHandType.Right);

			var left  = leftData != null ? HandToAutoHand.Convert(leftData) : null;
			var right = rightData != null ? HandToAutoHand.Convert(rightData) : null;

			connector.Set(left, right);

			#if HAS_FINALIK
			var vrik = runtime.Descriptor.Anchor.GetComponentInChildren<RootMotion.FinalIK.VRIK>();
			if (vrik && left && right) {
				var autovrik = vrik.GetOrAddComponent<NoxAutoHandVRIK>();

				autovrik.leftHand             = left;
				autovrik.leftTrackedController = connector.Fallbacks[0].follow;
				autovrik.leftHandSource        = leftData;
				autovrik.rightHand             = right;
				autovrik.rightTrackedController = connector.Fallbacks[1].follow;
				autovrik.rightHandSource        = rightData;
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

			ApplyRig(_runtime);
			root.SetActive(true);

			Client.CoreAPI.EventAPI.Emit("controller_avatar_changed", this, _runtime);
			return true;
		}

		public async UniTask<IRuntimeAvatar> SetAvatar(Identifier identifier, Action<string, float> progress = null, bool forceReload = false) {
			Logger.LogDebug($"Loading avatar for identifier {identifier.ToString()}");

			if (this == null || gameObject == null)
				return null;

			var playerAvatar = GetComponent<XRController>()?.GetPlayer() as ILocalPlayerAvatar;

			if (!identifier.IsValid()) {
				if (playerAvatar != null)
					await playerAvatar.OnAvatarFailed(new Exception("Invalid avatar identifier."));
				return null;
			}

			if (!forceReload && _runtime.Arguments.ContainsKey("error") && identifier.Equals(_runtime.Identifier)) {
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

			Logger.LogDebug("Creating avatar");

			try {
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