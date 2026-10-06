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
using Nox.XR.Runtime.Diagnostics;
using Nox.XR.Runtime.FullBody;
using Nox.XR.Runtime.Loaders;
using Nox.XR.Runtime.Panels;
using Nox.XR.Runtime.Settings;
using UnityEditor;
using UnityEditor.XR.Management;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.XR;
using UnityEngine.XR.Management;
using IPanel = Nox.Editor.Panel.IPanel;
using Logger = Nox.CCK.Utils.Logger;
using BindingSnapshot = Nox.XR.Runtime.Diagnostics.XRDiagnostics.BindingSnapshot;
using DeviceSnapshot = Nox.XR.Runtime.Diagnostics.XRDiagnostics.DeviceSnapshot;
using MatchSnapshot = Nox.XR.Runtime.Diagnostics.XRDiagnostics.MatchSnapshot;
using StateSnapshot = Nox.XR.Runtime.Diagnostics.XRDiagnostics.StateSnapshot;

namespace Nox.XR.Editor {
	/// <summary>
	/// XR inspector panel: what the XR is doing right now (loader chain, input providers, devices)
	/// and why a device that is switched on is not used (diagnosis, tracker filter verdict, full-body
	/// calibration), with the settings and actions that change it.
	///
	/// <para>
	/// Everything is read through <see cref="XRDiagnostics"/>, the same snapshots the <c>xr</c>
	/// terminal command and the <c>nox.control</c> operators return, so the three cannot drift apart.
	/// </para>
	///
	/// <para>
	/// Markup lives in <c>xr-panel.uxml</c> and only uses the nox.cck classes. The lists (devices,
	/// loaders, rig bones) are built here, and a section only builds its list while it is unfolded:
	/// a folded section costs nothing per tick.
	/// </para>
	/// </summary>
	public class XRPanel : IEditorModInitializer, IPanel {
		private static readonly string[] PanelPath = { "xr" };

		/// <summary>Editor API of the mod: also what the menu entry needs to reach the panel framework.</summary>
		internal static IEditorModCoreAPI API;

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
		/// <summary>Fast enough to follow a pose, slow enough to cost nothing.</summary>
		private const double RefreshInterval = 0.5;

		/// <summary>
		/// The loader list is re-read from the AssetDatabase and has no reason to move at 2 Hz: it is
		/// only rebuilt on a change of mods or assets (see <see cref="OnAssetsChanged"/>).
		/// </summary>
		private const double LoaderRefreshInterval = 2;

		private const string OpenXRSettingsPath = "Project/XR Plug-in Management/OpenXR";

		private enum Status { Neutral, Ok, Warn, Bad }

		private readonly XRPanel _panel;
		private readonly IWindow _window;

		private VisualElement _content;
		private VisualElement _rowsStatus;
		private VisualElement _rowsTracking;
		private VisualElement _rowsDevices;
		private VisualElement _rowsTrackers;
		private VisualElement _rowsFullBody;
		private VisualElement _rowsLoaders;
		private VisualElement _rowsSettings;
		private VisualElement _playMode;
		private VisualElement _hints;
		private VisualElement _hintsEmpty;
		private VisualElement _devices;
		private VisualElement _devicesEmpty;
		private VisualElement _trackers;
		private VisualElement _trackersEmpty;
		private VisualElement _bones;
		private VisualElement _loaders;
		private VisualElement _loadersEmpty;
		private VisualElement _settings;
		private Label _product;

		private Button _vrButton;
		private Button _reportButton;
		private Button _openXrButton;

		private readonly Dictionary<string, Label>   _rows     = new();
		private readonly Dictionary<string, Toggle> _toggles  = new();
		private readonly Dictionary<string, Foldout> _foldouts = new();

		private double _lastRefresh       = double.MinValue;
		private double _lastLoaderRefresh = double.MinValue;

		/// <summary>Last failure reported, so a broken read is logged once instead of every tick.</summary>
		private string _lastError;

		/// <summary>Guards the settings toggles: writing a value must not look like a user click.</summary>
		private bool _syncingToggles;

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
			=> new IToolOption[] {
				new DefaultToolOption("Refresh", Refresh, "Read every value again.")
			};

		public void OnDestroy() {
			_panel.Instance = null;

			XRLoaderEditorRegistry.Changed.RemoveListener(OnAssetsChanged);
			EditorApplication.projectChanged       -= OnAssetsChanged;
			EditorApplication.playModeStateChanged -= OnPlayModeChanged;

			_rows.Clear();
			_toggles.Clear();
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

			_content = XRPanel.API.AssetAPI.GetAsset<VisualTreeAsset>("xr-panel.uxml").CloneTree();
			_content.AddToClassList("flex-grow");

			_rowsStatus    = _content.Q ("rows-status");
			_rowsTracking  = _content.Q ("rows-tracking");
			_rowsDevices   = _content.Q ("rows-devices");
			_rowsTrackers  = _content.Q ("rows-trackers");
			_rowsFullBody  = _content.Q ("rows-full-body");
			_rowsLoaders   = _content.Q ("rows-loaders");
			_rowsSettings  = _content.Q ("rows-settings");
			_playMode      = _content.Q ("play-mode");
			_hints         = _content.Q ("hints");
			_hintsEmpty    = _content.Q ("no-hints");
			_devices       = _content.Q ("devices");
			_devicesEmpty  = _content.Q ("no-devices");
			_trackers      = _content.Q ("trackers");
			_trackersEmpty = _content.Q ("no-trackers");
			_bones         = _content.Q ("bones");
			_loaders       = _content.Q ("loaders");
			_loadersEmpty  = _content.Q ("no-loaders");
			_settings      = _content.Q ("settings");
			_product       = _content.Q<Label>("product");

			_vrButton      = _content.Q<Button>("vr");
			_reportButton  = _content.Q<Button>("report");
			_openXrButton  = _content.Q<Button>("openxr");

			CacheFoldouts();
			BuildRows();
			BuildToggles();
			Hook();

			Refresh();
			return _content;
		}

		private void CacheFoldouts() {
			foreach (var name in new[] { "sec-status", "sec-diagnosis", "sec-tracking", "sec-devices", "sec-trackers", "sec-loaders", "sec-settings" }) {
				var foldout = _content.Q<Foldout>(name);
				if (foldout != null)
					_foldouts[name] = foldout;
			}
		}

		/// <summary>
		/// The loaders are announced by the mods after nox.xr and their assets move when a mod is
		/// added or removed: the list is rebuilt on those events rather than on the tick.
		/// </summary>
		private void Hook() {
			XRLoaderEditorRegistry.Changed.AddListener(OnAssetsChanged);
			EditorApplication.projectChanged       += OnAssetsChanged;
			EditorApplication.playModeStateChanged += OnPlayModeChanged;

			if (_vrButton != null)
				_vrButton.clicked += OnToggleVr;

			if (_openXrButton != null)
				_openXrButton.clicked += () => SettingsService.OpenProjectSettings(OpenXRSettingsPath);

			if (_reportButton != null)
				_reportButton.clicked += CopyReport;
		}

		private void OnAssetsChanged()
			=> RefreshLoaders(force: true);

		/// <summary>
		/// Entering or leaving Play mode replaces every device: the rows of the previous session would
		/// only show stale names until the next tick.
		/// </summary>
		private void OnPlayModeChanged(PlayModeStateChange state) {
			_devices?.Clear();
			_trackers?.Clear();
			_bones?.Clear();
		}

		/// <summary>A folded section does not build its list.</summary>
		private bool IsOpen(string id)
			=> !_foldouts.TryGetValue(id, out var foldout) || foldout.value;

		private void BuildRows() {
			// --- Status -----------------------------------------------------------------
			Row(_rowsStatus, "mode",       "Mode");
			Row(_rowsStatus, "xr-setting", "XR setting");
			Row(_rowsStatus, "loader",     "Loader");
			Row(_rowsStatus, "subsystems", "Subsystems");
			Row(_rowsStatus, "client",     "Client");
			Row(_rowsStatus, "headset",    "Headset");
			Row(_rowsStatus, "platform",   "Platform");

			// --- Tracking ---------------------------------------------------------------
			Row(_rowsTracking, "provider",    "Input provider");
			Row(_rowsTracking, "fallback",    "Provider chain");
			Row(_rowsTracking, "hands",       "Hands");
			Row(_rowsTracking, "pose-head",   "Head pose");
			Row(_rowsTracking, "pose-left",   "Left hand");
			Row(_rowsTracking, "pose-right",  "Right hand");
			Row(_rowsTracking, "hand-spread", "Hand spread");
			Row(_rowsTracking, "origin",      "XR origin");
			Row(_rowsTracking, "origin-pos",  "Origin position");

			// --- Devices (a summary only: the list below holds every detail) -------------
			Row(_rowsDevices, "summary", "Summary");

			// --- Trackers & full-body ----------------------------------------------------
			Row(_rowsTrackers, "detection", "Detection");
			Row(_rowsTrackers, "used",      "Used trackers");
			Row(_rowsFullBody, "calibration", "Calibration");
			Row(_rowsFullBody, "driving",     "Driving");
			Row(_rowsFullBody, "range",       "Range");
			Row(_rowsFullBody, "scale",       "Avatar scale");
			Row(_rowsFullBody, "rig",         "Rig");

			// --- Loaders -----------------------------------------------------------------
			Row(_rowsLoaders, "providers",  "Providers");
			Row(_rowsLoaders, "compatible", "Compatible");
			Row(_rowsLoaders, "preferred",  "Preferred");
			Row(_rowsLoaders, "first",      "First tried");
			Row(_rowsLoaders, "configured", "XR Management");

			// --- Settings ----------------------------------------------------------------
			Row(_rowsSettings, "ipd",       "IPD");
			Row(_rowsSettings, "poke-pct",  "Poke threshold");
		}

		/// <summary>
		/// The settings a panel can flip are real toggles, next to the values they drive: the FBT ones
		/// in the trackers section, the XR ones in the settings section — never in both.
		/// </summary>
		private void BuildToggles() {
			AddToggle(_rowsTrackers, "fbt", "Full-body tracking", "Use hardware trackers to drive the avatar's rig.",
				() => FullBodyTrackingSetting.Value,
				value => FullBodyTrackingSetting.Value = value);

			AddToggle(_rowsTrackers, "trackers-controller", "Trackers flagged as controllers",
				"Keep devices the runtime reports with the Controller flag (a Vive tracker with a role assigned) as full-body trackers.",
				() => TrackersIncludeControllerSetting.Value,
				value => TrackersIncludeControllerSetting.Value = value);

			AddToggle(_settings, "xr", "XR enabled", "Master switch of nox.xr (same as the XR/General/Enable XR setting).",
				() => EnableXRSetting.Value,
				value => EnableXRSetting.Value = value);

			AddToggle(_settings, "poke", "Poke", "Poke interaction (fingertip pressure on a surface).",
				() => PokeSettings.Enabled,
				value => PokeSettings.Enabled = value);
		}

		#endregion

		#region Refresh

		private void Refresh() {
			if (_content == null)
				return;

			// The panel is driven by the mod update tick: an exception here would break the whole mod
			// update (and spam the console), so a failure is reported once and the panel keeps its last
			// values instead of taking the editor down with it.
			try {
				RefreshAll();
				_lastError = null;
			} catch (Exception e) {
				if (_lastError == e.Message)
					return;

				_lastError = e.Message;
				Logger.LogError($"XR panel refresh failed: {e.Message}", tag: nameof(XRPanelInstance));
			}
		}

		private void RefreshAll() {
			var state = XRDiagnostics.State();

			RefreshStatus(state);
			RefreshDiagnosis();
			RefreshTracking();
			RefreshDevices();
			RefreshTrackers(state);
			RefreshLoaders();
			RefreshSettings();
			RefreshActions(state);

			if (_product != null)
				_product.text = $"{Application.productName} — Unity {Application.unityVersion}";
		}

		private void RefreshStatus(StateSnapshot state) {
			Set("mode", state.Playing ? "Play mode" : "Edit mode", state.Playing ? Status.Ok : Status.Neutral);
			Set("xr-setting", state.XREnabled ? "Enabled" : "Disabled", state.XREnabled ? Status.Ok : Status.Warn);

			var loader = state.ActiveLoader;
			Set("loader",
				!state.LoaderRunning
					? "Not running"
					: loader == null
						? "Running — no loader identified"
						: $"{loader}{(state.PreferredLoader != null && state.PreferredLoader != loader ? $" (preferred: {state.PreferredLoader})" : "")}",
				state.LoaderRunning && loader != null ? Status.Ok : Status.Bad);

			var subsystems = Subsystems();
			Set("subsystems", subsystems, subsystems.StartsWith("none", StringComparison.Ordinal) ? Status.Bad : Status.Ok);
			Set("client",
				Client.Instance == null
					? "Not instantiated"
					: $"Instantiated — running: {(Client.Instance.IsRunning ? "yes" : "no")}, ready: {(Client.Instance.IsReady() ? "yes" : "no")}",
				Client.Instance == null ? Status.Warn : Client.Instance.IsRunning ? Status.Ok : Status.Neutral);

			// XRInputs.HasHeadset goes through the input provider, not InputDevices: naming the source
			// avoids concluding that the headset is missing when another provider answers.
			var provider = state.ActiveProvider;
			Set("headset",
				provider == null
					? "No XR input provider"
					: state.HasHeadset
						? $"Tracked ({provider})"
						: $"Not tracked ({provider})",
				provider == null ? Status.Warn : state.HasHeadset ? Status.Ok : Status.Bad);

			Set("platform", state.Platform);

			// Device data only exists in Play mode: say it once, in the section it affects.
			_playMode?.EnableInClassList("hidden", state.Playing);
		}

		private void RefreshDiagnosis() {
			var hints = XRDiagnostics.Hints()
				.Where(hint => !hint.StartsWith("Not in play mode", StringComparison.Ordinal))
				.ToArray();

			_hints?.Clear();
			_hintsEmpty?.EnableInClassList("hidden", hints.Length > 0);

			if (_hints == null)
				return;

			foreach (var hint in hints) {
				// The filter lists one line per rejected device: indenting them keeps the reason
				// visibly attached to the device above it.
				var indented = hint.StartsWith("  - ", StringComparison.Ordinal);
				var label = new Label(indented ? hint.TrimStart() : "• " + hint);
				label.AddToClassList("text-wrap");
				label.AddToClassList("mb-4");
				if (indented)
					label.AddToClassList("ms-16");

				_hints.Add(label);
			}
		}

		private void RefreshTracking() {
			Set("provider", XRInputs.ActiveProvider == null ? "None" : XRInputs.ActiveProvider.GetType().Name,
				XRInputs.ActiveProvider != null ? Status.Ok : Status.Bad);

			Set("fallback", $"override: {Name(XRInputs.Provider)} • fallback: {Name(XRInputs.DefaultProvider)}");

			Set("hands",
				$"left: {(XRInputs.HasHandLeft ? "tracked" : "missing")} • right: {(XRInputs.HasHandRight ? "tracked" : "missing")}",
				XRInputs.HasHandLeft && XRInputs.HasHandRight ? Status.Ok : Status.Warn);

			SetPose("pose-head", XRInputs.GetHeadsetPose(out var headPosition, out var headRotation), headPosition, headRotation);
			SetPose("pose-left", XRInputs.GetLeftHandPose(out var leftPosition, out var leftRotation), leftPosition, leftRotation);
			SetPose("pose-right", XRInputs.GetRightHandPose(out var rightPosition, out var rightRotation), rightPosition, rightRotation);

			var spread = XRHandTracking.TryGetHandDistance(out var distance);
			Set("hand-spread", spread ? $"{distance * 100f:0.0} cm" : "Not available", spread ? Status.Ok : Status.Neutral);

			// L'espace de suivi est l'objet dont la caméra XR est l'enfant : c'est lui (et pas la racine du
			// rig) qui sert de repère aux poses des devices, donc celui à afficher.
			var origin        = XROriginSetter.GlobalOrigin;
			var trackingSpace = FullBodyTrackers.TrackingSpace();
			Set("origin", origin != null ? origin.gameObject.name : "None", origin != null ? Status.Ok : Status.Bad);
			Set("origin-pos", trackingSpace ? $"{trackingSpace.name} {Format(trackingSpace.position)}" : "—");
		}

		private void RefreshDevices() {
			var devices = XRDiagnostics.Devices();
			var used    = devices.Count(device => device.TrackerCandidate);
			var hmd     = devices.Count(device => device.Characteristics?.Contains("HeadMounted") == true);

			// In edit mode Unity reports no device at all: that is not a problem, only a missing session.
			Set("summary",
				$"{devices.Count} device(s) — {hmd} head-mounted, {used} used as tracker(s)",
				devices.Count > 0 ? used > 0 ? Status.Ok : Status.Neutral : Application.isPlaying ? Status.Warn : Status.Neutral);

			if (_devices == null || !IsOpen("sec-devices"))
				return;

			_devicesEmpty?.EnableInClassList("hidden", devices.Count > 0);
			_devices.Clear();

			// Used trackers first, then the ones the runtime says are tracked, then by name: what
			// matters is at the top of a list that can hold a dozen tracker slots. Identical rows are
			// merged (the runtime declares one slot per role, all with the same placeholder pose,
			// which would otherwise be a dozen identical lines).
			foreach (var group in devices
						.GroupBy(device => $"{device.Name}|{device.Characteristics}|{device.IsValid}|{device.IsTracked}|{device.TrackerCandidate}|{device.TrackerRejection}")
						.Select(group => new { Device = group.First(), Count = group.Count() })
						.OrderByDescending(entry => entry.Device.TrackerCandidate)
						.ThenByDescending(entry => entry.Device.IsTracked == true)
						.ThenBy(entry => entry.Device.Name, StringComparer.OrdinalIgnoreCase)) {
				_devices.Add(DeviceRow(group.Device, group.Count));
			}
		}

		private void RefreshTrackers(StateSnapshot state) {
			Set("detection",
				state.HardwareTrackerNodeCount > 0
					? $"{state.HardwareTrackerNodeCount} device(s) on XRNode.HardwareTracker"
					: "No device on XRNode.HardwareTracker",
				state.HardwareTrackerNodeCount > 0 ? Status.Ok : Status.Neutral);

			var used = XRDiagnostics.Trackers();
			Set("used", used.Count > 0 ? used.Count.ToString() : "None", used.Count > 0 ? Status.Ok : Status.Neutral);

			if (!IsOpen("sec-trackers"))
				return;

			_trackersEmpty?.EnableInClassList("hidden", used.Count > 0);
			_trackers.Clear();

			foreach (var tracker in used)
				_trackers.Add(DeviceRow(tracker));

			RefreshFullBody();
		}

		private void RefreshFullBody() {
			var snapshot = XRDiagnostics.FullBody(withRig: true);

			Set("calibration",
				!snapshot.Available
					? "Component not present (no XR proxy?)"
					: snapshot.Calibrating
						? "Running…"
						: snapshot.HasCalibration ? "Stored" : "None",
				!snapshot.Available ? Status.Warn : snapshot.Calibrating ? Status.Warn : snapshot.HasCalibration ? Status.Ok : Status.Neutral);

			Set("driving", snapshot.Driving ? "Yes" : "No", snapshot.Driving ? Status.Ok : Status.Neutral);
			Set("range", $"{snapshot.Range:0.###} m");
			Set("scale", snapshot.AvatarScale.ToString("0.###"));
			Set("rig", snapshot.RigBackend ?? "None", snapshot.RigBackend != null ? Status.Ok : Status.Neutral);

			if (_bones == null)
				return;

			_bones.Clear();

			// Stored bindings first (the calibration that is replayed), then the live matches while a
			// calibration runs — one list, because they answer the same question at different times.
			foreach (var binding in snapshot.Bindings ?? Array.Empty<BindingSnapshot>()) {
				var rigBone = snapshot.RigBones?.FirstOrDefault(bone => bone.Bone == binding.Bone);
				_bones.Add(BoneRow(
					binding.Bone,
					$"← {binding.Tracker} • offset {binding.OffsetPosition} • scale {binding.Scale:0.###}"
					+ (rigBone?.Path != null ? $" • {rigBone.Path}" : rigBone == null ? " • not on the rig" : ""),
					rigBone?.Active == true ? "driven" : "stored",
					rigBone?.Active == true ? "success" : "muted"
				));
			}

			foreach (var match in snapshot.Matches ?? Array.Empty<MatchSnapshot>()) {
				_bones.Add(BoneRow(
					match.Bone,
					$"← {match.Tracker} • {match.Distance:0.###} m",
					"live",
					match.Distance <= snapshot.Range ? "info" : "warning"
				));
			}

			if (_bones.childCount == 0) {
				// No stored calibration yet: list the bones the calibration is looking for instead.
				var bones = snapshot.Bones ?? Array.Empty<string>();
				var label = new Label(bones.Length == 0
					? "No bone is tracked by the calibration."
					: "No stored calibration — bones: " + string.Join(", ", bones));
				label.AddToClassList("subtitle");
				_bones.Add(label);
			}
		}

		/// <summary>
		/// The loader list is the order nox.xr will try: the providers the mods announce, completed by
		/// the ones <see cref="XRLoaderManager"/> already met, deduplicated by id and sorted by
		/// preference then priority.
		/// </summary>
		private void RefreshLoaders(bool force = false) {
			if (_content == null)
				return;

			if (!force && EditorApplication.timeSinceStartup - _lastLoaderRefresh < LoaderRefreshInterval)
				return;

			_lastLoaderRefresh = EditorApplication.timeSinceStartup;

			var platform   = PlatformExtensions.CurrentPlatform;
			var loaders    = LoaderEntries();
			var compatible = loaders.Where(loader => loader.Supported).ToList();
			var preferred  = XRLoaderPreference.Preferred;

			Set("providers", $"{loaders.Count} known", loaders.Count > 0 ? Status.Neutral : Status.Warn);
			Set("compatible",
				compatible.Count == 0 ? $"None for {platform}" : string.Join(", ", compatible.Select(loader => loader.Id)),
				compatible.Count == 0 ? Status.Warn : Status.Ok);
			Set("preferred", preferred ?? "Automatic — loader priority", preferred == null ? Status.Neutral : Status.Ok);

			// nox.xr tries the loaders by descending priority and keeps the first that starts: the
			// first usable one is the one that will be picked.
			var first = loaders.FirstOrDefault(loader => loader.Usable);
			Set("first",
				first != null
					? first.Id
					: loaders.Count == 0
						? "No loader registered"
						: "None usable — XR Management fallback",
				first != null ? Status.Ok : Status.Warn);

			var configured = ConfiguredLoaders();

			Set("configured",
				configured.Count > 0
					? $"{configured.Count} for the active target: " + string.Join(", ", configured.Select(loader => loader.name))
					: "None for the active target",
				configured.Count > 0 ? Status.Ok : Status.Warn);

			if (_loaders == null || !IsOpen("sec-loaders"))
				return;

			_loadersEmpty?.EnableInClassList("hidden", loaders.Count > 0);
			_loaders.Clear();

			foreach (var loader in loaders)
				_loaders.Add(LoaderRow(loader));
		}

		private void RefreshSettings() {
			Set("ipd", $"{IPDSetting.Value * 100f:0.00} cm");
			Set("poke-pct", $"{PokeSettings.DisablePokePercent * 100f:0} %");

			// Values are re-read every tick: a setting changed in the XR settings window must show up
			// here without the user having to touch anything.
			_syncingToggles = true;
			try {
				Synchronize("fbt", FullBodyTrackingSetting.Value);
				Synchronize("trackers-controller", TrackersIncludeControllerSetting.Value);
				Synchronize("xr", EnableXRSetting.Value);
				Synchronize("poke", PokeSettings.Enabled);
			} finally {
				_syncingToggles = false;
			}
		}

		private void RefreshActions(StateSnapshot state) {
			if (_vrButton != null) {
				_vrButton.text = state.LoaderRunning ? "Stop VR" : "Start VR";
				_vrButton.SetEnabled(Client.Instance != null);
			}
		}

		#endregion

		#region Rows

		private Label Row(VisualElement parent, string id, string key) {
			var row = new VisualElement();
			row.AddToClassList("flex-row");
			row.AddToClassList("align-center");
			row.AddToClassList("mb-4");

			var label = new Label(key);
			label.style.minWidth = 92f;
			label.style.flexShrink = 0f;
			label.style.opacity = 0.75f;
			row.Add(label);

			var value = new Label("—");
			value.AddToClassList("flex-grow");
			value.AddToClassList("flex-shrink");
			value.AddToClassList("flex-basis-0");
			value.AddToClassList("text-wrap");
			row.Add(value);

			parent.Add(row);
			_rows[id] = value;
			return value;
		}

		private void AddToggle(VisualElement parent, string id, string label, string tooltip, Func<bool> get, Action<bool> set) {			
            var row = new VisualElement();
			row.AddToClassList("flex-row");
			row.AddToClassList("align-center");
			row.AddToClassList("mb-4");

			var name = new Label(label);
			name.style.minWidth = 92f;
			name.style.flexShrink = 0f;
			name.style.opacity = 0.75f;
			row.Add(name);

			var toggle = new Toggle { tooltip = tooltip };
			toggle.AddToClassList("large");
			toggle.RegisterValueChangedCallback(evt => {
				if (_syncingToggles)
					return;

				set(evt.newValue);
				Refresh();
			});

			row.Add(toggle);
			parent.Add(row);
			_toggles[id] = toggle;
		}

		/// <summary>One device row, or one row per group of identical devices (see the caller).</summary>
		private VisualElement DeviceRow(DeviceSnapshot device, int count = 1) {
			var row = new VisualElement();
			row.AddToClassList("flex-row");
			row.AddToClassList("align-center");
			row.AddToClassList("pt-4");
			row.AddToClassList("pb-4");
			row.style.borderBottomWidth = 1f;
			row.style.borderBottomColor = new Color(0.18f, 0.18f, 0.18f);

			var dot = new VisualElement();
			dot.AddToClassList("status-dot");
			dot.AddToClassList(device.TrackerCandidate ? "is-on" : device.IsTracked == true ? "is-off" : "is-idle");
			row.Add(dot);

			var column = new VisualElement();
			column.AddToClassList("flex-grow");
			column.AddToClassList("flex-shrink");
			column.AddToClassList("flex-basis-0");
			row.Add(column);

			var head = new VisualElement();
			head.AddToClassList("flex-row");
			head.AddToClassList("align-center");
			column.Add(head);

			var name = new Label(string.IsNullOrEmpty(device.Name) ? "(unnamed)" : device.Name);
			name.AddToClassList("text-md");
			name.AddToClassList("text-ellipsis");
			name.style.flexShrink = 1f;
			name.tooltip = device.Name;
			head.Add(name);

			if (count > 1)
				head.Add(Badge($"×{count}", "muted"));

			head.Add(Badge(DeviceKind(device), "info"));

			if (device.IsValid == false)
				head.Add(Badge("invalid", "danger"));
			else if (device.IsTracked == true)
				head.Add(Badge("tracked", "success"));
			else
				head.Add(Badge("not tracked", "muted"));

			var details = new Label(Join(device.Manufacturer, device.Serial, device.Characteristics));
			details.AddToClassList("subtitle");
			column.Add(details);

			if (device.IsTracked == true && device.HasPosition) {
				var pose = new Label($"pos {device.Position} • rot {device.Rotation}°");
				pose.AddToClassList("subtitle");
				column.Add(pose);
			}

			// A slot the runtime declared but never fills is not rejected by the tracker filter: it is
			// simply empty, and the reason is on the runtime side (no role assigned to the tracker).
			Label verdict;
			if (device.TrackerCandidate)
				verdict = Badge("used", "success");
			else if (device.IsTracked == true)
				verdict = Badge("rejected", "warning");
			else if (DeviceKind(device) == "tracker")
				verdict = Badge("empty slot", "muted");
			else
				verdict = Badge("idle", "muted");

			verdict.tooltip = device.TrackerRejection ?? (DeviceKind(device) == "tracker"
				? "The runtime declared this tracker slot but nothing is bound to it: a tracker needs one of the roles of the HTC Vive tracker OpenXR profile (assign it in the tracker settings of the runtime)."
				: null);
			row.Add(verdict);

			return row;
		}

		private VisualElement BoneRow(string bone, string details, string badge, string kind) {
			var row = new VisualElement();
			row.AddToClassList("flex-row");
			row.AddToClassList("align-center");

			var name = new Label(bone);
			name.AddToClassList("text-md");
			name.AddToClassList("text-ellipsis");
			name.style.flexGrow = 1f;
			name.style.flexShrink = 1f;
			name.style.flexBasis = 0f;
			row.Add(name);

			if (!string.IsNullOrEmpty(details)) {
				var subtitle = new Label(details);
				subtitle.AddToClassList("subtitle");
				subtitle.AddToClassList("text-ellipsis");
				subtitle.style.flexGrow = 1f;
				subtitle.style.flexShrink = 1f;
				subtitle.style.flexBasis = 0f;
				row.Add(subtitle);
			}

			row.Add(Badge(badge, kind));
			return row;
		}

		/// <summary>One loader: state badges on the left, the "use it" action on the right.</summary>
		private VisualElement LoaderRow(LoaderEntry loader) {
			var row = new VisualElement();
			row.AddToClassList("flex-row");
			row.AddToClassList("align-center");
			row.AddToClassList("pt-4");
			row.AddToClassList("pb-4");
			row.style.borderBottomWidth = 1f;
			row.style.borderBottomColor = new Color(0.18f, 0.18f, 0.18f);

			var preferred = XRLoaderPreference.IsPreferred(loader.Id);

			var column = new VisualElement();
			column.AddToClassList("flex-grow");
			column.AddToClassList("flex-shrink");
			column.AddToClassList("flex-basis-0");
			row.Add(column);

			var head = new VisualElement();
			head.AddToClassList("flex-row");
			head.AddToClassList("align-center");
			column.Add(head);

			var name = new Label(loader.Id);
			name.AddToClassList("text-md");
			name.AddToClassList("text-ellipsis");
			name.style.flexShrink = 1f;
			head.Add(name);

			if (preferred)
				head.Add(Badge("preferred", "success"));

			head.Add(Badge(loader.Supported ? "compatible" : "unsupported", loader.Supported ? "info" : "muted"));
			head.Add(Badge(loader.Configured ? "configured" : "not configured", loader.Configured ? "info" : "muted"));

			if (loader.Running)
				head.Add(Badge("running", "success"));

			var details = new Label(Join($"priority {loader.Priority}", loader.Asset, loader.Compatibility));
			details.AddToClassList("subtitle");
			column.Add(details);

			if (!preferred) {
				var use = new Button(() => SwitchLoader(loader.Id)) { text = "Use" };
				use.AddToClassList("large");
				use.style.flexShrink = 0f;
				use.SetEnabled(loader.Usable && loader.Supported && loader.Asset != null);
				use.tooltip = loader.Usable
					? $"Start XR with {loader.Id} (the VR session is restarted if it is already running)."
					: $"{loader.Id} is not usable here: {Reason(loader)}.";
				row.Add(use);
			}

			return row;
		}

		private static Label Badge(string text, string kind) {
			var badge = new Label(text);
			badge.AddToClassList("badge");
			badge.AddToClassList("badge-" + kind);
			return badge;
		}

		#endregion

		#region Actions

		private void OnToggleVr()
			=> RestartVr().Forget(e => Logger.LogError($"XR toggle failed: {e.Message}"));

		private void CopyReport() {
			EditorGUIUtility.systemCopyBuffer = XRDiagnostics.Report();
			Logger.Log("XR report copied to the clipboard.", tag: nameof(XRPanelInstance));
		}

		/// <summary>
		/// Changing the preferred loader has no effect on a running session (the order is only read at
		/// startup), so the session is restarted — and only when there was one.
		/// </summary>
		private void SwitchLoader(string id) {
			XRLoaderPreference.Preferred = id;
			RestartVr().Forget(e => Logger.LogError($"XR loader switch failed: {e.Message}"));
			RefreshLoaders(force: true);
		}

		private async UniTask RestartVr() {
			if (Client.Instance == null)
				return;

			if (XRLoaderManager.IsRunning) {
				await Client.Instance.Quit();
				await Client.Instance.Enter();
			} else {
				await Client.Instance.Enter();
			}

			Refresh();
		}

		#endregion

		#region Helpers

		private void Set(string id, string text, Status status = Status.Neutral) {
			if (_rows.TryGetValue(id, out var label))
				Set(label, text, status);
		}

		private static void Set(Label label, string text, Status status) {
			label.text = text;
			label.EnableInClassList("text-success", status == Status.Ok);
			label.EnableInClassList("text-warning", status == Status.Warn);
			label.EnableInClassList("text-danger",  status == Status.Bad);
		}

		private void SetPose(string id, bool available, Vector3 position, Quaternion rotation) {
			if (!available) {
				Set(id, "Not available", Status.Neutral);
				return;
			}

			var euler = rotation.eulerAngles;
			Set(id, $"{Format(position)} • {euler.x:0}°, {euler.y:0}°, {euler.z:0}°", Status.Ok);
		}

		private void Synchronize(string id, bool value) {
			if (_toggles.TryGetValue(id, out var toggle) && toggle.value != value)
				toggle.value = value;
		}

		/// <summary>Subsystems really loaded by the active loader (Display/Input), from the state.</summary>
		private static string Subsystems() {
			var loader = XRManagementLoader.Active;
			if (loader == null)
				return "none — no loader loaded";

			var parts = new List<string>();
			if (loader.GetLoadedSubsystem<XRDisplaySubsystem>() != null)
				parts.Add("display");

			var input = loader.GetLoadedSubsystem<XRInputSubsystem>();
			if (input != null)
				parts.Add(input.running ? "input" : "input (stopped)");

			return parts.Count == 0 ? "none" : string.Join(", ", parts);
		}

		private static string DeviceKind(DeviceSnapshot device) {
			var characteristics = device.Characteristics ?? string.Empty;

			if (characteristics.Contains("HeadMounted"))
				return "head-mounted";
			if (characteristics.Contains("Controller"))
				return "controller";
			if (characteristics.Contains("TrackedDevice"))
				return "tracker";

			return "device";
		}

		private static string Name(IXRInputProvider provider)
			=> provider == null ? "none" : provider.GetType().Name;

		private static string Format(Vector3 value)
			=> $"{value.x:0.00}, {value.y:0.00}, {value.z:0.00}";

		private static string Join(params string[] parts)
			=> string.Join(" • ", parts.Where(part => !string.IsNullOrEmpty(part)));

		private static string Reason(LoaderEntry loader)
			=> !loader.Supported     ? "not supported on this platform"
			 : loader.Asset == null ? "loader asset not found"
			 : !loader.Configured   ? "absent from XR Plug-in Management"
			 : "loader unavailable";

		/// <summary>A loader that could be used here, with everything worth saying about it.</summary>
		private sealed class LoaderEntry {
			public string Id            { get; }
			public int    Priority      { get; }
			public string Compatibility { get; }
			/// <summary>Declared for the current platform by the loader's editor provider.</summary>
			public bool   Supported     { get; }
			public string Asset         { get; }
			/// <summary>Present in the loader list of the build target.</summary>
			public bool   Configured    { get; }
			/// <summary>Could start right now (<c>IsValid</c>).</summary>
			public bool   Usable        { get; }
			/// <summary>Is the loader currently loaded.</summary>
			public bool   Running       { get; }

			public LoaderEntry(string id, int priority, string compatibility, bool supported, string asset, bool configured, bool usable, bool running) {
				Id            = id;
				Priority      = priority;
				Compatibility = compatibility;
				Supported     = supported;
				Asset         = asset;
				Configured    = configured;
				Usable        = usable;
				Running       = running;
			}
		}

		/// <summary>
		/// Editor providers first: they are registered as soon as the mods load, while
		/// <see cref="XRLoaderManager.Providers"/> only knows the loaders a start already met. The
		/// nox.xr fallback comes last (priority 0).
		/// </summary>
		private static List<LoaderEntry> LoaderEntries() {
			var platform = PlatformExtensions.CurrentPlatform;
			var editors  = XRLoaderEditorRegistry.Registered;
			var entries  = new List<LoaderEntry>();
			var seen     = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			void Add(IXRLoaderProvider provider, IXRLoaderEditorProvider editor) {
				if (provider == null || !seen.Add(provider.Id))
					return;

				// Without an editor provider it is the fallback: it has no loader of its own and
				// starts whatever the target has configured, so it is compatible by construction.
				var asset     = editor?.Loader;
				var supported = editor == null || editor.IsSupported(platform);

				entries.Add(new LoaderEntry(
					provider.Id,
					provider.Priority,
					editor == null
						? "Fallback — first configured loader"
						: supported ? $"compatible with {platform}" : $"not supported on {platform}",
					supported,
					asset?.name,
					asset != null ? IsConfigured(asset) : ConfiguredLoaders().Count > 0,
					provider.IsValid,
					provider == XRLoaderManager.Current));
			}

			foreach (var editor in editors)
				Add(editor, editor);

			foreach (var provider in XRLoaderManager.Providers)
				Add(provider, editors.FirstOrDefault(editor => editor.Id == provider.Id));

			return entries
				.OrderByDescending(entry => XRLoaderPreference.IsPreferred(entry.Id))
				.ThenByDescending(entry => entry.Priority)
				.ToList();
		}

		/// <summary>Loaders configured in XR Plug-in Management for the current build target.</summary>
		private static IReadOnlyList<XRLoader> ConfiguredLoaders() {
			var group    = BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget);
			var settings = group == BuildTargetGroup.Unknown ? null : XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(group);
			var loaders  = settings?.Manager?.activeLoaders;

			return loaders == null || loaders.Count == 0
				? Array.Empty<XRLoader>()
				: loaders.Where(loader => loader != null).ToList();
		}

		/// <summary>
		/// The asset found by the editor provider is compared by <b>type</b>: a project can hold two
		/// assets of the same loader (the one XR Plug-in Management created and the one a mod ships)
		/// and only the type equality matters, exactly like in <c>XRSettingsSetup</c>.
		/// </summary>
		private static bool IsConfigured(XRLoader asset)
			=> asset != null && ConfiguredLoaders().Any(loader => loader.GetType() == asset.GetType());

		#endregion
	}

	/// <summary>
	/// "Nox/XR/XR Panel…": opens the panel in the Nox panel window. The panel framework itself
	/// (nox.editor.panel) is an optional mod, hence the null checks.
	/// <para>
	/// Priority stays in the diagnostics band of the "Nox/XR" sub-menu: after "Open XR Settings"
	/// (991) and well before the actions (1007), which keeps the separator between the two groups.
	/// </para>
	/// </summary>
	public static class XRPanelMenu {
		private const int Priority = 992;

		[MenuItem("Nox/XR/XR Panel…", false, Priority)]
		private static void Open() {
			var panels = XRPanel.API?.ModAPI?.GetMod("editor.panel")?.GetInstance<IPanelAPI>();
			if (panels == null) {
				Logger.LogWarning("The Nox panel framework (nox.editor.panel) is not available.", tag: nameof(XRPanelMenu));
				return;
			}

			if (!panels.TryGetPanel(new ResourceIdentifier("nox.xr", new[] { "xr" }), out var panel)) {
				Logger.LogWarning("The XR panel is not registered.", tag: nameof(XRPanelMenu));
				return;
			}

			panels.TryOpen(panel);
		}
	}
}
