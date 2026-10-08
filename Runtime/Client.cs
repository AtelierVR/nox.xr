using Nox.CCK.Mods.Cores;
using Nox.CCK.Mods.Events;
using Nox.CCK.Mods.Initializers;
using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Nox.Avatars;
using Nox.Controllers;
using Nox.UI;
using Nox.CCK.XR;
using Nox.Nameplate;
using Nox.Settings;
using Nox.Users;
using UnityEngine;
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

		/// <summary>
		/// API of the optional <c>nox.nameplate</c> mod: null when the mod is not loaded.
		/// </summary>
		static internal INameplateAPI NameplateAPI
			=> CoreAPI?.ModAPI?.GetMod("nameplate")
				?.GetInstance<INameplateAPI>();

		private EventSubscription[] _events = Array.Empty<EventSubscription>();

		public bool IsRunning
			=> XRLoaderManager.IsRunning;

		public async UniTask WaitReady(CancellationToken ct = default) {
			if (IsRunning)
				return;
			await UniTask.WaitUntil(() => IsRunning, cancellationToken: ct);
		}

		public bool IsReady()
			=> IsRunning && XRInputs.HasHeadset;

		public async UniTask OnInitializeClientAsync(IClientModCoreAPI api) {
			CoreAPI  = api;
			Instance = this;

			_events = new[] {
				CoreAPI.EventAPI.Subscribe("widget_request", OnWidgetRequest),
				CoreAPI.EventAPI.Subscribe("controller_changed", OnCurrentControllerChanged)
			};

			if (!Settings.EnableXRSetting.Value) {
				Logger.LogWarning("VR disabled by setting or --no-vr flag.");
				return;
			}

			await Enter();
		}

        public async UniTask OnDisposeClientAsync() {
			StandUpWidget.Hide();
			FullBodyCalibrationWidget.Hide();

			foreach (var e in _events)
				CoreAPI?.EventAPI.Unsubscribe(e);
			_events = Array.Empty<EventSubscription>();

			await Quit();
			Instance = null;
			CoreAPI  = null;
		}

		#region Loader Actions

		public async UniTask Enter() {
			if (!await XRLoaderManager.Start(CoreAPI?.ModAPI)) {
				CoreAPI.LoggerAPI.LogError("XR loader failed to initialize.");
				return;
			}

			XRInputs.DeviceConnected.AddListener(OnDeviceConnected);
			XRInputs.DeviceDisconnected.AddListener(OnDeviceDisconnected);
			XRInputs.DeviceConfigChanged.AddListener(OnDeviceConfigChanged);

			// Already present devices also arrive in a burst: same deferred handling.
			ScheduleHeadsetWatch();

			// The loader state drives several XR settings (Start VR label, IPD…).
			SettingsNotifier.NotifyUpdated(null);
		}

		public async UniTask Quit() {
			if (!XRLoaderManager.IsRunning) {
				CoreAPI.LoggerAPI.LogWarning("No XR initialized.");
				return;
			}

			XRInputs.DeviceConnected.RemoveListener(OnDeviceConnected);
			XRInputs.DeviceDisconnected.RemoveListener(OnDeviceDisconnected);
			XRInputs.DeviceConfigChanged.RemoveListener(OnDeviceConfigChanged);

			// The session devices are all about to disconnect: stop the watch.
			_deviceWatchVersion++;

			await XRController.Remove();
			await XRLoaderManager.Stop();

			SettingsNotifier.NotifyUpdated(null);
		}

		#endregion

		#region Device Events

		/// <summary>Settle delay after a device event, in seconds.</summary>
		private const float DeviceSettleDelay = 0.75f;

		private bool _deviceWatchRunning;
		private int  _deviceWatchVersion;

		private void OnDeviceConnected(InputDevice device)
			=> OnDeviceChanged(device, "connected");

		private void OnDeviceDisconnected(InputDevice device)
			=> OnDeviceChanged(device, "disconnected");

		private void OnDeviceConfigChanged(InputDevice device)
			=> OnDeviceChanged(device, "config changed");

		/// <summary>Logs the change and reschedules the headset watch.</summary>
		private void OnDeviceChanged(InputDevice device, string change) {
			CoreAPI?.LoggerAPI?.LogDebug($"Device {change}: {device.name} {device.characteristics}");
			ScheduleHeadsetWatch();
		}

		private void ScheduleHeadsetWatch() {
			_deviceWatchVersion++;
			if (_deviceWatchRunning)
				return;

			_deviceWatchRunning = true;
			WatchHeadsetAsync(_deviceWatchVersion).Forget();
		}

		/// <summary>
		/// Waits for the burst to settle, then creates or removes the XR proxy to match the headset presence.
		/// </summary>
		private async UniTaskVoid WatchHeadsetAsync(int version) {
			while (version == _deviceWatchVersion)
				await UniTask.Delay(TimeSpan.FromSeconds(DeviceSettleDelay));

			_deviceWatchRunning = false;

			if (CoreAPI == null || !XRLoaderManager.IsRunning)
				return;

			// The usable tracker count may have changed with the burst.
			FullBodyCalibrationWidget.Refresh();

			try {
				var hasHeadset = XRInputs.HasHeadset;
				var isCurrent  = XRController.IsCurrent();

				if (hasHeadset == isCurrent)
					return;

				if (hasHeadset) {
					if (!await XRController.Make())
						CoreAPI.LoggerAPI.LogWarning($"Failed to {nameof(XRController)}.");
				} else if (!await XRController.Remove()) {
					CoreAPI.LoggerAPI.LogWarning($"Failed to remove {nameof(XRController)}.");
				}
			} catch (Exception e) {
				CoreAPI.LoggerAPI.LogError($"Headset watch failed: {e.Message}");
			}
		}

		#endregion
		
		#region Widget

		/// <summary>
		/// Provides the mod widgets to the requesting page. The request expects every widget of the mod: the
		/// page collects one entry per callback call.
		/// </summary>
		private static void OnWidgetRequest(EventData context) {
			if (!context.TryGet(0, out int mid)) return;
			if (!context.TryGet(1, out RectTransform parent)) return;

			var menu = UiAPI?.Get<IMenu>(mid);
			if (menu == null) return;

			if (StandUpWidget.TryMake(menu, parent, out var widget) && widget.Item2 != null)
				context.Callback(widget.Item2, widget.Item1);

			if (FullBodyCalibrationWidget.TryMake(menu, parent, out widget) && widget.Item2 != null)
				context.Callback(widget.Item2, widget.Item1);
		}

		/// <summary>Adds or removes the widgets when the current controller changes.</summary>
		private static void OnCurrentControllerChanged(EventData context) {
			if (context.TryGet<IXRController>(0, out var _)) {
				StandUpWidget.Show();
				FullBodyCalibrationWidget.Refresh();
			} else {
				StandUpWidget.Hide();
				FullBodyCalibrationWidget.Hide();
			}
		}

		#endregion
	}
}