using Nox.CCK.Settings;
using Nox.CCK.Utils;
using Nox.UI;
using UnityEngine;

namespace Nox.XR.Runtime.Settings {
	/// <summary>
	/// Range setting to control IPD (inter-pupillary distance) in VR.
	/// </summary>
	public sealed class IPDSetting : RangeHandler {
		private const string ConfigKey = "settings.xr.ipd";
		public const float DefaultIPD = 0.064f; // 64mm

		public override string[] GetPath()
			=> new[] { "xr", "general", "ipd" };

		public override int GetOrder() => 2;

		public override bool IsActive()
			=> Client.Instance?.IsReady() ?? false;

		public IPDSetting() {
			// La valeur est stockée en mètres (l'unité de l'API XR) mais réglée/affichée en centimètres
			// décimaux : un IPD en millimètres entiers n'a pas assez de résolution.
			SetRange(0.050f, 0.080f);
			SetStep(0.001f);
			SetValue(Value);
			SetLabelKey("settings.entry.xr.general.ipd.label");
			SetValueKey("settings.range.value.float_centimeters");
		}

		protected override GameObject GetPrefab()
			=> Main.CoreAPI.AssetAPI.GetAsset<GameObject>("settings:prefabs/range.prefab");

		public static new float Value {
			get => Config.Load().Get(ConfigKey, DefaultIPD);
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
