using Nox.CCK.Settings;
using UnityEngine;

namespace Nox.XR.Runtime.Settings {
	public sealed class PokeDisablePercentSetting : RangeHandler {
		public override string[] Path
			=> new[] { "xr", "interaction", "poke_disable_percent" };

		public override int Order => 60001;

		public override bool IsActive => true;

		public PokeDisablePercentSetting() {
			SetRange(0f, 1f);
			SetStep(0.01f);
			SetValue(PokeSettings.DisablePokePercent);
			SetLabelKey("settings.entry.xr.general.poke_disable_percent.label");
			SetValueKey("settings.range.value.percent");
		}

		protected override GameObject GetPrefab()
			=> Main.CoreAPI.AssetAPI.GetAsset<GameObject>("settings:prefabs/range.prefab");

		protected override void OnValueChanged(float value)
			=> PokeSettings.DisablePokePercent = value;
	}
}
