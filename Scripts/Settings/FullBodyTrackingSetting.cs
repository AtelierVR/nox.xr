using Nox.CCK.Settings;
using Nox.CCK.Utils;
using UnityEngine;

namespace Nox.XR.Settings {
	/// <summary>
	/// Toggle setting to enable or disable Full-Body Tracking (FBT).
	/// When enabled, additional Vive/VR trackers are used for hip and feet tracking.
	/// </summary>
	public sealed class FullBodyTrackingSetting : ToggleHandler {
		private const string ConfigKey = "settings.xr.fbt_enabled";

		public override string[] GetPath()
			=> new[] { "xr", "general", "full_body_tracking" };

		public override int GetOrder() => 3;

		public override bool IsActive()
			=> EnableXRSetting.Value && Client.Instance != null;

		public FullBodyTrackingSetting() {
			SetValue(Value, notify: false);
			SetLabelKey("settings.entry.xr.general.full_body_tracking.label");
		}

		protected override GameObject GetPrefab()
			=> Main.CoreAPI.AssetAPI.GetAsset<GameObject>("settings:prefabs/toggle.prefab");

		public static bool Value {
			get => Config.Load().Get(ConfigKey, false);
			set {
				var config = Config.Load();
				config.Set(ConfigKey, value);
				config.Save();
			}
		}

		protected override void OnValueChanged(bool value)
			=> Value = value;
	}
}
