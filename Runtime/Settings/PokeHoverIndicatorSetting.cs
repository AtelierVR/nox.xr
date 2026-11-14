using Nox.CCK.Settings;
using UnityEngine;

namespace Nox.XR.Runtime.Settings {
	/// <summary>
	/// Shows a low opacity ring on the UI element a finger or a ray can touch. Turn it off to keep the UI
	/// free of any indicator.
	/// </summary>
	public sealed class PokeHoverIndicatorSetting : ToggleHandler {
		public override string[] Path
			=> new[] { "xr", "interaction", "hover_indicator" };

		public override int Order => 60003;

		public override bool IsActive => true;

		public PokeHoverIndicatorSetting() {
			SetValue(PokeSettings.HoverIndicator, notify: false);
			SetLabelKey("settings.entry.xr.general.poke_hover_indicator.label");
		}

		protected override GameObject GetPrefab()
			=> Main.CoreAPI.AssetAPI.GetAsset<GameObject>("settings:prefabs/toggle.prefab");

		protected override void OnValueChanged(bool value)
			=> PokeSettings.HoverIndicator = value;
	}
}
