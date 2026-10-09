using Nox.CCK.Settings;
using Nox.CCK.Utils;
using Nox.XR.Runtime.FullBody;
using UnityEngine;

namespace Nox.XR.Runtime.Settings {
	/// <summary>
	/// Maximum distance (metres) between a tracker and a bone for an automatic match during
	/// full-body calibration. Can be adjusted live while calibrating.
	/// </summary>
	public sealed class FullBodyCalibrationRangeSetting : RangeHandler {
		private const string ConfigKey = "settings.xr.fbt_calibration_range";
		/// <summary>Default calibration range (metres). Kept at 0.60 or below.</summary>
		public const float DefaultRange = 0.5f;

		public override string[] Path
			=> new[] { "xr", "fbt", "full_body_calibration_range" };

		public override int Order => 60005;

		public override bool IsActive
			=> Client.Instance != null
			   && FullBodyTrackingSetting.Value
			   && FullBodyCalibration.IsXRControllerActive;

		public FullBodyCalibrationRangeSetting() {
			// Stored in metres, set and displayed in decimal centimetres.
			SetRange(0.1f, 3f);
			SetStep(0.01f);
			SetValue(Value);
			SetLabelKey("settings.entry.xr.general.full_body_calibration_range.label");
			SetValueKey("settings.range.value.float_centimeters");
		}

		protected override GameObject GetPrefab()
			=> Main.CoreAPI.AssetAPI.GetAsset<GameObject>("settings:prefabs/range.prefab");

		public static new float Value {
			get => Config.Load().Get(ConfigKey, DefaultRange);
			set {
				var config = Config.Load();
				config.Set(ConfigKey, value);
				config.Save();
			}
		}

		protected override void OnValueChanged(float value)
			=> Value = value;
	}
}
