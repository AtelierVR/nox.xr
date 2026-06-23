using Nox.CCK.Mods.Cores;
using Nox.CCK.Mods.Initializers;
using Nox.CCK.Utils;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using Nox.Avatars;
using Nox.UI;
using Nox.CCK.XR;
using Nox.Users;
using UnityEngine.Events;
using UnityEngine.XR;
using UnityEngine.XR.Management;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.XR {
	public class Client : IClientModInitializer {
		public static Client Instance;
		static internal IClientModCoreAPI CoreAPI;

		static internal IUiAPI UiAPI
			=> CoreAPI.ModAPI.GetMod("ui")
				?.GetInstance<IUiAPI>();

		static internal IAvatarAPI AvatarAPI
			=> CoreAPI.ModAPI.GetMod("avatar")
				?.GetInstance<IAvatarAPI>();

		static internal IUserAPI UserAPI
			=> CoreAPI.ModAPI.GetMod("users")
				?.GetInstance<IUserAPI>();

		private bool _isXRInitialized;

		[NoxPublic(NoxAccess.Read)]
		public readonly UnityEvent<bool> OnHeadsetConnected = new();

		[NoxPublic(NoxAccess.Method)]
		public bool IsXRInitialized()
			=> _isXRInitialized;

		public async UniTask WaitXRInitialization(CancellationToken ct = default) {
			if (IsXRInitialized())
				return;

			await UniTask.WaitUntil(IsXRInitialized, cancellationToken: ct);
		}

		[NoxPublic(NoxAccess.Method)]
		public bool IsReady()
			=> IsXRInitialized() && XRInputs.HasHeadset;

		public async UniTask OnInitializeClientAsync(IClientModCoreAPI api) {
			CoreAPI  = api;
			Instance = this;

			if (!Settings.EnableXRSetting.Value) {
				Logger.LogWarning("VR disabled by setting or --no-vr flag.");
				return;
			}

			await UniTask.Yield();
			await StartLoader();
		}

		public async UniTask OnDisposeClientAsync() {
			StopLoader();
			if (await XRController.Remove())
				Logger.Log("XR Controller has been removed.");
			Instance = null;
			CoreAPI  = null;
		}

		private void OnDeviceConnected(InputDevice device)
			=> OnDeviceConnectedAsync(device).Forget();

		private async UniTask OnDeviceConnectedAsync(InputDevice device) {
			Logger.LogDebug($"New XR Device:");
			Logger.LogDebug(" - name: " + device.name);
			Logger.LogDebug(" - characteristics: " + device.characteristics);
			Logger.LogDebug(" - manufacturer: " + device.manufacturer);
			Logger.LogDebug(" - serial number: " + device.serialNumber);
			Logger.LogDebug(" - subsystem: " + device.subsystem);

			var usages = new List<InputFeatureUsage>();
			device.TryGetFeatureUsages(usages);
			foreach (var usage in usages)
				Logger.LogDebug(" - usage: " + usage.name);

			if (device.TryGetHapticCapabilities(out var hapticCapabilities)) {
				Logger.LogDebug(" - haptic capabilities:");
				Logger.LogDebug("   - num channels: " + hapticCapabilities.numChannels);
				Logger.LogDebug("   - supports buffer: " + hapticCapabilities.supportsBuffer);
				Logger.LogDebug("   - supports impulse: " + hapticCapabilities.supportsImpulse);
				Logger.LogDebug("   - buffer optimal size: " + hapticCapabilities.bufferOptimalSize);
				Logger.LogDebug("   - buffer max size: " + hapticCapabilities.bufferMaxSize);
				Logger.LogDebug("   - buffer frequency Hz: " + hapticCapabilities.bufferFrequencyHz);
			}

			if (device.characteristics.HasFlag(InputDeviceCharacteristics.HeadMounted)) {
				OnHeadsetConnected.Invoke(true);
				if (await XRController.Make())
					Logger.Log("XR Controller has been created.");
				else
					Logger.LogWarning("Failed to create XR Controller.");
			}
		}

		private void OnDeviceDisconnected(InputDevice device)
			=> OnDeviceDisconnectedAsync(device).Forget();

		private async UniTask OnDeviceDisconnectedAsync(InputDevice device) {
			Logger.Log($"XR Device disconnected: {device.name} {device.characteristics}");
			if (device.characteristics.HasFlag(InputDeviceCharacteristics.HeadMounted)) {
				OnHeadsetConnected.Invoke(false);
				if (await XRController.Remove())
					Logger.Log("XR Controller has been removed.");
				else
					Logger.LogWarning("Failed to remove XR Controller.");
			}
		}

		private void OnDeviceConfigChanged(InputDevice device) {
			Logger.Log($"XR Device config changed: {device.name} {device.characteristics}");
		}


		[NoxPublic(NoxAccess.Method)]
		public async UniTask StartLoader() {
			if (_isXRInitialized) {
				Logger.LogWarning("XR already initialized.");
				return;
			}

			if (!XRGeneralSettings.Instance.Manager.isInitializationComplete) {
				Logger.Log("Initializing XR...");
				await XRGeneralSettings.Instance.Manager.InitializeLoader().ToUniTask();
			}

			var loader = XRGeneralSettings.Instance.Manager.activeLoader;

			if (!loader) {
				Logger.LogWarning("XR loader is not active.");
				return;
			}

			Logger.Log("Loading XR...");

			if (!loader.Initialize()) {
				Logger.LogError("XR loader failed to initialize.");
				var err = loader.GetLoadedSubsystem<XRDisplaySubsystem>() == null
					? "Display subsystem is null."
					: loader.GetLoadedSubsystem<XRInputSubsystem>() == null
						? "Input subsystem is null."
						: "Unknown error.";
				Logger.LogError($"XR loader error: {err}");
				return;
			}

			Logger.Log($"XR loader initialized: {loader.name}");


			Logger.Log("XR initialized. Starting subsystems...");
			XRGeneralSettings.Instance.Manager.StartSubsystems();

			_isXRInitialized = true;

			InputDevices.deviceConnected     += OnDeviceConnected;
			InputDevices.deviceDisconnected  += OnDeviceDisconnected;
			InputDevices.deviceConfigChanged += OnDeviceConfigChanged;

			var devices = new List<InputDevice>();
			InputDevices.GetDevices(devices);
			Logger.LogDebug($"XR Devices found: {devices.Count}");
			foreach (var device in devices)
				await OnDeviceConnectedAsync(device);
		}

		public void StopLoader() {
			if (!_isXRInitialized) {
				Logger.LogWarning("XR not initialized.");
				return;
			}

			Logger.Log("Stopping XR...");
			XRGeneralSettings.Instance.Manager.StopSubsystems();
			XRGeneralSettings.Instance.Manager.DeinitializeLoader();
			_isXRInitialized = false;

			InputDevices.deviceConnected     -= OnDeviceConnected;
			InputDevices.deviceDisconnected  -= OnDeviceDisconnected;
			InputDevices.deviceConfigChanged -= OnDeviceConfigChanged;

			OnHeadsetConnected.Invoke(false);
			Logger.Log("XR stopped.");
		}


		[NoxPublic(NoxAccess.Method)]
		public List<InputDevice> GetAllTrackers() {
			var devices = new List<InputDevice>();
			InputDevices.GetDevices(devices);
			return devices.Where(
					d => d.characteristics.HasFlag(InputDeviceCharacteristics.TrackedDevice)
						&& !d.characteristics.HasFlag(InputDeviceCharacteristics.HeadMounted)
						&& !d.characteristics.HasFlag(InputDeviceCharacteristics.Left)
						&& !d.characteristics.HasFlag(InputDeviceCharacteristics.Right)
				)
				.ToList();
		}

	}
}