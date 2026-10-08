using Nox.CCK.Settings;
using Nox.CCK.Utils;
using Nox.Settings;
using Nox.XR.Runtime.FullBody;
using UnityEngine;

namespace Nox.XR.Runtime.Settings {
	/// <summary>
	/// Real height of the player, in metres (0 = not set): the height the player measures in real life, as
	/// described by the headset and the trackers.
	/// <para>
	/// The player's pose is fitted to the avatar's proportions with it (see
	/// <c>AvatarSyncConnector.RescaleTrackers</c>): a taller avatar enlarges the pose, a shorter one reduces it.
	/// The avatar itself is never rescaled, its own size is the reference.
	/// </para>
	/// </summary>
	public sealed class RealHeightSetting : RangeHandler {
		private const string ConfigKey = "settings.xr.fbt_real_height";

		/// <summary>Legacy key, still read to keep an existing value, never written.</summary>
		private const string LegacyConfigKey = "settings.xr.fbt_player_height";

		/// <summary>0 = no height set: no fitting applied.</summary>
		public const float Automatic = 0f;

		public override string[] GetPath()
			=> new[] { "xr", "fbt", "real_height" };

		public override int GetOrder() => 3;

		public override bool IsActive()
			=> Client.Instance != null
			   && FullBodyTrackingSetting.Value
			   && FullBodyCalibration.IsXRControllerActive;

		public RealHeightSetting() {
			// Stored in metres (used as is by the calibration) but set/displayed in decimal centimetres.
			SetRange(0f, 2.5f);
			SetStep(0.005f);
			SetValue(Value);
			SetLabelKey("settings.entry.xr.fbt.real_height.label");
			SetValueKey("settings.range.value.float_centimeters");
		}

		protected override GameObject GetPrefab()
			=> Main.CoreAPI.AssetAPI.GetAsset<GameObject>("settings:prefabs/range.prefab");

		public static float Value {
			get => Config.Load().Get(ConfigKey, Config.Load().Get(LegacyConfigKey, Automatic));
			set {
				var config = Config.Load();
				config.Set(ConfigKey, value);
				config.Save();
			}
		}

		protected override void OnValueChanged(float value)
			=> Value = value;

		/// <summary>Resynchronises the slider when the value is written from elsewhere.</summary>
		public override void OnUpdated(IHandler handler)
			=> SetValue(Value);
	}
}
