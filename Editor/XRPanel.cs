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
	/// Panel d'inspection XR : état du loader, provider d'input, devices, trackers, loaders connus
	/// et leur compatibilité, configuration XR Plug-in Management et réglages, plus quelques
	/// actions (démarrer/arrêter la VR, poke, FBT).
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

		/// <summary>
		/// Cadence de la section « Loaders ». <c>IXRLoaderEditorProvider.Loader</c> relit
		/// l'AssetDatabase à chaque accès, et la liste n'a aucune raison de bouger à 2 Hz : elle
		/// n'est réécrite que sur changement de mods ou d'assets.
		/// </summary>
		private const double LoaderRefreshInterval = 2;

		private readonly XRPanel _panel;
		private readonly IWindow _window;

		private VisualElement _content;
		private VisualElement _devicesList;
		private VisualElement _trackersList;
		private VisualElement _loadersList;
		private VisualElement _loaderActions;
		private VisualElement _managementList;
		private VisualElement _actions;
		private Label _noDevicesLabel;

		private Button _vrButton;
		private Button _pokeButton;
		private Button _fbtButton;

		private readonly Dictionary<string, Label>   _rows     = new();
		private readonly Dictionary<string, Foldout> _foldouts = new();

		// Les replis sont créés une fois puis réécrits : les recréer à chaque tick refermerait ceux
		// que l'utilisateur a dépliés et ferait perdre le défilement dans les longues listes.
		// `InputDevice` sert de clé : sa valeur implémente `IEquatable` sur son device id, qui
		// n'est pas public mais qui est la seule identity stable d'un device entre deux ticks.
		private readonly Dictionary<string, InfoFoldout>    _loaderFoldouts  = new();
		private readonly Dictionary<InputDevice, InfoFoldout> _deviceFoldouts  = new();
		private readonly Dictionary<InputDevice, InfoFoldout> _trackerFoldouts = new();

		// Listes réutilisées : le panel tourne en continu, inutile d'allouer à chaque tick.
		private readonly List<InputDevice> _deviceSample  = new();
		private readonly List<InputDevice> _trackerSample = new();

		private double _lastRefresh       = double.MinValue;
		private double _lastLoaderRefresh = double.MinValue;

		/// <summary>État des boutons de switch, pour ne les reconstruire que lorsqu'ils changent.</summary>
		private string _loaderActionsSignature;

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

			XRLoaderEditorRegistry.Changed.RemoveListener(OnLoadersChanged);
			EditorApplication.projectChanged       -= OnLoadersChanged;
			EditorApplication.playModeStateChanged -= OnPlayModeChanged;

			_rows.Clear();
			_foldouts.Clear();
			_loaderFoldouts.Clear();
			_deviceFoldouts.Clear();
			_trackerFoldouts.Clear();
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
			_loaderActions  = _content.Q<VisualElement>("loader-actions");
			_managementList = _content.Q<VisualElement>("management-list");
			_actions        = _content.Q<VisualElement>("action-buttons");
			_noDevicesLabel = _content.Q<Label>("no-devices");

			CacheFoldouts();
			BuildRows();
			BuildActions();
			Hook();

			Refresh();
			return _content;
		}

		private void CacheFoldouts() {
			foreach (var name in new[] { "sec-status", "sec-loaders", "sec-input", "sec-devices", "sec-trackers", "sec-management", "sec-settings", "sec-raw" }) {
				var foldout = _content.Q<Foldout>(name);
				if (foldout != null)
					_foldouts[name] = foldout;
			}
		}

		/// <summary>
		/// Les loaders sont annoncés par les mods après nox.xr, et leurs assets bougent quand un
		/// mod est ajouté ou retiré : on réécrit la liste sur ces événements plutôt que de parier
		/// sur le tick, qui ne sert qu'à lire des valeurs.
		/// </summary>
		private void Hook() {
			XRLoaderEditorRegistry.Changed.AddListener(OnLoadersChanged);
			EditorApplication.projectChanged       += OnLoadersChanged;
			EditorApplication.playModeStateChanged += OnPlayModeChanged;
		}

		private void OnLoadersChanged()
			=> RefreshLoaders(force: true);

		/// <summary>En Play, les devices et trackers Unity changent complètement : on repart de zéro.</summary>
		private void OnPlayModeChanged(PlayModeStateChange state) {
			_deviceFoldouts.Clear();
			_trackerFoldouts.Clear();
			_devicesList?.Clear();
			_trackersList?.Clear();
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

			// --- Loaders ----------------------------------------------------------------
			var loaders = _content.Q<VisualElement>("rows-loaders");
			AddRow(loaders, "ldr-providers",  "Providers");
			AddRow(loaders, "ldr-compatible", "Compatibles");
			AddRow(loaders, "ldr-preference", "Préférence");
			AddRow(loaders, "ldr-first",      "Premier essai");

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
			RefreshLoaders();
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

		/// <summary>
		/// Les loaders connus, du plus prioritaire au moins prioritaire : c'est l'ordre dans lequel
		/// nox.xr les essaie, et celui que <c>XRSettingsSetup</c> déclare dans XR Plug-in Management.
		/// </summary>
		private void RefreshLoaders(bool force = false) {
			if (_content == null)
				return;

			if (!force && EditorApplication.timeSinceStartup - _lastLoaderRefresh < LoaderRefreshInterval)
				return;

			_lastLoaderRefresh = EditorApplication.timeSinceStartup;

			var platform   = PlatformExtensions.CurrentPlatform;
			var loaders    = LoaderEntries();
			var compatible = loaders.Where(l => l.Supported).ToList();

			SetRow("ldr-providers", $"{loaders.Count} connu(s)", RowStatus.Neutral);
			SetRow("ldr-compatible", compatible.Count == 0
				? $"Aucun pour {platform}"
				: string.Join(", ", compatible.Select(l => l.Id)),
				compatible.Count == 0 ? RowStatus.Warn : RowStatus.Ok);

			var preferred = XRLoaderPreference.Preferred;
			SetRow("ldr-preference",
				preferred == null ? "Automatique — priorité des loaders" : preferred,
				preferred == null ? RowStatus.Neutral : RowStatus.Ok);

			// nox.xr essaie les loaders par priorité décroissante et s'arrête au premier qui
			// démarre : le premier « utilisable » est donc celui qui sera retenu.
			var first = loaders.FirstOrDefault(l => l.Usable);
			SetRow("ldr-first",
				first != null
					? first.Id
					: loaders.Count == 0
						? "Aucun loader enregistré"
						: "Aucun utilisable — repli XR Management",
				first != null ? RowStatus.Ok : RowStatus.Warn);

			if (_loadersList == null || !IsOpen("sec-loaders"))
				return;

			SyncFoldouts(_loadersList, loaders, _loaderFoldouts, l => l.Id, BuildLoaderFoldout, UpdateLoaderFoldout);
			SyncLoaderActions(loaders);
		}

		/// <summary>
		/// Un loader par provider, dédupliqué par Id et trié par priorité décroissante.
		///
		/// <para>
		/// Les providers éditeur d'abord : ils sont enregistrés dès le chargement des mods, alors
		/// que <see cref="XRLoaderManager.Providers"/> ne connaît que les loaders qu'un démarrage
		/// de XR a déjà rencontrés. Le repli de nox.xr passe par là aussi, en dernier (priorité 0).
		/// </para>
		/// </summary>
		private static List<LoaderEntry> LoaderEntries() {
			var platform = PlatformExtensions.CurrentPlatform;
			var editors  = XRLoaderEditorRegistry.Registered;
			var entries  = new List<LoaderEntry>();
			var seen     = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			void Add(IXRLoaderProvider provider, IXRLoaderEditorProvider editor) {
				if (provider == null || !seen.Add(provider.Id))
					return;

				// Sans provider éditeur, c'est le repli : il n'a pas son propre loader mais démarre
				// ce que la cible a de configuré — donc compatible par construction.
				var asset     = editor?.Loader;
				var supported = editor == null || editor.IsSupported(platform);

				entries.Add(new LoaderEntry(
					provider.Id,
					provider.Priority,
					editor == null
						? "Repli — premier loader configuré"
						: supported ? $"Compatible — {platform}" : $"Non supporté — {platform}",
					supported,
					asset?.name,
					asset != null ? IsConfigured(asset) : ConfiguredLoaders().Count > 0,
					provider.IsValid,
					provider == XRLoaderManager.Current));
			}

			foreach (var editor in editors)
				Add(editor, editor);

			foreach (var provider in XRLoaderManager.Providers)
				Add(provider, editors.FirstOrDefault(e => e.Id == provider.Id));

			// Le loader choisi passe devant, exactement comme dans `XRLoaderManager.Providers` : le
			// panel annonce ainsi ce que nox.xr va réellement essayer.
			return entries
				.OrderByDescending(e => XRLoaderPreference.IsPreferred(e.Id))
				.ThenByDescending(e => e.Priority)
				.ToList();
		}

		private static string LoaderTitle(LoaderEntry loader)
			=> $"{loader.Id} — priorité {loader.Priority}"
			   + (XRLoaderPreference.IsPreferred(loader.Id) ? "  ◀ choisi" : "");

		private static InfoFoldout BuildLoaderFoldout(LoaderEntry loader)
			=> new(LoaderTitle(loader),
				Fields.Compatibility, Fields.Asset, Fields.Usable, Fields.Configured, Fields.Running);

		private static void UpdateLoaderFoldout(InfoFoldout foldout, LoaderEntry loader) {
			foldout.SetTitle(LoaderTitle(loader));
			foldout.Set(Fields.Compatibility, loader.Compatibility, loader.Supported ? RowStatus.Ok : RowStatus.Neutral);
			foldout.Set(Fields.Asset, loader.Asset ?? "introuvable", loader.Asset == null ? RowStatus.Warn : RowStatus.Neutral);
			foldout.Set(Fields.Usable, loader.Usable ? "Oui" : "Non", Status(loader.Usable));
			foldout.Set(Fields.Configured, loader.Configured ? "Oui (dans la cible)" : "Non", Status(loader.Configured));
			foldout.Set(Fields.Running, loader.Running ? "Oui (loader chargé)" : "Non", Status(loader.Running));
		}

		/// <summary>
		/// Boutons de sélection : un par loader déclarable ici, plus le retour au choix automatique.
		///
		/// <para>
		/// Aucun bouton ne touche à XR Plug-in Management : le choix ne fait que changer l'ordre
		/// d'essai de nox.xr (<see cref="XRLoaderPreference"/>). Rien à défaire si le loader ne
		/// démarre pas — la chaîne de repli reste intacte.
		/// </para>
		/// </summary>
		private void SyncLoaderActions(List<LoaderEntry> loaders) {
			// Le repli `xr-management` n'a pas d'asset : ce n'est pas un choix, c'est l'absence de choix.
			var candidates = loaders.Where(l => l.Supported && l.Asset != null).ToList();

			var signature = string.Join("|", candidates.Select(l => $"{l.Id}/{l.Usable}"))
							+ "||" + XRLoaderPreference.Preferred;
			if (signature == _loaderActionsSignature)
				return;

			_loaderActionsSignature = signature;
			_loaderActions.Clear();

			foreach (var loader in candidates) {
				var preferred = XRLoaderPreference.IsPreferred(loader.Id);
				var button = AddButton(_loaderActions,
					preferred ? $"{loader.Id} ✓" : loader.Id,
					() => SwitchLoader(loader.Id),
					preferred
						? $"{loader.Id} est le loader choisi."
						: loader.Usable
							? $"Démarre la XR avec {loader.Id}. Relance la VR si elle tourne déjà."
							: $"{loader.Id} n'est pas utilisable ici : {Reason(loader)}.");

				button.SetEnabled(!preferred);
			}

			var automatic = AddButton(_loaderActions, "Automatique",
				() => SwitchLoader(null),
				"Laisse la priorité des loaders décider (OpenXR avant OpenVR).");
			automatic.SetEnabled(XRLoaderPreference.Preferred != null);
		}

		private static string Reason(LoaderEntry loader)
			=> !loader.Supported     ? "non supporté sur cette plateforme"
			 : loader.Asset == null ? "asset de loader introuvable"
			 : !loader.Configured   ? "absent de XR Plug-in Management"
			 : "loader indisponible";

		/// <summary>
		/// Change le loader préféré puis relance la XR.
		///
		/// <para>
		/// L'ordre des providers n'est lu qu'au démarrage de la XR
		/// (<see cref="XRLoaderManager.Start"/>), donc en changer en cours de session n'a aucun
		/// effet visible ; et se contenter d'un arrêt laisserait la VR coupée. On enchaîne donc les
		/// deux, et seulement s'il y avait une session à relancer.
		/// </para>
		/// </summary>
		private void SwitchLoader(string id) {
			XRLoaderPreference.Preferred = id;

			RestartVr().Forget(e => Logger.LogError($"XR loader switch failed: {e.Message}"));

			RefreshLoaders(force: true);
		}

		private async UniTask RestartVr() {
			if (Client.Instance == null || !XRLoaderManager.IsRunning)
				return;

			await Client.Instance.Quit();
			await Client.Instance.Enter();
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
			InputDevices.GetDevices(_deviceSample);

			var hmd = new List<InputDevice>();
			InputDevices.GetDevicesWithCharacteristics(InputDeviceCharacteristics.HeadMounted, hmd);

			var left  = DevicesWith(InputDeviceCharacteristics.Left | InputDeviceCharacteristics.Controller);
			var right = DevicesWith(InputDeviceCharacteristics.Right | InputDeviceCharacteristics.Controller);

			SetRow("dev-count", _deviceSample.Count.ToString(), _deviceSample.Count > 0 ? RowStatus.Ok : RowStatus.Bad);
			SetRow("dev-hmd",   hmd.Count > 0 ? $"{hmd[0].name}" : "Non détecté",     Status(hmd.Count > 0));
			SetRow("dev-left",  left.Count > 0 ? left[0].name : "Non détecté",        Status(left.Count > 0));
			SetRow("dev-right", right.Count > 0 ? right[0].name : "Non détecté",      Status(right.Count > 0));

			if (_devicesList == null || !IsOpen("sec-devices"))
				return;

			_noDevicesLabel?.EnableInClassList("hidden", _deviceSample.Count > 0);
			SyncFoldouts(_devicesList, _deviceSample, _deviceFoldouts, d => d, BuildDeviceFoldout, UpdateDeviceFoldout);
		}

		private static InfoFoldout BuildDeviceFoldout(InputDevice device)
			=> new(DeviceTitle(device),
				Fields.Manufacturer, Fields.Serial, Fields.Valid,
				Fields.Tracked, Fields.Position, Fields.Rotation, Fields.Battery);

		private static void UpdateDeviceFoldout(InfoFoldout foldout, InputDevice device) {
			foldout.SetTitle(DeviceTitle(device));

			foldout.Set(Fields.Manufacturer, string.IsNullOrEmpty(device.manufacturer) ? "—" : device.manufacturer);
			foldout.Set(Fields.Serial, string.IsNullOrEmpty(device.serialNumber) ? "—" : device.serialNumber);
			foldout.Set(Fields.Valid, device.isValid ? "Oui" : "Non", Status(device.isValid));

			var hasTracked = device.TryGetFeatureValue(CommonUsages.isTracked, out var tracked);
			foldout.Set(Fields.Tracked, hasTracked ? (tracked ? "Oui" : "Non") : "—", hasTracked ? Status(tracked) : RowStatus.Neutral);

			// La pose n'est affichée que si les deux composantes existent : une position seule
			// ferait croire à un device qui ne s'oriente pas.
			var hasPos = device.TryGetFeatureValue(CommonUsages.devicePosition, out var pos);
			var hasRot = device.TryGetFeatureValue(CommonUsages.deviceRotation, out var rot);
			if (hasPos && hasRot) {
				foldout.Set(Fields.Position, $"{pos.x:F3}, {pos.y:F3}, {pos.z:F3}");
				var euler = rot.eulerAngles;
				foldout.Set(Fields.Rotation, $"{euler.x:F1}°, {euler.y:F1}°, {euler.z:F1}°");
			} else {
				foldout.Set(Fields.Position, "—");
				foldout.Set(Fields.Rotation, "—");
			}

			var hasBattery = device.TryGetFeatureValue(CommonUsages.batteryLevel, out var battery);
			foldout.Set(Fields.Battery, hasBattery ? $"{battery * 100f:F0} %" : "—");
		}

		private void RefreshTrackers() {
			SetRow("fbt-setting", FullBodyTrackingSetting.Value ? "Activé" : "Désactivé", Status(FullBodyTrackingSetting.Value));
			SetRow("fbt-provider",
				XRInputs.HasDevice(XRNode.HardwareTracker) ? "Tracker vu par le provider" : "Aucun tracker via le provider",
				Status(XRInputs.HasDevice(XRNode.HardwareTracker)));

			Trackers(_trackerSample);
			SetRow("fbt-count", _trackerSample.Count.ToString(), _trackerSample.Count > 0 ? RowStatus.Ok : RowStatus.Neutral);
			SetRow("fbt-roles", $"{_trackerSample.Count(t => (t.characteristics & InputDeviceCharacteristics.Left) != 0)} gauche / " +
								$"{_trackerSample.Count(t => (t.characteristics & InputDeviceCharacteristics.Right) != 0)} droite",
				RowStatus.Neutral);

			if (_trackersList == null || !IsOpen("sec-trackers"))
				return;

			SyncFoldouts(_trackersList, _trackerSample, _trackerFoldouts, t => t, BuildTrackerFoldout, UpdateTrackerFoldout);
		}

		private static InfoFoldout BuildTrackerFoldout(InputDevice tracker)
			=> new(DeviceTitle(tracker), Fields.Characteristics, Fields.Valid);

		private static void UpdateTrackerFoldout(InfoFoldout foldout, InputDevice tracker) {
			foldout.SetTitle(DeviceTitle(tracker));
			foldout.Set(Fields.Characteristics, tracker.characteristics.ToString());
			foldout.Set(Fields.Valid, tracker.isValid ? "Oui" : "Non", Status(tracker.isValid));
		}

		private void RefreshManagement() {
			SetRow("platform", PlatformExtensions.CurrentPlatform.ToString(), RowStatus.Neutral);

			var manager = XRGeneralSettings.Instance?.Manager;
			SetRow("init-complete",
				manager == null ? "Aucun XRGeneralSettings" : manager.isInitializationComplete ? "Terminée" : "En cours",
				manager == null ? RowStatus.Warn : Status(manager.isInitializationComplete));

			// La cible de build, pas XRGeneralSettings.Instance : c'est elle que la build démarrera,
			// et elle peut différer de la cible courante en éditeur.
			var configured = ConfiguredLoaders();
			SetRow("configured",
				configured.Count == 0 ? "Aucun pour la cible active" : string.Join(", ", configured.Select(l => l.name)),
				configured.Count > 0 ? RowStatus.Ok : RowStatus.Warn);

			if (_managementList == null || !IsOpen("sec-management"))
				return;

			_managementList.Clear();

			if (configured.Count == 0)
				AddInfo(_managementList, "—", "Aucun loader configuré");
			else
				foreach (var loader in configured) {
					var active = loader == manager?.activeLoader;
					SetStatus(AddInfo(_managementList, loader.name, active ? "actif" : "configuré"), active ? RowStatus.Ok : RowStatus.Neutral);
				}

			var current = manager?.activeLoader;
			if (current != null)
				AddInfo(_managementList, "Sous-systèmes actifs", Describe(current));
		}

		private void RefreshSettings() {
			SetRow("set-enabled", EnableXRSetting.Value ? "Activé" : "Désactivé", Status(EnableXRSetting.Value));
			SetRow("set-ipd", $"{IPDSetting.Value * 1000f:F1} mm");
			SetRow("set-fbt", FullBodyTrackingSetting.Value ? "Activé" : "Désactivé", Status(FullBodyTrackingSetting.Value));
			SetRow("set-poke", PokeSettings.Enabled ? "Activé" : "Désactivé", Status(PokeSettings.Enabled));
			SetRow("set-poke-pct", $"{PokeSettings.DisablePokePercent * 100f:F0} %");
		}

		private void RefreshRaw() {
			InputDevices.GetDevices(_deviceSample);
			SetRow("raw-count", _deviceSample.Count.ToString());

			var subsystems = new List<XRInputSubsystem>();
			SubsystemManager.GetSubsystems(subsystems);
			SetRow("raw-xs-count", subsystems.Count.ToString());

			SetRow("raw-chars", string.Join(" | ", _deviceSample
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

		/// <summary>Libellés des lignes des replis, partagés entre leur création et leur mise à jour.</summary>
		private static class Fields {
			public const string Compatibility   = "Compatibilité";
			public const string Asset           = "Asset loader";
			public const string Usable          = "Utilisable ici";
			public const string Configured      = "Configuré";
			public const string Running         = "En cours";
			public const string Manufacturer    = "Manufacturer";
			public const string Serial          = "Serial";
			public const string Valid           = "Valid";
			public const string Tracked         = "Tracked";
			public const string Position        = "Position";
			public const string Rotation        = "Rotation";
			public const string Battery         = "Batterie";
			public const string Characteristics = "Caractéristiques";
		}

		/// <summary>Un loader connu, avec ce qu'on peut dire de lui sans rien démarrer.</summary>
		private sealed class LoaderEntry {
			public string Id             { get; }
			public int Priority          { get; }
			public string Compatibility { get; }
			/// <summary>Déclaré par XR Plug-in Management pour la cible courante.</summary>
			public bool Supported        { get; }
			public string Asset          { get; }
			/// <summary>Présent dans la liste de loaders de la cible de build.</summary>
			public bool Configured       { get; }
			/// <summary>Pourrait démarrer maintenant (<c>IsValid</c>).</summary>
			public bool Usable           { get; }
			/// <summary>Est le loader actuellement chargé.</summary>
			public bool Running          { get; }

			public LoaderEntry(string id, int priority, string compatibility, bool supported, string asset, bool configured, bool usable, bool running) {
				Id             = id;
				Priority        = priority;
				Compatibility  = compatibility;
				Supported      = supported;
				Asset          = asset;
				Configured     = configured;
				Usable         = usable;
				Running        = running;
			}
		}

		/// <summary>
		/// Repli dont les lignes sont créées une fois et réécrites ensuite. Recréer le
		/// <see cref="Foldout"/> à chaque rafraîchissement le refermerait, ce qui rendrait la
		/// navigation dans une longue liste de devices inutilisable.
		/// </summary>
		private sealed class InfoFoldout {
			private readonly Dictionary<string, Label> _labels = new();

			public InfoFoldout(string title, params string[] keys) {
				Root = new Foldout { text = title };
				Root.AddToClassList("p-8");
				Root.AddToClassList("mb-4");

				foreach (var key in keys)
					_labels[key] = AddInfo(Root, key, "—");
			}

			public Foldout Root { get; }

			public void SetTitle(string title) {
				if (Root.text != title)
					Root.text = title;
			}

			public void Set(string key, string text, RowStatus status = RowStatus.Neutral) {
				if (_labels.TryGetValue(key, out var label))
					SetStatus(label, text, status);
			}
		}

		/// <summary>
		/// Aligne <paramref name="container"/> sur <paramref name="items"/> en réutilisant les replis
		/// déjà construits : seuls ceux dont la clé n'est plus dans la liste sont retirés, les autres
		/// restent en place — donc dépliés, et avec le même défilement.
		/// </summary>
		private static void SyncFoldouts<TKey, TItem>(
				VisualElement container,
				IReadOnlyList<TItem> items,
				Dictionary<TKey, InfoFoldout> cache,
				Func<TItem, TKey> keyOf,
				Func<TItem, InfoFoldout> build,
				Action<InfoFoldout, TItem> update)
				where TKey : notnull {
			if (container == null)
				return;

			var kept  = new HashSet<TKey>();
			var index = 0;

			foreach (var item in items) {
				var key = keyOf(item);
				kept.Add(key);

				if (!cache.TryGetValue(key, out var foldout))
					cache[key] = foldout = build(item);

				// Décaler seulement ce qui n'est plus au bon endroit : un device qui disparaît en
				// tête de liste n'a pas à faire bouger tous les replis qui suivent.
				if (index >= container.childCount || container[index] != foldout.Root) {
					foldout.Root.RemoveFromHierarchy();
					container.Insert(index, foldout.Root);
				}

				update(foldout, item);
				index++;
			}

			while (container.childCount > index)
				container.RemoveAt(container.childCount - 1);

			foreach (var stale in cache.Keys.Where(k => !kept.Contains(k)).ToList())
				cache.Remove(stale);
		}

		private static RowStatus Status(bool ok)
			=> ok ? RowStatus.Ok : RowStatus.Bad;

		private static string Name(IXRInputProvider provider)
			=> provider == null ? "Aucun" : provider.GetType().Name;

		private static string Pose(Vector3 position, Quaternion rotation) {
			var euler = rotation.eulerAngles;
			return $"({position.x:F2}, {position.y:F2}, {position.z:F2})  •  ({euler.x:F0}°, {euler.y:F0}°, {euler.z:F0}°)";
		}

		private static string DeviceTitle(InputDevice device)
			=> $"{(string.IsNullOrEmpty(device.name) ? "<sans nom>" : device.name)} — {device.characteristics}";

		private static List<InputDevice> DevicesWith(InputDeviceCharacteristics characteristics) {
			var devices = new List<InputDevice>();
			InputDevices.GetDevicesWithCharacteristics(characteristics, devices);
			return devices;
		}

		/// <summary>
		/// Trackers FBT : mêmes critères que <c>AutoHandProvider.GetTrackers</c> (device suivi qui n'est
		/// ni le casque ni un contrôleur), pour que le panel et le provider d'input voient la même chose.
		/// </summary>
		private static void Trackers(List<InputDevice> into) {
			into.Clear();
			InputDevices.GetDevices(into);
			into.RemoveAll(d => !d.characteristics.HasFlag(InputDeviceCharacteristics.TrackedDevice)
							  || d.characteristics.HasFlag(InputDeviceCharacteristics.HeadMounted)
							  || d.characteristics.HasFlag(InputDeviceCharacteristics.Controller));
		}

		/// <summary>Loaders configurés dans XR Plug-in Management pour la cible de build courante.</summary>
		private static IReadOnlyList<XRLoader> ConfiguredLoaders() {
			var group    = BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget);
			var settings = group == BuildTargetGroup.Unknown ? null : XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(group);
			var loaders  = settings?.Manager?.activeLoaders;

			if (loaders == null || loaders.Count == 0)
				return Array.Empty<XRLoader>();

			return loaders.Where(l => l != null).ToList();
		}

		/// <summary>
		/// L'asset trouvé par <c>IXRLoaderEditorProvider.Loader</c> est comparé par <b>type</b> : un
		/// projet peut avoir deux assets du même loader (celui créé par XR Plug-in Management et celui
		/// livré par un mod), et seule l'égalité de type compte — comme dans <c>XRSettingsSetup</c>.
		/// </summary>
		private static bool IsConfigured(XRLoader asset)
			=> asset != null && ConfiguredLoaders().Any(l => l.GetType() == asset.GetType());

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
