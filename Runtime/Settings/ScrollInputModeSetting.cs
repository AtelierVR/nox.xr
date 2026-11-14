using System.Collections.Generic;
using Nox.CCK.Settings;
using Nox.UI;
using Nox.UI.modals;
using UnityEngine;

namespace Nox.XR.Runtime.Settings {
	/// <summary>
	/// What the joystick does while a hand points at a scrollable list: scroll only (default),
	/// both, or move the player only.
	/// </summary>
	public sealed class ScrollInputModeSetting : DropdownHandler {
		public override string[] Path
			=> new[] { "xr", "interaction", "scroll_mode" };

		public override int Order
			=> 60004;

		public override bool IsActive
			=> true;

		public ScrollInputModeSetting() {
			SetLabel($"settings.entry.{string.Join(".", Path)}.label");
			SetOptions(GetOptions());
			SetValue(ScrollInputSettings.Key(ScrollInputSettings.Mode), notify: false);
		}

		private static Dictionary<string, string[]> GetOptions()
			=> new() {
				[ScrollInputSettings.Key(XRScrollInput.UiOnly)] =
					new[] { "settings.entry.xr.interaction.scroll_mode.option.ui" },
				[ScrollInputSettings.Key(XRScrollInput.Both)] =
					new[] { "settings.entry.xr.interaction.scroll_mode.option.both" },
				[ScrollInputSettings.Key(XRScrollInput.MovementOnly)] =
					new[] { "settings.entry.xr.interaction.scroll_mode.option.movement" }
			};

		protected override GameObject GetPrefab()
			=> Main.CoreAPI.AssetAPI.GetAsset<GameObject>("settings:prefabs/dropdown.prefab");

		protected override IModalBuilder GetModalBuilder(IMenu menu)
			=> Main.CoreAPI?.ModAPI?.GetMod("ui")?.GetInstance<IUiAPI>()?.MakeModal(menu);

		protected override void OnValueChanged(string value)
			=> ScrollInputSettings.Mode = ScrollInputSettings.Parse(value);
	}
}
