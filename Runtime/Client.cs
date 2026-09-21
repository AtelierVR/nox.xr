using Nox.CCK.Mods.Cores;
using Nox.CCK.Mods.Events;
using Nox.CCK.Mods.Initializers;
using Nox.CCK.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using Nox.Avatars;
using Nox.Controllers;
using Nox.UI;
using Nox.CCK.XR;
using Nox.Users;
using UnityEngine;
using UnityEngine.Events;
using Nox.XR.Loaders;
using Nox.XR.Runtime.Loaders;
using Nox.XR.Runtime.Widgets;
using UnityEngine.XR;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.XR.Runtime {
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

		static internal IControllerAPI ControllerAPI
			=> CoreAPI.ModAPI.GetMod("controllers")
				?.GetInstance<IControllerAPI>();

		private EventSubscription[] _events = Array.Empty<EventSubscription>();

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

			_events = new[] {
				CoreAPI.EventAPI.Subscribe("widget_request", OnWidgetRequest)
			};
			ControllerAPI?.OnCurrentChanged.AddListener(OnCurrentControllerChanged);

			if (!Settings.EnableXRSetting.Value) {
				Logger.LogWarning("VR disabled by setting or --no-vr flag.");
				return;
			}

			await UniTask.Yield();
			await StartLoader();
		}

		public async UniTask OnDisposeClientAsync() {
			// Retirer le bouton avant de couper les évènements : la page peut être encore ouverte.
			StandUpWidget.Hide();

			ControllerAPI?.OnCurrentChanged.RemoveListener(OnCurrentControllerChanged);
			foreach (var e in _events)
				CoreAPI?.EventAPI.Unsubscribe(e);
			_events = Array.Empty<EventSubscription>();

			await QuitXR();
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

		/// <summary>
		/// Fournit les widgets du mod à la page qui les demande (voir <see cref="StandUpWidget"/>).
		/// </summary>
		private static void OnWidgetRequest(EventData context) {
			if (!context.TryGet(0, out int menuId)) return;
			if (!context.TryGet(1, out RectTransform parent)) return;

			var menu = UiAPI?.Get<IMenu>(menuId);
			if (menu == null) return;

			if (StandUpWidget.TryMake(menu, parent, out var widget) && widget.Item2 != null)
				context.Callback(widget.Item2, widget.Item1);
		}

		/// <summary>
		/// Le bouton « Stand up » n'a de sens que si le proxy XR est le contrôleur courant :
		/// on l'ajoute ou le retire à chaud quand le contrôleur courant change.
		/// </summary>
		private static void OnCurrentControllerChanged(IController controller) {
			if (controller is IXRController)
				StandUpWidget.Show();
			else
				StandUpWidget.Hide();
		}


		/// <summary>
		/// Loader XR retenu pour cette session (openxr, openvr, ...), ou <c>null</c>.
		/// Voir <see cref="XRLoaderManager"/>.
		/// </summary>
		[NoxPublic(NoxAccess.Method)]
		public IXRLoaderProvider GetLoader()
			=> XRLoaderManager.Current;

		/// <summary>
		/// Loaders XR disponibles, triés par priorité décroissante. Sert au diagnostic
		/// (quel loader est valide sur cet OS, et pourquoi les autres ne le sont pas).
		/// </summary>
		[NoxPublic(NoxAccess.Method)]
		public List<IXRLoaderProvider> GetLoaders()
			=> XRLoaderManager.GetProviders().ToList();

		[NoxPublic(NoxAccess.Method)]
		public async UniTask StartLoader() {
			if (_isXRInitialized) {
				Logger.LogWarning("XR already initialized.");
				return;
			}

			// Le choix du loader (et les éventuels replis) est délégué aux fournisseurs :
			// nox.xr ne connaît ni OpenXR ni OpenVR.
			if (!await XRLoaderManager.StartAsync(CoreAPI?.ModAPI)) {
				Logger.LogError("XR loader failed to initialize.");
				return;
			}

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

			XRLoaderManager.Stop();
			_isXRInitialized = false;

			InputDevices.deviceConnected     -= OnDeviceConnected;
			InputDevices.deviceDisconnected  -= OnDeviceDisconnected;
			InputDevices.deviceConfigChanged -= OnDeviceConfigChanged;

			OnHeadsetConnected.Invoke(false);
		}

		/// <summary>
		/// Entre en XR : initialise le loader (le proxy XR est créé par l'évènement de
		/// connexion du casque).
		/// </summary>
		[NoxPublic(NoxAccess.Method)]
		public async UniTask EnterXR() {
			if (IsXRInitialized()) {
				Logger.LogDebug("XR already initialized, nothing to enter.");
				return;
			}

			await StartLoader();
		}

		/// <summary>
		/// Quitte la XR : arrête le loader ET retire le proxy.
		/// <para>
		/// <see cref="StopLoader"/> seul ne suffit pas : le proxy XR reste le contrôleur
		/// courant avec un tracking mort. Il faut le retirer pour retomber sur un autre
		/// contrôleur (desktop/offline).
		/// </para>
		/// </summary>
		[NoxPublic(NoxAccess.Method)]
		public async UniTask QuitXR() {
			StopLoader();

			if (await XRController.Remove())
				Logger.Log("XR Controller has been removed.");
		}


		[NoxPublic(NoxAccess.Method)]
		public List<InputDevice> GetAllTrackers() {
			var devices = new List<InputDevice>();
			InputDevices.GetDevices(devices);
			return devices.Where(
					d => d.characteristics.HasFlag(InputDeviceCharacteristics.TrackedDevice)
						&& !d.characteristics.HasFlag(InputDeviceCharacteristics.HeadMounted)
						&& !d.characteristics.HasFlag(InputDeviceCharacteristics.Controller)
				)
				.ToList();
		}

	}
}