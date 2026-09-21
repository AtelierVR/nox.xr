using System;
using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using Nox.CCK.Mods.Cores;
using Nox.CCK.Mods.Initializers;
using Nox.CCK.Utils;
using Nox.CCK.XR;
using Nox.Editor.Panel;
using Nox.XR.Loaders;
using Nox.XR.Runtime;
using Nox.XR.Runtime.Loaders;
using Nox.XR.Runtime.Panels;
using Nox.XR.Runtime.Settings;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.XR.Management;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.XR;
using UnityEngine.XR.Management;
using IPanel = Nox.Editor.Panel.IPanel;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.XR.Editor {
	/// <summary>
	/// Panel d'inspection XR : état du loader, provider d'input, devices, trackers, configuration
	/// XR Plug-in Management et réglages, plus quelques actions (démarrer/arrêter la VR, poke, FBT).
	///
	/// <para>
	/// Toutes les lignes sont regroupées dans des <c>Foldout</c> et construites ici plutôt qu'en UXML :
	/// le panel expose beaucoup de valeurs qui se ressemblent, et un helper unique garantit le même
	/// alignement (classe <c>key-label</c>) et le même code couleur partout.
	/// </para>
	/// </summary>
	public class XRPanel : IEditorModInitializer, IPanel {
		private static readonly string[] PanelPath = { "xr" };

		internal IEditorModCoreAPI API;
		internal XRPanelInstance Instance;

		public void OnInitializeEditor(IEditorModCoreAPI api)
			=> API = api;

		public void OnDisposeEditor() {
			Instance?.OnDestroy();
			API = null;
		}

		public void OnUpdateEditor()
			=> Instance?.OnUpdate();

		public string[] GetPath()
			=> PanelPath;

		public string GetLabel()
			=> "XR";

		public IInstance[] GetInstances()
			=> Instance != null
				? new IInstance[] { Instance }
				: Array.Empty<IInstance>();

		public IInstance Instantiate(IWindow window, Dictionary<string, object> data) {
			if (Instance != null)
				throw new InvalidOperationException("XRPanel only supports a single instance.");
			return Instance = new XRPanelInstance(this, window, data);
		}
	}

	public class XRPanelInstance : IInstance {
		/// <summary>Cadence de rafraîchissement : assez rapide pour suivre une pose, assez lente pour ne rien coûter.</summary>
		private const double RefreshInterval = 0.5;

		private readonly XRPanel _panel;
		private readonly IWindow _window;

		private VisualElement _content;
		private VisualElement _devicesList;
		private VisualElement _trackersList;
		private VisualElement _loadersList;
		private VisualElement _actions;
		private Label _noDevicesLabel;

		private Button _vrButton;
		private Button _pokeButton;
		private Button _fbtButton;

		private readonly Dictionary<string, Label>   _rows     = new();
		private readonly Dictionary<string, Foldout> _foldouts = new();

		private double _lastRefresh = double.MinValue;

		public XRPanelInstance(XRPanel panel, IWindow window, Dictionary<string, object> data) {
			_panel  = panel;
			_window = window;
		}

		public IPanel GetPanel()
			=> _panel;

		public IWindow GetWindow()
			=> _window;

		public string GetTitle()
			=> "XR";

		public IToolOption[] GetOptions()
			=> new IToolOption[] { new DefaultToolOption("Refresh", Refresh, "Force la mise à jour de toutes les valeurs.") };

		public void OnDestroy() {
			_panel.Instance = null;
			_rows.Clear();
			_foldouts.Clear();
		}

		public void OnUpdate() {
			if (_content == null)
				return;

			if (EditorApplication.timeSinceStartup - _lastRefresh < RefreshInterval)
				return;

			_lastRefresh = EditorApplication.timeSinceStartup;
			Refresh();
		}

		#region Content

		public VisualElement GetContent() {
			if (_content != null)
				return _content;

			_content = _panel.API.AssetAPI.GetAsset<VisualTreeAsset>("xr-panel.uxml").CloneTree();
			_content.AddToClassList("flex-fill");

			_devicesList    = _content.Q<VisualElement>("devices-list");
			_trackersList   = _content.Q<VisualElement>("trackers-list");
			_loadersList    = _content.Q<VisualElement>("loaders-list");
			_actions        = _content.Q<VisualElement>("action-buttons");
			_noDevicesLabel = _content.Q<Label>("no-devices");

			CacheFoldouts();
			BuildRows();
			BuildActions();

			Refresh();
			return _content;
		}

		private void CacheFoldouts() {
			foreach (var name in new[] { "sec-status", "sec-input", "sec-devices", "sec-trackers", "sec-management", "sec-settings", "sec-raw" }) {
				var foldout = _content.Q<Foldout>(name);
				if (foldout != null)
					_foldouts[name] = foldout;
			}
		}

		/// <summary>
		/// Une section n'est rafraîchie que si elle est dépliée : replier « Devices » ne doit pas
		/// reconstruire la liste des devices à chaque tick.
		/// </summary>
		private bool IsOpen(string id)
			=> !_foldouts.TryGetValue(id, out var foldout) || foldout.value;

		private void BuildRows() {
			// --- XR Status ---------------------------------------------------------------
			var status = _content.Q<VisualElement>("rows-status");
			AddRow(status, "editor-mode",    "Editor");
			AddRow(status, "vr-mode",        "VR Mode");
			AddRow(status, "mod-loader",     "Loader (nox.xr)");
			AddRow(status, "unity-loader",   "Loader (XR Mgmt)");
			AddRow(status, "display",        "Display Subsystem");
			AddRow(status, "input",          "Input Subsystem");
			AddRow(status, "client",         "Client");
			AddRow(status, "client-running", "XR Running");
			AddRow(status, "client-ready",   "XR Ready");
			AddRow(status, "headset",        "Headset Detection");

			// --- Input & Tracking --------------------------------------------------------
			var input = _content.Q<VisualElement>("rows-input");
			AddRow(input, "provider-active",   "Provider actif");
			AddRow(input, "provider-override", "Provider (mod)");
			AddRow(input, "provider-default",  "Provider (repli)");
			AddRow(input, "has-left",          "Main gauche");
			AddRow(input, "has-right",         "Main droite");
			AddTitle(input, "Poses (world)");
			AddRow(input, "pose-head",   "Tête");
			AddRow(input, "pose-left",   "Main gauche");
			AddRow(input, "pose-right",  "Main droite");
			AddRow(input, "hand-spread", "Écartement mains");
			AddTitle(input, "XR Origin");
			AddRow(input, "xr-origin",       "Origin");
			AddRow(input, "xr-origin-pos",   "Position");
			AddRow(input, "xr-origin-scale", "Scale");

			// --- Devices -----------------------------------------------------------------
			var devices = _content.Q<VisualElement>("rows-devices");
			AddRow(devices, "dev-count", "Devices détectés");
			AddRow(devices, "dev-hmd",   "HMD");
			AddRow(devices, "dev-left",  "Contrôleur gauche");
			AddRow(devices, "dev-right", "Contrôleur droit");

			// --- Trackers & Full-Body ----------------------------------------------------
			var trackers = _content.Q<VisualElement>("rows-trackers");
			AddRow(trackers, "fbt-setting", "Réglage FBT");
			AddRow(trackers, "fbt-provider", "Détection (provider)");
			AddRow(trackers, "fbt-count",    "Trackers détectés");
			AddRow(trackers, "fbt-roles",    "Rôles assignés");

			// --- XR Plug-in Management ---------------------------------------------------
			var management = _content.Q<VisualElement>("rows-management");
			AddRow(management, "platform",       "Plateforme");
			AddRow(management, "init-complete",  "Initialisation");
			AddRow(management, "configured",     "Loaders configurés");

			// --- Settings ----------------------------------------------------------------
			var settings = _content.Q<VisualElement>("rows-settings");
			AddRow(settings, "set-enabled", "XR activé");
			AddRow(settings, "set-ipd",     "IPD");
			AddRow(settings, "set-fbt",     "Full-Body Tracking");
			AddRow(settings, "set-poke",    "Poke");
			AddRow(settings, "set-poke-pct", "Seuil de désactivation poke");

			// --- Raw devices -------------------------------------------------------------
			var raw = _content.Q<VisualElement>("rows-raw");
			AddRow(raw, "raw-count",      "Devices InputDevices");
			AddRow(raw, "raw-xs-count",   "Sous-systèmes Input");
			AddRow(raw, "raw-chars",      "Caractéristiques cumulées");
		}

		private void BuildActions() {
			_vrButton = AddButton(_actions, "Start VR", OnToggleVr, "Démarre / arrête la XR (même action que XR/General/Start VR).");
			_pokeButton = AddButton(_actions, "Toggle poke", () => PokeSettings.Enabled = !PokeSettings.Enabled);
			_fbtButton = AddButton(_actions, "Toggle FBT", () => FullBodyTrackingSetting.Value = !FullBodyTrackingSetting.Value);
		}

		#endregion

		#region Refresh

		private void Refresh() {
			if (_content == null)
				return;

			RefreshStatus();
			RefreshInput();
			RefreshDevices();
			RefreshTrackers();
			RefreshManagement();
			RefreshSettings();
			RefreshRaw();
			RefreshActions();
		}

		private void RefreshStatus() {
			SetRow("editor-mode", Application.isPlaying ? "Play mode" : "Edit mode", Application.isPlaying ? RowStatus.Ok : RowStatus.Neutral);

			var enabled = EnableXRSetting.Value;
			SetRow("vr-mode", enabled ? "Enabled" : "Disabled (--no-vr ou réglage)", Status(enabled));

			var provider = XRLoaderManager.Current;
			SetRow("mod-loader",
				provider != null ? $"{provider.Id} — priorité {provider.Priority}" : "Aucun",
				Status(provider != null));

			var loader = XRManagementLoader.Active;
			SetRow("unity-loader",
				loader != null ? $"{loader.name} ({loader.GetType().Name})" : "Aucun",
				Status(loader != null));

			var display = loader?.GetLoadedSubsystem<XRDisplaySubsystem>();
			SetRow("display",
				display == null ? "Non chargé" : display.running ? "Running" : "Chargé, à l'arrêt",
				Status(display?.running ?? false));

			var input = loader?.GetLoadedSubsystem<XRInputSubsystem>();
			SetRow("input",
				input == null ? "Non chargé" : input.running ? "Running" : "Chargé, à l'arrêt",
				Status(input?.running ?? false));

			var client = Client.Instance;
			SetRow("client", client == null ? "Non instancié" : "Instancié", client != null ? RowStatus.Ok : RowStatus.Warn);
			SetRow("client-running", client?.IsRunning == true ? "Oui" : "Non", Status(client?.IsRunning ?? false));
			SetRow("client-ready", client?.IsReady() == true ? "Prêt" : "Pas prêt", client?.IsReady() == true ? RowStatus.Ok : RowStatus.Warn);

			// XRInputs.HasHeadset interroge le provider d'input, pas InputDevices : afficher la source
			// évite de conclure à tort que le casque n'est pas vu (voir aussi la section Devices).
			var inputProvider = XRInputs.ActiveProvider;
			var hasHeadset    = XRInputs.HasHeadset;
			SetRow("headset",
				inputProvider == null
					? "Aucun provider d'input"
					: hasHeadset
						? $"Détecté ({inputProvider.GetType().Name})"
						: $"Non détecté ({inputProvider.GetType().Name})",
				inputProvider == null ? RowStatus.Warn : Status(hasHeadset));
		}

		private void RefreshInput() {
			SetRow("provider-active",   Name(XRInputs.ActiveProvider),   Status(XRInputs.ActiveProvider != null));
			SetRow("provider-override", XRInputs.Provider == null ? "— (aucun)" : Name(XRInputs.Provider));
			SetRow("provider-default",  XRInputs.DefaultProvider == null ? "— (aucun)" : Name(XRInputs.DefaultProvider));

			SetRow("has-left",  XRInputs.HasHandLeft ? "Trackée" : "Absente",     Status(XRInputs.HasHandLeft));
			SetRow("has-right", XRInputs.HasHandRight ? "Trackée" : "Absente",    Status(XRInputs.HasHandRight));

			if (XRInputs.GetHeadsetPose(out var headPos, out var headRot))
				SetRow("pose-head", Pose(headPos, headRot), RowStatus.Ok);
			else
				SetRow("pose-head", "Non disponible", RowStatus.Bad);

			if (XRInputs.GetLeftHandPose(out var leftPos, out var leftRot))
				SetRow("pose-left", Pose(leftPos, leftRot), RowStatus.Ok);
			else
				SetRow("pose-left", "Non disponible", RowStatus.Bad);

			if (XRInputs.GetRightHandPose(out var rightPos, out var rightRot))
				SetRow("pose-right", Pose(rightPos, rightRot), RowStatus.Ok);
			else
				SetRow("pose-right", "Non disponible", RowStatus.Bad);

			SetRow("hand-spread",
				XRHandTracking.TryGetHandDistance(out var distance) ? $"{distance * 100f:F1} cm" : "Non disponible",
				XRHandTracking.TryGetHandDistance(out _) ? RowStatus.Ok : RowStatus.Bad);

			var origin = XROriginSetter.GlobalOrigin;
			SetRow("xr-origin", origin != null ? origin.gameObject.name : "Aucun (XROriginSetter absent)", Status(origin != null));
			SetRow("xr-origin-pos", origin != null ? $"{origin.transform.position.x:F2}, {origin.transform.position.y:F2}, {origin.transform.position.z:F2}" : "—");
			SetRow("xr-origin-scale", origin != null
				? $"{origin.transform.lossyScale.x:F2}, {origin.transform.lossyScale.y:F2}, {origin.transform.lossyScale.z:F2}"
				: "—");
		}

		private void RefreshDevices() {
			var hmd = new List<InputDevice>();
			InputDevices.GetDevicesWithCharacteristics(InputDeviceCharacteristics.HeadMounted, hmd);

			var hands = new List<InputDevice>();
			InputDevices.GetDevices(hands);

			var left  = DevicesWith(InputDeviceCharacteristics.Left | InputDeviceCharacteristics.Controller);
			var right = DevicesWith(InputDeviceCharacteristics.Right | InputDeviceCharacteristics.Controller);

			SetRow("dev-count", hands.Count.ToString(), hands.Count > 0 ? RowStatus.Ok : RowStatus.Bad);
			SetRow("dev-hmd",   hmd.Count > 0 ? $"{hmd[0].name}" : "Non détecté",     Status(hmd.Count > 0));
			SetRow("dev-left",  left.Count > 0 ? left[0].name : "Non détecté",        Status(left.Count > 0));
			SetRow("dev-right", right.Count > 0 ? right[0].name : "Non détecté",      Status(right.Count > 0));

			if (_devicesList == null || !IsOpen("sec-devices"))
				return;

			_devicesList.Clear();
			_noDevicesLabel?.EnableInClassList("hidden", hands.Count > 0);

			foreach (var device in hands)
				_devicesList.Add(BuildDeviceFoldout(device));
		}

		private VisualElement BuildDeviceFoldout(InputDevice device) {
			var name    = string.IsNullOrEmpty(device.name) ? "<sans nom>" : device.name;
			var foldout = new Foldout { text = $"{name} — {device.characteristics}", value = false };
			foldout.AddToClassList("p-8");
			foldout.AddToClassList("mb-4");

			AddInfo(foldout, "Manufacturer", string.IsNullOrEmpty(device.manufacturer) ? "—" : device.manufacturer);
			AddInfo(foldout, "Serial",       string.IsNullOrEmpty(device.serialNumber) ? "—" : device.serialNumber);
			SetStatus(AddInfo(foldout, "Valid", device.isValid ? "Oui" : "Non"), Status(device.isValid));

			if (device.TryGetFeatureValue(CommonUsages.isTracked, out bool tracked))
				SetStatus(AddInfo(foldout, "Tracked", tracked ? "Oui" : "Non"), Status(tracked));

			var hasPos = device.TryGetFeatureValue(CommonUsages.devicePosition, out var pos);
			var hasRot = device.TryGetFeatureValue(CommonUsages.deviceRotation, out var rot);
			if (hasPos && hasRot) {
				AddInfo(foldout, "Position", $"{pos.x:F3}, {pos.y:F3}, {pos.z:F3}");
				var euler = rot.eulerAngles;
				AddInfo(foldout, "Rotation", $"{euler.x:F1}°, {euler.y:F1}°, {euler.z:F1}°");
			}

			if (device.TryGetFeatureValue(CommonUsages.batteryLevel, out float battery))
				AddInfo(foldout, "Batterie", $"{battery * 100f:F0} %");

			return foldout;
		}

		private void RefreshTrackers() {
			SetRow("fbt-setting", FullBodyTrackingSetting.Value ? "Activé" : "Désactivé", Status(FullBodyTrackingSetting.Value));
			SetRow("fbt-provider",
				XRInputs.HasDevice(XRNode.HardwareTracker) ? "Tracker vu par le provider" : "Aucun tracker via le provider",
				Status(XRInputs.HasDevice(XRNode.HardwareTracker)));

			var trackers = Trackers();
			SetRow("fbt-count", trackers.Count.ToString(), trackers.Count > 0 ? RowStatus.Ok : RowStatus.Neutral);
			SetRow("fbt-roles", $"{trackers.Count(t => (t.characteristics & InputDeviceCharacteristics.Left) != 0)} gauche / " +
								$"{trackers.Count(t => (t.characteristics & InputDeviceCharacteristics.Right) != 0)} droite",
				RowStatus.Neutral);

			if (_trackersList == null || !IsOpen("sec-trackers"))
				return;

			_trackersList.Clear();
			foreach (var tracker in trackers)
				AddInfo(_trackersList, string.IsNullOrEmpty(tracker.name) ? "<sans nom>" : tracker.name, tracker.characteristics.ToString());
		}

		private void RefreshManagement() {
			SetRow("platform", PlatformExtensions.CurrentPlatform.ToString(), RowStatus.Neutral);

			var manager = XRGeneralSettings.Instance?.Manager;
			SetRow("init-complete",
				manager == null ? "Aucun XRGeneralSettings" : manager.isInitializationComplete ? "Terminée" : "En cours",
				manager == null ? RowStatus.Warn : Status(manager.isInitializationComplete));

			var configured = ConfiguredLoaders();
			SetRow("configured",
				configured.Count == 0 ? "Aucun pour la cible active" : string.Join(", ", configured),
				configured.Count > 0 ? RowStatus.Ok : RowStatus.Warn);

			if (_loadersList == null || !IsOpen("sec-management"))
				return;

			_loadersList.Clear();

			// Providers de mods + repli, tels que XRLoaderManager les essaie.
			AddTitle(_loadersList, "nox.xr — loaders (par priorité)");
			foreach (var provider in XRLoaderManager.Providers) {
				var line = $"{provider.Id} — priorité {provider.Priority} — " +
						   (provider.IsValid ? "valide" : "invalide sur cette plateforme") +
						   (provider == XRLoaderManager.Current ? "  ◀ actif" : "");
				SetStatus(AddInfo(_loadersList, provider.Id, line), provider.IsValid ? RowStatus.Ok : RowStatus.Neutral);
			}

			// Providers éditeur : ce que nox.xr peut déclarer dans XR Plug-in Management.
			var editors = XRLoaderEditorRegistry.Registered;
			AddTitle(_loadersList, "nox.xr — providers éditeur");
			if (editors.Count == 0) {
				AddInfo(_loadersList, "—", "Aucun provider éditeur enregistré");
			} else {
				foreach (var editor in editors) {
					var supported = editor.IsSupported(PlatformExtensions.CurrentPlatform);
					var loader    = editor.Loader;
					var line      = $"{editor.Id} — priorité {editor.Priority} — " +
									(supported ? "supporté" : "non supporté sur cette plateforme") +
									$" — asset : {(loader != null ? loader.name : "introuvable")}";
					SetStatus(AddInfo(_loadersList, editor.Id, line), supported && loader != null ? RowStatus.Ok : RowStatus.Warn);
				}
			}

			// Loaders XR Management de la cible active (ce que la build démarrera).
			AddTitle(_loadersList, $"XR Plug-in Management — {PlatformExtensions.CurrentPlatform}");
			if (configured.Count == 0)
				AddInfo(_loadersList, "—", "Aucun loader configuré");
			else
				foreach (var name in configured)
					AddInfo(_loadersList, name, name == manager?.activeLoader?.name ? "actif" : "configuré");

			var active = manager?.activeLoader;
			if (active != null)
				AddInfo(_loadersList, "Sous-systèmes actifs", Describe(active));
		}

		private void RefreshSettings() {
			SetRow("set-enabled", EnableXRSetting.Value ? "Activé" : "Désactivé", Status(EnableXRSetting.Value));
			SetRow("set-ipd", $"{IPDSetting.Value * 1000f:F1} mm");
			SetRow("set-fbt", FullBodyTrackingSetting.Value ? "Activé" : "Désactivé", Status(FullBodyTrackingSetting.Value));
			SetRow("set-poke", PokeSettings.Enabled ? "Activé" : "Désactivé", Status(PokeSettings.Enabled));
			SetRow("set-poke-pct", $"{PokeSettings.DisablePokePercent * 100f:F0} %");
		}

		private void RefreshRaw() {
			var devices = new List<InputDevice>();
			InputDevices.GetDevices(devices);
			SetRow("raw-count", devices.Count.ToString());

			var subsystems = new List<XRInputSubsystem>();
			SubsystemManager.GetSubsystems(subsystems);
			SetRow("raw-xs-count", subsystems.Count.ToString());

			SetRow("raw-chars", string.Join(" | ", devices
				.Select(d => string.IsNullOrEmpty(d.name) ? "?" : d.name)
				.Distinct()));
		}

		private void RefreshActions() {
			if (_vrButton != null) {
				_vrButton.text = XRLoaderManager.IsRunning ? "Stop VR" : "Start VR";
				_vrButton.SetEnabled(Client.Instance != null);
			}

			if (_pokeButton != null)
				_pokeButton.text = PokeSettings.Enabled ? "Désactiver poke" : "Activer poke";

			if (_fbtButton != null)
				_fbtButton.text = FullBodyTrackingSetting.Value ? "Désactiver FBT" : "Activer FBT";
		}

		#endregion

		#region Actions

		private void OnToggleVr() {
			if (Client.Instance == null)
				return;

			// Même logique que StartVRSetting : sortir de la VR doit arrêter le loader ET retirer
			// le proxy XR, sinon le proxy courant garde un tracking mort.
			var task = XRLoaderManager.IsRunning ? Client.Instance.Quit() : Client.Instance.Enter();
			task.Forget(e => Logger.LogError($"XR toggle failed: {e.Message}"));

			Refresh();
		}

		#endregion

		#region Helpers

		private enum RowStatus { Neutral, Ok, Warn, Bad }

		private static RowStatus Status(bool ok)
			=> ok ? RowStatus.Ok : RowStatus.Bad;

		private static string Name(IXRInputProvider provider)
			=> provider == null ? "Aucun" : provider.GetType().Name;

		private static string Pose(Vector3 position, Quaternion rotation) {
			var euler = rotation.eulerAngles;
			return $"({position.x:F2}, {position.y:F2}, {position.z:F2})  •  ({euler.x:F0}°, {euler.y:F0}°, {euler.z:F0}°)";
		}

		private static List<InputDevice> DevicesWith(InputDeviceCharacteristics characteristics) {
			var devices = new List<InputDevice>();
			InputDevices.GetDevicesWithCharacteristics(characteristics, devices);
			return devices;
		}

		/// <summary>
		/// Trackers FBT : mêmes critères que <c>AutoHandProvider.GetTrackers</c> (device suivi qui n'est
		/// ni le casque ni un contrôleur), pour que le panel et le provider d'input voient la même chose.
		/// </summary>
		private static List<InputDevice> Trackers() {
			var devices = new List<InputDevice>();
			InputDevices.GetDevices(devices);
			return devices.FindAll(d => d.characteristics.HasFlag(InputDeviceCharacteristics.TrackedDevice)
										&& !d.characteristics.HasFlag(InputDeviceCharacteristics.HeadMounted)
										&& !d.characteristics.HasFlag(InputDeviceCharacteristics.Controller));
		}

		private static List<string> ConfiguredLoaders() {
			var loaders = new List<string>();

			var group    = BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget);
			var settings = group == BuildTargetGroup.Unknown ? null : XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(group);
			var manager  = settings?.Manager;
			if (manager?.activeLoaders == null)
				return loaders;

			loaders.AddRange(manager.activeLoaders.Where(l => l != null).Select(l => l.name));
			return loaders;
		}

		/// <summary>
		/// Sous-systèmes réellement chargés par un loader : c'est ce qui distingue un XR démarré
		/// d'un loader simplement configuré.
		/// </summary>
		private static string Describe(XRLoader loader) {
			var parts = new List<string>();
			if (loader.GetLoadedSubsystem<XRDisplaySubsystem>() != null)
				parts.Add("Display");
			if (loader.GetLoadedSubsystem<XRInputSubsystem>() != null)
				parts.Add("Input");
			var meshing = loader.GetLoadedSubsystem<XRMeshSubsystem>();
			if (meshing != null)
				parts.Add("Meshing");

			return parts.Count == 0 ? "aucun" : string.Join(", ", parts);
		}

		/// <summary>Ligne « clé / valeur » enregistrée sous <paramref name="id"/> pour les mises à jour.</summary>
		private Label AddRow(VisualElement parent, string id, string key) {
			var value   = AddInfo(parent, key, "—");
			_rows[id]   = value;
			return value;
		}

		private static Label AddInfo(VisualElement parent, string key, string value) {
			var row = new VisualElement();
			row.AddToClassList("flex-row");
			row.AddToClassList("align-start");
			row.AddToClassList("mb-4");

			var keyLabel = new Label(key);
			keyLabel.AddToClassList("key-label");
			keyLabel.AddToClassList("opacity-75");
			row.Add(keyLabel);

			var valueLabel = new Label(value);
			valueLabel.AddToClassList("flex-grow");
			valueLabel.AddToClassList("flex-shrink");
			valueLabel.AddToClassList("text-wrap");
			row.Add(valueLabel);

			parent.Add(row);
			return valueLabel;
		}

		private static Label AddTitle(VisualElement parent, string text) {
			var title = new Label(text);
			title.AddToClassList("text-md");
			title.AddToClassList("text-bold");
			title.AddToClassList("opacity-75");
			title.AddToClassList("mt-8");
			title.AddToClassList("mb-4");
			parent.Add(title);
			return title;
		}

		private static Button AddButton(VisualElement parent, string text, Action onClick, string tooltip = null) {
			var button = new Button(onClick) { text = text };
			button.AddToClassList("mb-4");
			button.AddToClassList("me-8");
			if (!string.IsNullOrEmpty(tooltip))
				button.tooltip = tooltip;

			parent.Add(button);
			return button;
		}

		private void SetRow(string id, string text, RowStatus status = RowStatus.Neutral) {
			if (_rows.TryGetValue(id, out var label))
				SetStatus(label, text, status);
		}

		private static void SetStatus(Label label, string text, RowStatus status) {
			if (label == null)
				return;

			label.text = text;
			SetStatus(label, status);
		}

		private static void SetStatus(Label label, RowStatus status) {
			if (label == null)
				return;

			label.EnableInClassList("text-success", status == RowStatus.Ok);
			label.EnableInClassList("text-warning", status == RowStatus.Warn);
			label.EnableInClassList("text-danger",  status == RowStatus.Bad);
		}

		#endregion
	}
}
