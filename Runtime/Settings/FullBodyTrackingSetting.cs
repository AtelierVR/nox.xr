using Nox.CCK.Events;
using Nox.CCK.Settings;
using Nox.CCK.Utils;
using UnityEngine;

namespace Nox.XR.Runtime.Settings {
	/// <summary>
	/// Toggle setting to enable or disable Full-Body Tracking (FBT).
	/// When enabled, additional Vive/VR trackers are used for hip and feet tracking.
	/// </summary>
	public sealed class FullBodyTrackingSetting : ToggleHandler {
		private const string CONFIG_KEY = "settings.xr.fbt_enabled";

		public override string[] Path
			=> new[] { "xr", "fbt", "full_body_tracking" };

		public override int Order => 60000;

		public override bool IsActive
			=> Client.Instance != null;

		public override bool IsEditable
			=> Client.Instance != null && EnableXRSetting.Value;

		public static readonly NoxEvent<bool> Changed = new();

		public FullBodyTrackingSetting() {
			SetValue(Value, notify: false);
			SetLabelKey("settings.entry.xr.general.full_body_tracking.label");
		}

		protected override GameObject GetPrefab()
			=> Main.CoreAPI.AssetAPI.GetAsset<GameObject>("settings:prefabs/toggle.prefab");

		public static new bool Value {
			get => Config.Load().Get(CONFIG_KEY, false);
			set {
				var config = Config.Load();
				config.Set(CONFIG_KEY, value);
				config.Save();
			}
		}

		protected override void OnValueChanged(bool value) {
			Value = value;
			Widgets.CalibrationWidget.Refresh();
			Changed.Invoke(value);
		}

		public override void OnUpdated(Nox.Settings.IHandler handler)
			=> RefreshEditable();
	}
}
