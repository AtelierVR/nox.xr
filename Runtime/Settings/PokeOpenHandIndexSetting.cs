using Nox.CCK.Settings;
using UnityEngine;

namespace Nox.XR.Runtime.Settings {
	/// <summary>
	/// Keeps the <b>index</b> poke while the hand is <b>fully open</b>. Off by default: a fully open hand
	/// cuts every poke, so pointing with an extended finger never pokes by itself. On restores the
	/// "point to poke" behaviour (only the index finger pokes on an open hand).
	/// </summary>
	public sealed class PokeOpenHandIndexSetting : ToggleHandler {
		public override string[] Path
			=> new[] { "xr", "interaction", "poke_open_hand_index" };

		public override int Order => 60003;

		public override bool IsActive => true;

		public PokeOpenHandIndexSetting() {
			SetValue(PokeSettings.OpenHandIndex, notify: false);
			SetLabelKey("settings.entry.xr.general.poke_open_hand_index.label");
		}

		protected override GameObject GetPrefab()
			=> Main.CoreAPI.AssetAPI.GetAsset<GameObject>("settings:prefabs/toggle.prefab");

		protected override void OnValueChanged(bool value)
			=> PokeSettings.OpenHandIndex = value;
	}
}
