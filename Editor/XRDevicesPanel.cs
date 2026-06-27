using System;
using System.Collections.Generic;
using Nox.CCK.Mods.Cores;
using Nox.CCK.Mods.Initializers;
using Nox.CCK.XR;
using Nox.Editor.Panel;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.XR;
using IPanel = Nox.Editor.Panel.IPanel;

namespace Nox.XR.Editor {
	public class XRDevicesPanel : IEditorModInitializer, IPanel {
		private static readonly string[] PanelPath = { "xr", "devices" };
		internal IEditorModCoreAPI API;

		public void OnInitializeEditor(IEditorModCoreAPI api)
			=> API = api;

		public void OnDisposeEditor()
			=> API = null;

		public string[] GetPath()
			=> PanelPath;

		internal XRDevicesInstance Instance;

		public IInstance[] GetInstances()
			=> Instance != null
				? new IInstance[] { Instance }
				: Array.Empty<IInstance>();

		public string GetLabel()
			=> "XR/Devices";

		public IInstance Instantiate(IWindow window, Dictionary<string, object> data) {
			if (Instance != null)
				throw new InvalidOperationException("XRDevicesPanel only supports a single instance.");
			return Instance = new XRDevicesInstance(this, window, data);
		}
	}

	public class XRDevicesInstance : IInstance {
		private readonly XRDevicesPanel _panel;
		private readonly IWindow _window;
		private VisualElement _content;
		private VisualElement _devicesList;
		private Label _hmdStatusLabel;
		private Label _leftControllerStatusLabel;
		private Label _rightControllerStatusLabel;
		private Label _noVrFlagLabel;
		private Label _xrInitializedLabel;
		private Label _xrReadyLabel;
		private Label _hasHeadsetLabel;
		private float _lastRefreshTime;

		public XRDevicesInstance(XRDevicesPanel panel, IWindow window, Dictionary<string, object> data) {
			_panel                   =  panel;
			_window                  =  window;
			EditorApplication.update += OnEditorUpdate;
		}

		public IPanel GetPanel()
			=> _panel;

		public IWindow GetWindow()
			=> _window;

		public string GetTitle()
			=> "VR Devices";

		public void OnDestroy() {
			EditorApplication.update -= OnEditorUpdate;
			_panel.Instance          =  null;
		}

		public IToolOption[] GetOptions()
			=> new IToolOption[] { new DefaultToolOption("Refresh", OnRefreshClicked) };

		private void OnEditorUpdate() {
			// Refresh devices info every second
			if (Time.realtimeSinceStartup - _lastRefreshTime > 0.5f) {
				_lastRefreshTime = Time.realtimeSinceStartup;
				if (_content != null) {
					RefreshDevices();
				}
			}
		}

		private void OnRefreshClicked() {
			RefreshDevices();
		}

		private void RefreshDevices() {
			if (_devicesList == null)
				return;

			// Update XR System Status
			if (_noVrFlagLabel != null) {
				var noVrFlag = !Settings.EnableXRSetting.Value;
				_noVrFlagLabel.text = noVrFlag ? "Disabled (--no-vr)" : "Enabled";
				_noVrFlagLabel.EnableInClassList("text-danger", noVrFlag);
				_noVrFlagLabel.EnableInClassList("text-success", !noVrFlag);
			}

			if (_xrInitializedLabel != null && Client.Instance != null) {
				var isInitialized = Client.Instance.IsXRInitialized();
				_xrInitializedLabel.text = isInitialized ? "Initialized" : "Not Initialized";
				_xrInitializedLabel.EnableInClassList("text-success", isInitialized);
				_xrInitializedLabel.EnableInClassList("text-danger", !isInitialized);
			}

			if (_xrReadyLabel != null && Client.Instance != null) {
				var isReady = Client.Instance.IsReady();
				_xrReadyLabel.text = isReady ? "Ready" : "Not Ready";
				_xrReadyLabel.EnableInClassList("text-success", isReady);
				_xrReadyLabel.EnableInClassList("text-warning", !isReady);
			}

			if (_hasHeadsetLabel != null && Client.Instance != null) {
				var hasHeadset = XRInputs.HasHeadset;
				_hasHeadsetLabel.text = hasHeadset ? "Detected" : "Not Detected";
				_hasHeadsetLabel.EnableInClassList("text-success", hasHeadset);
				_hasHeadsetLabel.EnableInClassList("text-danger", !hasHeadset);
			}

			// Update HMD status
			var hmdDevices = new List<InputDevice>();
			InputDevices.GetDevicesWithCharacteristics(InputDeviceCharacteristics.HeadMounted, hmdDevices);
			if (hmdDevices.Count > 0) {
				_hmdStatusLabel.text = $"Connected: {hmdDevices[0].name}";
				_hmdStatusLabel.EnableInClassList("text-success", true);
				_hmdStatusLabel.EnableInClassList("text-danger", false);
			} else {
				_hmdStatusLabel.text = "Not Connected";
				_hmdStatusLabel.EnableInClassList("text-success", false);
				_hmdStatusLabel.EnableInClassList("text-danger", true);
			}

			// Update Left Controller status
			var leftControllers = new List<InputDevice>();
			InputDevices.GetDevicesWithCharacteristics(
				InputDeviceCharacteristics.Left | InputDeviceCharacteristics.Controller,
				leftControllers
			);
			if (leftControllers.Count > 0) {
				_leftControllerStatusLabel.text = $"Connected: {leftControllers[0].name}";
				_leftControllerStatusLabel.EnableInClassList("text-success", true);
				_leftControllerStatusLabel.EnableInClassList("text-danger", false);
			} else {
				_leftControllerStatusLabel.text = "Not Connected";
				_leftControllerStatusLabel.EnableInClassList("text-success", false);
				_leftControllerStatusLabel.EnableInClassList("text-danger", true);
			}

			// Update Right Controller status
			var rightControllers = new List<InputDevice>();
			InputDevices.GetDevicesWithCharacteristics(
				InputDeviceCharacteristics.Right | InputDeviceCharacteristics.Controller,
				rightControllers
			);
			if (rightControllers.Count > 0) {
				_rightControllerStatusLabel.text = $"Connected: {rightControllers[0].name}";
				_rightControllerStatusLabel.EnableInClassList("text-success", true);
				_rightControllerStatusLabel.EnableInClassList("text-danger", false);
			} else {
				_rightControllerStatusLabel.text = "Not Connected";
				_rightControllerStatusLabel.EnableInClassList("text-success", false);
				_rightControllerStatusLabel.EnableInClassList("text-danger", true);
			}

			// List all devices with live pose
			_devicesList.Clear();
			var devices = new List<InputDevice>();
			InputDevices.GetDevices(devices);

			var noDevicesLabel = _content?.Q<Label>("no-devices");
			noDevicesLabel?.EnableInClassList("hidden", devices.Count > 0);

			foreach (var device in devices) {
				var deviceContainer = new GroupBox();
				deviceContainer.AddToClassList("p-8");
				deviceContainer.AddToClassList("m-0");
				deviceContainer.AddToClassList("border-b");

				var nameLabel = new Label($"Device: {device.name}");
				nameLabel.AddToClassList("text-bold");
				nameLabel.AddToClassList("mb-4");
				deviceContainer.Add(nameLabel);

				deviceContainer.Add(new Label($"Manufacturer: {device.manufacturer}"));
				deviceContainer.Add(new Label($"Serial: {device.serialNumber}"));

				var characteristicsLabel = new Label($"Characteristics: {device.characteristics}");
				characteristicsLabel.AddToClassList("text-wrap");
				deviceContainer.Add(characteristicsLabel);

				// Live pose
				var hasPos = device.TryGetFeatureValue(CommonUsages.devicePosition, out var pos);
				var hasRot = device.TryGetFeatureValue(CommonUsages.deviceRotation, out var rot);
				if (hasPos && hasRot) {
					var posLabel = new Label($"Position: {pos.x:F3}, {pos.y:F3}, {pos.z:F3}");
					posLabel.AddToClassList("text-small");
					deviceContainer.Add(posLabel);

					var euler = rot.eulerAngles;
					var rotLabel = new Label($"Rotation: {euler.x:F1}°, {euler.y:F1}°, {euler.z:F1}°");
					rotLabel.AddToClassList("text-small");
					deviceContainer.Add(rotLabel);
				}

				var isValidLabel = new Label($"Valid: {device.isValid}");
				isValidLabel.EnableInClassList("text-success", device.isValid);
				isValidLabel.EnableInClassList("text-danger", !device.isValid);
				deviceContainer.Add(isValidLabel);

				_devicesList.Add(deviceContainer);
			}
		}

		public VisualElement GetContent() {
			if (_content != null)
				return _content;

			_content = _panel.API.AssetAPI.GetAsset<VisualTreeAsset>("xr-devices.uxml").CloneTree();
			_content.AddToClassList("flex-fill");

			_noVrFlagLabel             = _content.Q<Label>("vr-mode");
			_xrInitializedLabel        = _content.Q<Label>("xr-system");
			_xrReadyLabel              = _content.Q<Label>("xr-ready");
			_hasHeadsetLabel           = _content.Q<Label>("headset-detection");
			_hmdStatusLabel            = _content.Q<Label>("hmd-status");
			_leftControllerStatusLabel  = _content.Q<Label>("left-controller");
			_rightControllerStatusLabel = _content.Q<Label>("right-controller");
			_devicesList               = _content.Q<VisualElement>("devices-list");

			RefreshDevices();
			return _content;
		}
	}
}