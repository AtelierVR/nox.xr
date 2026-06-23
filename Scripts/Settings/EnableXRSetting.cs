using Nox.CCK.Settings;
using Nox.CCK.Utils;
using UnityEngine;

namespace Nox.XR.Settings {
	/// <summary>
	/// Toggle setting to enable or disable XR at startup and at runtime.
	/// Disabling while in VR will exit VR.
	/// </summary>
	public sealed class EnableXRSetting : ToggleHandler {
		private const string ConfigKey = "settings.xr.enabled";

		public override string[] GetPath()
			=> new[] { "xr", "general", "enable_xr" };

		public override int GetOrder() => -1;

		public override bool IsActive() => true;

		public EnableXRSetting() {
			SetValue(Value, notify: false);
			SetLabelKey("settings.entry.xr.general.enable_xr.label");
		}

		protected override GameObject GetPrefab()
			=> Main.CoreAPI.AssetAPI.GetAsset<GameObject>("settings:prefabs/toggle.prefab");

		public static bool Value {
			get {
				var args = ArgsParser.Parse();
				return args.GetBool("vr", Config.Load().Get(ConfigKey, true));
			}
			set {
				var config = Config.Load();
				config.Set(ConfigKey, value);
				config.Save();
			}
		}

		protected override void OnValueChanged(bool value) {
			Value = value;
			if (!value && Client.Instance != null && Client.Instance.IsXRInitialized())
				Client.Instance.StopLoader();
		}
	}
}
