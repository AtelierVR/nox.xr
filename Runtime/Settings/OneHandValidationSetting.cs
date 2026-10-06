using Nox.CCK.Settings;
using Nox.CCK.Utils;
using Nox.XR.Runtime.FullBody;
using UnityEngine;

namespace Nox.XR.Runtime.Settings {
	/// <summary>
	/// When enabled, a single controller press is enough to validate actions that normally require
	/// both controllers (e.g. confirming a full-body calibration).
	/// </summary>
	public sealed class OneHandValidationSetting : ToggleHandler {
		private const string ConfigKey = "settings.xr.one_hand_validation";

		public override string[] GetPath()
			=> new[] { "xr", "general", "one_hand_validation" };

		public override int GetOrder() => 6;

		public override bool IsActive()
			=> Client.Instance != null
			   && EnableXRSetting.Value
			   && FullBodyCalibration.IsXRControllerActive;

		public OneHandValidationSetting() {
			SetValue(Value, notify: false);
			SetLabelKey("settings.entry.xr.general.one_hand_validation.label");
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
