using System;
using Nox.CCK.Language;
using Nox.CCK.Mods.Cores;
using Nox.CCK.Mods.Initializers;
using Nox.Settings;
using Nox.XR.Settings;

namespace Nox.XR {
	public class Main : IMainModInitializer {
		internal static IMainModCoreAPI CoreAPI;

		private static ISettingAPI SettingAPI
			=> CoreAPI?.ModAPI?.GetMod("settings")?.GetInstance<ISettingAPI>();

		private IHandler[]   _settings = Array.Empty<IHandler>();
		private LanguagePack _lang;

		public void OnInitializeMain(IMainModCoreAPI api) {
			CoreAPI = api;

			_lang = api.AssetAPI.GetAsset<LanguagePack>("lang.asset");
			LanguageManager.AddPack(_lang);

			_settings = new IHandler[] {
				new StartVRSetting(),
				new IPDSetting()
			};

			foreach (var setting in _settings)
				SettingAPI?.Add(setting);
		}

		public void OnDisposeMain() {
			foreach (var setting in _settings)
				SettingAPI?.Remove(setting.GetPath());

			_settings = Array.Empty<IHandler>();

			LanguageManager.RemovePack(_lang);
			_lang   = null;
			CoreAPI = null;
		}
	}
}
