using System;
using Nox.CCK.Language;
using Nox.CCK.Mods.Cores;
using Nox.CCK.Mods.Initializers;
using Nox.CCK.XR;
using Nox.Settings;
using Nox.XR.Runtime.Providers;
using Nox.XR.Runtime.Settings;

namespace Nox.XR.Runtime {
	public class Main : IMainModInitializer {
		internal static IMainModCoreAPI CoreAPI;

		private static ISettingAPI SettingAPI
			=> CoreAPI?.ModAPI?.GetMod("settings")?.GetInstance<ISettingAPI>();

		private IHandler[]   _settings = Array.Empty<IHandler>();
		private LanguagePack _lang;

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
				new FullBodyTrackingSetting()
			};

			foreach (var setting in _settings)
				SettingAPI?.Add(setting);
		}

		public void OnDisposeMain() {
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
