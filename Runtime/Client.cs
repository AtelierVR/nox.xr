using Nox.CCK.Mods.Cores;
using Nox.CCK.Mods.Events;
using Nox.CCK.Mods.Initializers;
using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Nox.Avatars;
using Nox.Controllers;
using Nox.UI;
using Nox.CCK.XR;
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

			InputDevices.deviceConnected     += OnDeviceConnected;
			InputDevices.deviceDisconnected  += OnDeviceDisconnected;
			InputDevices.deviceConfigChanged += OnDeviceConfigChanged;

			var devices = new List<InputDevice>();
			InputDevices.GetDevices(devices);
			foreach (var device in devices)
				await OnDeviceConnectedAsync(device);
		}

		public async UniTask Quit() {
			if (!XRLoaderManager.IsRunning) {
				CoreAPI.LoggerAPI.LogWarning("No XR initialized.");
				return;
			}

			InputDevices.deviceConnected     -= OnDeviceConnected;
			InputDevices.deviceDisconnected  -= OnDeviceDisconnected;
			InputDevices.deviceConfigChanged -= OnDeviceConfigChanged;

			var devices = new List<InputDevice>();
			InputDevices.GetDevices(devices);
			foreach (var device in devices)
				await OnDeviceDisconnectedAsync(device);

			await XRLoaderManager.Stop();
		}

		#endregion

		#region Device Events

		private void OnDeviceConnected(InputDevice device)
			=> OnDeviceConnectedAsync(device).Forget();

		private async UniTask OnDeviceConnectedAsync(InputDevice device) {
			CoreAPI.LoggerAPI.Log($"Device connected: {device.name} {device.characteristics}");
			if (!device.characteristics.HasFlag(InputDeviceCharacteristics.HeadMounted)) 
				return;
			if (await XRController.Make())
				return;
			CoreAPI.LoggerAPI.LogWarning($"Failed to {nameof(XRController)}.");
		}

		private void OnDeviceDisconnected(InputDevice device)
			=> OnDeviceDisconnectedAsync(device).Forget();

		private async UniTask OnDeviceDisconnectedAsync(InputDevice device) {
			CoreAPI.LoggerAPI.Log($"Device disconnected: {device.name} {device.characteristics}");
			if (!device.characteristics.HasFlag(InputDeviceCharacteristics.HeadMounted))
				return;
			if (!await XRController.Remove())
				CoreAPI.LoggerAPI.LogWarning($"Failed to remove {nameof(XRController)}.");
		}

		private void OnDeviceConfigChanged(InputDevice device)
			=> CoreAPI.LoggerAPI.Log($"Device config changed: {device.name} {device.characteristics}");

		#endregion
		
		#region Widget

		/// <summary>
		/// Fournit les widgets du mod à la page qui les demande (voir <see cref="StandUpWidget"/>).
		/// </summary>
		private static void OnWidgetRequest(EventData context) {
			if (!context.TryGet(0, out int mid)) return;
			if (!context.TryGet(1, out RectTransform parent)) return;

			var menu = UiAPI?.Get<IMenu>(mid);
			if (menu == null) return;

			if (StandUpWidget.TryMake(menu, parent, out var widget) && widget.Item2 != null)
				context.Callback(widget.Item2, widget.Item1);
		}

		/// <summary>
		/// Le bouton « Stand up » n'a de sens que si le proxy XR est le contrôleur courant :
		/// on l'ajoute ou le retire à chaud quand le contrôleur courant change.
		/// </summary>
		private static void OnCurrentControllerChanged(EventData context) {
			if (context.TryGet<IXRController>(0, out var _))
				StandUpWidget.Show();
			else StandUpWidget.Hide();
		}

		#endregion
	}
}