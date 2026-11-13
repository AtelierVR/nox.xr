using System.Collections.Generic;
using Nox.CCK.Settings;
using Nox.UI;
using Nox.UI.modals;
using UnityEngine;

namespace Nox.XR.Runtime.Settings {
	/// <summary>
	/// What happens when the player menu is left farther than <see cref="MenuSettings.MaxDistance"/>: it is
	/// hidden, brought back in front of the view, or dragged along at the edge of the reach circle. The value
	/// is stored and read through <see cref="MenuSettings"/>.
	/// </summary>
	public sealed class MenuRecallSetting : DropdownHandler {
		public override string[] Path
			=> new[] { "xr", "menu", "recall" };

		public override int Order
			=> 60012;

		public override bool IsActive
			=> MenuSettings.HasMaxDistance;

		public MenuRecallSetting() {
			SetLabel($"settings.entry.{string.Join(".", Path)}.label");
			SetOptions(GetOptions());
			SetValue(MenuSettings.Key(MenuSettings.Recall), notify: false);
		}

		private static Dictionary<string, string[]> GetOptions()
			=> new() {
				[MenuSettings.Key(XRMenuRecall.Despawn)] =
					new[] { "settings.entry.xr.menu.recall.option.despawn" },
				[MenuSettings.Key(XRMenuRecall.Teleport)] =
					new[] { "settings.entry.xr.menu.recall.option.teleport" },
				[MenuSettings.Key(XRMenuRecall.Clamp)] =
					new[] { "settings.entry.xr.menu.recall.option.clamp" }
			};

		protected override GameObject GetPrefab()
			=> Main.CoreAPI.AssetAPI.GetAsset<GameObject>("settings:prefabs/dropdown.prefab");

		protected override IModalBuilder GetModalBuilder(IMenu menu)
			=> Main.CoreAPI?.ModAPI?.GetMod("ui")?.GetInstance<IUiAPI>()?.MakeModal(menu);

		protected override void OnValueChanged(string value)
			=> MenuSettings.Recall = MenuSettings.ParseRecall(value);
	}
}
