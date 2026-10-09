using Nox.CCK.Settings;
using UnityEngine;

namespace Nox.XR.Runtime.Settings {
	public sealed class PokeEnabledSetting : ToggleHandler {
		public override string[] Path
			=> new[] { "xr", "interaction", "poke" };

		public override int Order
			=> 60001;

		public override bool IsActive
			=> true;

		public PokeEnabledSetting() {
			SetValue(PokeSettings.Enabled, notify: false);
			SetLabelKey("settings.entry.xr.general.poke.label");
		}

		protected override GameObject GetPrefab()
			=> Main.CoreAPI.AssetAPI.GetAsset<GameObject>("settings:prefabs/toggle.prefab");

		protected override void OnValueChanged(bool value)
			=> PokeSettings.Enabled = value;
	}
}
