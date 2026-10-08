using Nox.CCK.Settings;
using UnityEngine;

namespace Nox.XR.Runtime.Settings {
	/// <summary>
		/// Angle turned at each snap, used while <see cref="MovementSettings.TurnMode"/> is
		/// <see cref="XRTurnMode.Snap"/>.
	/// </summary>
	public sealed class SnapTurnAngleSetting : RangeHandler {
		public override string[] GetPath()
			=> new[] { "xr", "movement", "snap_turn_angle" };

		public override int GetOrder()
			=> 1;

		public override bool IsActive()
			=> MovementSettings.TurnMode == XRTurnMode.Snap;

		public SnapTurnAngleSetting() {
			SetRange(MovementSettings.MinSnapTurnAngle, MovementSettings.MaxSnapTurnAngle);
			SetStep(5f);
			SetValue(MovementSettings.SnapTurnAngle);
			SetLabelKey("settings.entry.xr.movement.snap_turn_angle.label");
			SetValueKey("settings.range.value.degrees");
		}

		protected override GameObject GetPrefab()
			=> Main.CoreAPI.AssetAPI.GetAsset<GameObject>("settings:prefabs/range.prefab");

		protected override void OnValueChanged(float value)
			=> MovementSettings.SnapTurnAngle = value;
	}
}
