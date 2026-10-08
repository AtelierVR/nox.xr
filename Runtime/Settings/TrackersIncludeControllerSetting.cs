using Nox.CCK.Settings;
using Nox.CCK.Utils;
using Nox.XR.Runtime.FullBody;
using UnityEngine;

namespace Nox.XR.Runtime.Settings {
	/// <summary>
	/// Allows trackers flagged as controllers to be used for full-body tracking.
	/// <para>
	/// Some runtimes report a Vive tracker with the
	/// <see cref="UnityEngine.XR.InputDeviceCharacteristics.Controller"/> flag (a role was assigned
	/// to it). The default filter rejects those devices, which makes the tracker
	/// invisible to the game; enabling this setting keeps them. Real controllers are unaffected:
	/// they are also flagged Left/Right and stay filtered out as hands.
	/// </para>
	/// </summary>
	public sealed class TrackersIncludeControllerSetting : ToggleHandler {
		private const string ConfigKey = "settings.xr.trackers_include_controller";

		public override string[] GetPath()
			=> new[] { "xr", "fbt", "trackers_include_controller" };

		public override int GetOrder() => 6;

		public override bool IsActive()
			=> Client.Instance != null
			   && EnableXRSetting.Value
			   && FullBodyTrackingSetting.Value
			   && FullBodyCalibration.IsXRControllerActive;

		public TrackersIncludeControllerSetting() {
			SetValue(Value, notify: false);
			SetLabelKey("settings.entry.xr.general.trackers_include_controller.label");
		}

		protected override GameObject GetPrefab()
			=> Main.CoreAPI.AssetAPI.GetAsset<GameObject>("settings:prefabs/toggle.prefab");

		public static new bool Value {
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
