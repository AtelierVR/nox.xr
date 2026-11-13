using Nox.CCK.Settings;
using UnityEngine;

namespace Nox.XR.Runtime.Settings {
	/// <summary>
	/// How far the player may leave the menu behind before it is brought back in front of the view.
	/// <para>
	/// The slider goes from <see cref="MenuSettings.NoLimit"/> (no limit at all) to
	/// <see cref="MenuSettings.MaxMaxDistance"/> metres: any value above zero is a real limit, at least
	/// <see cref="MenuSettings.MinMaxDistance"/> metre. The value is stored and read through
	/// <see cref="MenuSettings"/>.
	/// </para>
	/// </summary>
	public sealed class MaxMenuDistanceSetting : RangeHandler {
		public override string[] Path
			=> new[] { "xr", "menu", "max_distance" };

		public override int Order
			=> 60010;

		public override bool IsActive
			=> true;

		public MaxMenuDistanceSetting() {
			SetRange(MenuSettings.NoLimit, MenuSettings.MaxMaxDistance);
			SetStep(1f);
			SetValue(MenuSettings.MaxDistance);
			SetLabelKey("settings.entry.xr.menu.max_distance.label");
			SetValueKey("settings.range.value.meters");
		}

		protected override GameObject GetPrefab()
			=> Main.CoreAPI.AssetAPI.GetAsset<GameObject>("settings:prefabs/range.prefab");

		protected override void OnValueChanged(float value)
			=> MenuSettings.MaxDistance = value;
	}
}
