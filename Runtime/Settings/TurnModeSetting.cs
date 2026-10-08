using System.Collections.Generic;
using Nox.CCK.Settings;
using Nox.UI;
using Nox.UI.modals;
using UnityEngine;

namespace Nox.XR.Runtime.Settings {
	/// <summary>
		/// XR rotation mode: fixed-angle snap turn or continuous turn. The value is stored
		/// and read through <see cref="MovementSettings"/>.
	/// </summary>
	public sealed class TurnModeSetting : DropdownHandler {
		public override string[] GetPath()
			=> new[] { "xr", "movement", "turn_mode" };

		public override int GetOrder()
			=> 0;

		public override bool IsActive()
			=> true;

		public TurnModeSetting() {
			SetLabel($"settings.entry.{string.Join(".", GetPath())}.label");
			SetOptions(GetOptions());
			SetValue(MovementSettings.Key(MovementSettings.TurnMode), notify: false);
		}

		private static Dictionary<string, string[]> GetOptions()
			=> new() {
				[MovementSettings.Key(XRTurnMode.Snap)] =
					new[] { "settings.entry.xr.movement.turn_mode.option.snap" },
				[MovementSettings.Key(XRTurnMode.Smooth)] =
					new[] { "settings.entry.xr.movement.turn_mode.option.smooth" }
			};

		protected override GameObject GetPrefab()
			=> Main.CoreAPI.AssetAPI.GetAsset<GameObject>("settings:prefabs/dropdown.prefab");

		protected override IModalBuilder GetModalBuilder(IMenu menu)
			=> Main.CoreAPI?.ModAPI?.GetMod("ui")?.GetInstance<IUiAPI>()?.MakeModal(menu);

		protected override void OnValueChanged(string value)
			=> MovementSettings.TurnMode = MovementSettings.Parse(value);
	}
}
