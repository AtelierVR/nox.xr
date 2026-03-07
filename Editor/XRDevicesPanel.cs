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
		private Button _refreshButton;
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

		private void OnEditorUpdate() {
			// Refresh devices info every second
			if (Time.realtimeSinceStartup - _lastRefreshTime > 1f) {
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
				var noVrFlag = XRController.NoVRFlag;
				_noVrFlagLabel.text = noVrFlag ? "Disabled (--no-vr)" : "Enabled";
				_noVrFlagLabel.style.color = noVrFlag ? new Color(0.8f, 0.3f, 0.3f) : new Color(0.3f, 0.8f, 0.3f);
			}

			if (_xrInitializedLabel != null && Client.Instance != null) {
				var isInitialized = Client.Instance.IsXRInitialized();
				_xrInitializedLabel.text = isInitialized ? "Initialized" : "Not Initialized";
				_xrInitializedLabel.style.color = isInitialized ? new Color(0.3f, 0.8f, 0.3f) : new Color(0.8f, 0.3f, 0.3f);
			}

			if (_xrReadyLabel != null && Client.Instance != null) {
				var isReady = Client.Instance.IsReady();
				_xrReadyLabel.text = isReady ? "Ready" : "Not Ready";
				_xrReadyLabel.style.color = isReady ? new Color(0.3f, 0.8f, 0.3f) : new Color(0.8f, 0.8f, 0.3f);
			}

			if (_hasHeadsetLabel != null && Client.Instance != null) {
				var hasHeadset = XRInputs.HasHeadset;
				_hasHeadsetLabel.text = hasHeadset ? "Detected" : "Not Detected";
				_hasHeadsetLabel.style.color = hasHeadset ? new Color(0.3f, 0.8f, 0.3f) : new Color(0.8f, 0.3f, 0.3f);
			}

			// Update HMD status
			var hmdDevices = new List<InputDevice>();
			InputDevices.GetDevicesWithCharacteristics(InputDeviceCharacteristics.HeadMounted, hmdDevices);

			if (hmdDevices.Count > 0) {
				var hmd = hmdDevices[0];
				_hmdStatusLabel.text        = $"Connected: {hmd.name}";
				_hmdStatusLabel.style.color = new Color(0.3f, 0.8f, 0.3f);
			} else {
				_hmdStatusLabel.text        = "Not Connected";
				_hmdStatusLabel.style.color = new Color(0.8f, 0.3f, 0.3f);
			}

			// Update Left Controller status
			var leftControllers = new List<InputDevice>();
			InputDevices.GetDevicesWithCharacteristics(
				InputDeviceCharacteristics.Left | InputDeviceCharacteristics.Controller,
				leftControllers
			);

			if (leftControllers.Count > 0) {
				var controller = leftControllers[0];
				_leftControllerStatusLabel.text        = $"Connected: {controller.name}";
				_leftControllerStatusLabel.style.color = new Color(0.3f, 0.8f, 0.3f);
			} else {
				_leftControllerStatusLabel.text        = "Not Connected";
				_leftControllerStatusLabel.style.color = new Color(0.8f, 0.3f, 0.3f);
			}

			// Update Right Controller status
			var rightControllers = new List<InputDevice>();
			InputDevices.GetDevicesWithCharacteristics(
				InputDeviceCharacteristics.Right | InputDeviceCharacteristics.Controller,
				rightControllers
			);

			if (rightControllers.Count > 0) {
				var controller = rightControllers[0];
				_rightControllerStatusLabel.text        = $"Connected: {controller.name}";
				_rightControllerStatusLabel.style.color = new Color(0.3f, 0.8f, 0.3f);
			} else {
				_rightControllerStatusLabel.text        = "Not Connected";
				_rightControllerStatusLabel.style.color = new Color(0.8f, 0.3f, 0.3f);
			}

			// List all devices
			_devicesList.Clear();
			var devices = new List<InputDevice>();
			InputDevices.GetDevices(devices);

			if (devices.Count == 0) {
				var noDeviceLabel = new Label("No VR devices detected") {
					style = {
						unityTextAlign = TextAnchor.MiddleCenter,
						paddingTop     = 20,
						paddingBottom  = 20,
						color          = new Color(0.6f, 0.6f, 0.6f)
					}
				};
				_devicesList.Add(noDeviceLabel);
			} else {
				foreach (var device in devices) {
					var deviceContainer = new VisualElement {
						style = {
							backgroundColor         = new Color(0.25f, 0.25f, 0.25f),
							marginBottom            = 5,
							paddingBottom           = 10,
							paddingLeft             = 10,
							paddingRight            = 10,
							paddingTop              = 10,
							borderBottomLeftRadius  = 4,
							borderBottomRightRadius = 4,
							borderTopLeftRadius     = 4,
							borderTopRightRadius    = 4
						}
					};

					var nameLabel = new Label($"Device: {device.name}") {
						style = {
							unityFontStyleAndWeight = FontStyle.Bold,
							marginBottom            = 5
						}
					};
					deviceContainer.Add(nameLabel);

					var manufacturerLabel = new Label($"Manufacturer: {device.manufacturer}");
					deviceContainer.Add(manufacturerLabel);

					var serialLabel = new Label($"Serial: {device.serialNumber}");
					deviceContainer.Add(serialLabel);

					var characteristicsLabel = new Label($"Characteristics: {device.characteristics}") {
						style = {
							whiteSpace = WhiteSpace.Normal,
							flexWrap   = Wrap.Wrap
						}
					};
					deviceContainer.Add(characteristicsLabel);

					var isValidLabel = new Label($"Valid: {device.isValid}") {
						style = {
							color = device.isValid ? new Color(0.3f, 0.8f, 0.3f) : new Color(0.8f, 0.3f, 0.3f)
						}
					};
					deviceContainer.Add(isValidLabel);

					_devicesList.Add(deviceContainer);
				}
			}
		}

		public VisualElement GetContent() {
			if (_content != null)
				return _content;

			var root = new VisualElement {
				style = {
					paddingTop    = 10,
					paddingBottom = 10,
					paddingLeft   = 10,
					paddingRight  = 10
				}
			};

			// Title
			var titleLabel = new Label("VR Devices Information") {
				style = {
					fontSize                = 18,
					unityFontStyleAndWeight = FontStyle.Bold,
					marginBottom            = 10,
					unityTextAlign          = TextAnchor.MiddleCenter
				}
			};
			root.Add(titleLabel);

			// Refresh button
			_refreshButton = new Button(OnRefreshClicked) {
				text = "Refresh Devices",
				style = {
					marginBottom = 10
				}
			};
			root.Add(_refreshButton);

			// XR System Status section
			var xrSystemContainer = new VisualElement {
				style = {
					backgroundColor         = new Color(0.2f, 0.2f, 0.25f),
					paddingBottom           = 10,
					paddingLeft             = 10,
					paddingRight            = 10,
					paddingTop              = 10,
					marginBottom            = 10,
					borderBottomLeftRadius  = 4,
					borderBottomRightRadius = 4,
					borderTopLeftRadius     = 4,
					borderTopRightRadius    = 4
				}
			};

			var xrSystemTitle = new Label("XR System Status") {
				style = {
					fontSize                = 14,
					unityFontStyleAndWeight = FontStyle.Bold,
					marginBottom            = 5
				}
			};
			xrSystemContainer.Add(xrSystemTitle);

			// NoVR Flag Status
			var noVrContainer = new VisualElement {
				style = {
					flexDirection = FlexDirection.Row,
					marginBottom  = 5
				}
			};
			var noVrLabel = new Label("VR Mode: ") {
				style = {
					width = 150
				}
			};
			_noVrFlagLabel = new Label("Checking...");
			noVrContainer.Add(noVrLabel);
			noVrContainer.Add(_noVrFlagLabel);
			xrSystemContainer.Add(noVrContainer);

			// XR Initialized Status
			var xrInitContainer = new VisualElement {
				style = {
					flexDirection = FlexDirection.Row,
					marginBottom  = 5
				}
			};
			var xrInitLabel = new Label("XR System: ") {
				style = {
					width = 150
				}
			};
			_xrInitializedLabel = new Label("Checking...");
			xrInitContainer.Add(xrInitLabel);
			xrInitContainer.Add(_xrInitializedLabel);
			xrSystemContainer.Add(xrInitContainer);

			// XR Ready Status
			var xrReadyContainer = new VisualElement {
				style = {
					flexDirection = FlexDirection.Row,
					marginBottom  = 5
				}
			};
			var xrReadyLabel = new Label("XR Ready: ") {
				style = {
					width = 150
				}
			};
			_xrReadyLabel = new Label("Checking...");
			xrReadyContainer.Add(xrReadyLabel);
			xrReadyContainer.Add(_xrReadyLabel);
			xrSystemContainer.Add(xrReadyContainer);

			// Has Headset Status
			var hasHeadsetContainer = new VisualElement {
				style = {
					flexDirection = FlexDirection.Row,
					marginBottom  = 5
				}
			};
			var hasHeadsetLabel = new Label("Headset Detection: ") {
				style = {
					width = 150
				}
			};
			_hasHeadsetLabel = new Label("Checking...");
			hasHeadsetContainer.Add(hasHeadsetLabel);
			hasHeadsetContainer.Add(_hasHeadsetLabel);
			xrSystemContainer.Add(hasHeadsetContainer);

			root.Add(xrSystemContainer);

			// Quick status section
			var statusContainer = new VisualElement {
				style = {
					backgroundColor         = new Color(0.2f, 0.2f, 0.2f),
					paddingBottom           = 10,
					paddingLeft             = 10,
					paddingRight            = 10,
					paddingTop              = 10,
					marginBottom            = 10,
					borderBottomLeftRadius  = 4,
					borderBottomRightRadius = 4,
					borderTopLeftRadius     = 4,
					borderTopRightRadius    = 4
				}
			};

			var statusTitle = new Label("Quick Status") {
				style = {
					fontSize                = 14,
					unityFontStyleAndWeight = FontStyle.Bold,
					marginBottom            = 5
				}
			};
			statusContainer.Add(statusTitle);

			// HMD Status
			var hmdContainer = new VisualElement {
				style = {
					flexDirection = FlexDirection.Row,
					marginBottom  = 5
				}
			};
			var hmdLabel = new Label("HMD: ") {
				style = {
					width = 150
				}
			};
			_hmdStatusLabel = new Label("Checking...");
			hmdContainer.Add(hmdLabel);
			hmdContainer.Add(_hmdStatusLabel);
			statusContainer.Add(hmdContainer);

			// Left Controller Status
			var leftContainer = new VisualElement {
				style = {
					flexDirection = FlexDirection.Row,
					marginBottom  = 5
				}
			};
			var leftLabel = new Label("Left Controller: ") {
				style = {
					width = 150
				}
			};
			_leftControllerStatusLabel = new Label("Checking...");
			leftContainer.Add(leftLabel);
			leftContainer.Add(_leftControllerStatusLabel);
			statusContainer.Add(leftContainer);

			// Right Controller Status
			var rightContainer = new VisualElement {
				style = {
					flexDirection = FlexDirection.Row,
					marginBottom  = 5
				}
			};
			var rightLabel = new Label("Right Controller: ") {
				style = {
					width = 150
				}
			};
			_rightControllerStatusLabel = new Label("Checking...");
			rightContainer.Add(rightLabel);
			rightContainer.Add(_rightControllerStatusLabel);
			statusContainer.Add(rightContainer);

			root.Add(statusContainer);

			// Devices list section
			var devicesTitle = new Label("All Detected Devices") {
				style = {
					fontSize                = 14,
					unityFontStyleAndWeight = FontStyle.Bold,
					marginBottom            = 5,
					marginTop               = 10
				}
			};
			root.Add(devicesTitle);

			_devicesList = new ScrollView {
				style = {
					maxHeight = 400
				}
			};
			root.Add(_devicesList);

			// Initial refresh
			RefreshDevices();

			return _content = root;
		}
	}
}