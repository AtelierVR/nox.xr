using Nox.CCK.Settings;
using UnityEngine;

namespace Nox.XR.Runtime.Settings {
	/// <summary>
	/// Cuts a hand's near/far ray while one of its pokes has a target in range, so the ray and the poke
	/// do not compete for the same surface. Turn it off to keep the ray active while poking.
	/// </summary>
	public sealed class PokeDisableNearFarSetting : ToggleHandler {
		public override string[] Path
			=> new[] { "xr", "interaction", "poke_disable_near_far" };

		public override int Order => 60002;

		public override bool IsActive => true;

		public PokeDisableNearFarSetting() {
			SetValue(PokeSettings.DisableNearFar, notify: false);
			SetLabelKey("settings.entry.xr.general.poke_disable_near_far.label");
		}

		protected override GameObject GetPrefab()
			=> Main.CoreAPI.AssetAPI.GetAsset<GameObject>("settings:prefabs/toggle.prefab");

		protected override void OnValueChanged(bool value)
			=> PokeSettings.DisableNearFar = value;
	}
}
