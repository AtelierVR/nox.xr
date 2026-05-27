using Nox.CCK.Settings;
using UnityEngine;

namespace Nox.XR.Settings {
	public sealed class PokeEnabledSetting : ToggleHandler {
		public override string[] GetPath()
			=> new[] { "xr", "general", "poke" };

		public override int GetOrder()
			=> 1;

		public override bool IsActive()
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
