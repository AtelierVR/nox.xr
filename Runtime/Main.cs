using System;
using Nox.CCK.Language;
using Nox.CCK.Mods.Cores;
using Nox.CCK.Mods.Initializers;
using Nox.CCK.XR;
using Nox.Control;
using Nox.Settings;
using Nox.Terminal;
using Nox.XR.Runtime.Providers;
using Nox.XR.Runtime.Settings;

namespace Nox.XR.Runtime {
	public class Main : IMainModInitializer {
		internal static IMainModCoreAPI CoreAPI;

		private static ISettingAPI SettingAPI
			=> CoreAPI?.ModAPI?.GetMod("settings")?.GetInstance<ISettingAPI>();

		/// <summary>
		/// API of the optional <c>nox.control</c> mod (MCP/REST/WebSocket server): null when the mod
		/// is not installed, in which case no operator is published.
		/// </summary>
		private static IControlAPI ControlAPI
			=> CoreAPI?.ModAPI?.GetMod("control")?.GetInstance<IControlAPI>();

		/// <summary>
		/// API of the optional <c>nox.terminal</c> mod: null when the mod is not installed.
		/// </summary>
		private static ITerminalAPI TerminalAPI
			=> CoreAPI?.ModAPI?.GetMod("terminal")?.GetInstance<ITerminalAPI>();

		private IHandler[]   _settings = Array.Empty<IHandler>();
		private LanguagePack _lang;

		private uint[] _operators = Array.Empty<uint>();
		private uint[] _commands  = Array.Empty<uint>();

		public void OnInitializeMain(IMainModCoreAPI api) {
			CoreAPI = api;

			// Repli générique : la détection de devices (XRInputs.HasHeadset, Client.IsReady(),
			// priorité du controller XR) doit fonctionner même sans provider de mod (AutoHand).
			XRInputs.DefaultProvider ??= new UnityXR();

			_lang = api.AssetAPI.GetAsset<LanguagePack>("lang.asset");
			LanguageManager.AddPack(_lang);

			_settings = new IHandler[] {
				new EnableXRSetting(),
				new StartVRSetting(),
				new PokeEnabledSetting(),
				new PokeDisablePercentSetting(),
				new IPDSetting(),
				new FullBodyTrackingSetting(),
				new CalibrateFullBodySetting(),
				new FullBodyCalibrationRangeSetting(),
				new OneHandValidationSetting(),
				new TrackersIncludeControllerSetting()
			};

			foreach (var setting in _settings)
				SettingAPI?.Add(setting);

			RegisterIntegrations();
		}

		#region Integrations (nox.control / nox.terminal)

		/// <summary>
		/// Publishes the XR diagnostics to the optional control and terminal mods. Both lookups
		/// return <c>null</c> when the mod is missing, so their absence is never an error.
		/// </summary>
		private void RegisterIntegrations() {
			var control = ControlAPI;
			if (control != null) {
				var operators = Nox.XR.Runtime.Operators.XRControl.All();
				_operators = new uint[operators.Length];

				for (var i = 0; i < operators.Length; i++)
					_operators[i] = control.Register(operators[i]);
			}

			var terminal = TerminalAPI;
			if (terminal != null)
				_commands = new[] { terminal.Register(new Nox.XR.Runtime.Terminal.XRCommand()) };
		}

		private void UnregisterIntegrations() {
			var control = ControlAPI;
			if (control != null)
				foreach (var id in _operators)
					control.Unregister(id);
			_operators = Array.Empty<uint>();

			var terminal = TerminalAPI;
			if (terminal != null)
				foreach (var id in _commands)
					terminal.Unregister(id);
			_commands = Array.Empty<uint>();
		}

		#endregion

		public void OnDisposeMain() {
			UnregisterIntegrations();

			if (XRInputs.Provider == null)
				XRInputs.DefaultProvider = null;

			foreach (var setting in _settings)
				SettingAPI?.Remove(setting.GetPath());
			_settings = Array.Empty<IHandler>();

			LanguageManager.RemovePack(_lang);
			_lang   = null;
			CoreAPI = null;
		}
	}
}
