using Nox.CCK.Settings;
using Nox.CCK.Utils;
using Nox.Settings;
using Nox.XR.Runtime.FullBody;
using UnityEngine;

namespace Nox.XR.Runtime.Settings {
	/// <summary>
	/// Arm span of the player, in metres (0 = not set): the wrist-to-wrist distance measured in T-pose.
	/// <para>
	/// Used by the full-body calibration to check that the avatar's arms are proportioned like the player's;
	/// a calibration made on an avatar whose arms are too short or too long bends the elbows even in T-pose.
	/// Estimated automatically by <c>EstimatePlayerMetricsSetting</c>.
	/// </para>
	/// </summary>
	public sealed class PlayerArmSpanSetting : RangeHandler {
		private const string ConfigKey = "settings.xr.fbt_player_arm_span";

		/// <summary>0 = no arm span set.</summary>
		public const float Automatic = 0f;

		public override string[] GetPath()
			=> new[] { "xr", "fbt", "player_arm_span" };

		public override int GetOrder() => 4;

		public override bool IsActive()
			=> Client.Instance != null
			   && FullBodyTrackingSetting.Value
			   && FullBodyCalibration.IsXRControllerActive;

		public PlayerArmSpanSetting() {
			SetRange(0f, 3f);
			SetStep(0.005f);
			SetValue(Value);
			SetLabelKey("settings.entry.xr.general.player_arm_span.label");
			SetValueKey("settings.range.value.float_centimeters");
		}

		protected override GameObject GetPrefab()
			=> Main.CoreAPI.AssetAPI.GetAsset<GameObject>("settings:prefabs/range.prefab");

		public static new float Value {
			get => Config.Load().Get(ConfigKey, Automatic);
			set {
				var config = Config.Load();
				config.Set(ConfigKey, value);
				config.Save();
			}
		}

		protected override void OnValueChanged(float value)
			=> Value = value;

		/// <summary>Resynchronises the slider when the T-pose estimation writes the value.</summary>
		public override void OnUpdated(IHandler handler)
			=> SetValue(Value);
	}
}
