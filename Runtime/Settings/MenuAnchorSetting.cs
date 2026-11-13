using System.Collections.Generic;
using Nox.CCK.Settings;
using Nox.UI;
using Nox.UI.modals;
using UnityEngine;

namespace Nox.XR.Runtime.Settings {
	/// <summary>
	/// What the player menu is anchored to: the head (always in front of the view), the player (it travels
	/// with them) or the world (it stays where it was put). The value is stored and read through
	/// <see cref="MenuSettings"/>.
	/// </summary>
	public sealed class MenuAnchorSetting : DropdownHandler {
		public override string[] Path
			=> new[] { "xr", "menu", "anchor" };

		public override int Order
			=> 60011;

		public override bool IsActive
			=> true;

		public MenuAnchorSetting() {
			SetLabel($"settings.entry.{string.Join(".", Path)}.label");
			SetOptions(GetOptions());
			SetValue(MenuSettings.Key(MenuSettings.Anchor), notify: false);
		}

		private static Dictionary<string, string[]> GetOptions()
			=> new() {
				[MenuSettings.Key(XRMenuAnchor.Head)] =
					new[] { "settings.entry.xr.menu.anchor.option.head" },
				[MenuSettings.Key(XRMenuAnchor.Player)] =
					new[] { "settings.entry.xr.menu.anchor.option.player" },
				[MenuSettings.Key(XRMenuAnchor.World)] =
					new[] { "settings.entry.xr.menu.anchor.option.world" }
			};

		protected override GameObject GetPrefab()
			=> Main.CoreAPI.AssetAPI.GetAsset<GameObject>("settings:prefabs/dropdown.prefab");

		protected override IModalBuilder GetModalBuilder(IMenu menu)
			=> Main.CoreAPI?.ModAPI?.GetMod("ui")?.GetInstance<IUiAPI>()?.MakeModal(menu);

		protected override void OnValueChanged(string value)
			=> MenuSettings.Anchor = MenuSettings.Parse(value);
	}
}
