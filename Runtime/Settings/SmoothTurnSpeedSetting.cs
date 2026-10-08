using Nox.CCK.Settings;
using UnityEngine;

namespace Nox.XR.Runtime.Settings {
	/// <summary>
		/// Rotation speed (degrees per second), used while <see cref="MovementSettings.TurnMode"/> is
		/// <see cref="XRTurnMode.Smooth"/>.
	/// </summary>
	public sealed class SmoothTurnSpeedSetting : RangeHandler {
		public override string[] GetPath()
			=> new[] { "xr", "movement", "smooth_turn_speed" };

		public override int GetOrder()
			=> 2;

		public override bool IsActive()
			=> MovementSettings.TurnMode == XRTurnMode.Smooth;

		public SmoothTurnSpeedSetting() {
			SetRange(MovementSettings.MinSmoothTurnSpeed, MovementSettings.MaxSmoothTurnSpeed);
			SetStep(10f);
			SetValue(MovementSettings.SmoothTurnSpeed);
			SetLabelKey("settings.entry.xr.movement.smooth_turn_speed.label");
			SetValueKey("settings.range.value.degrees_per_second");
		}

		protected override GameObject GetPrefab()
			=> Main.CoreAPI.AssetAPI.GetAsset<GameObject>("settings:prefabs/range.prefab");

		protected override void OnValueChanged(float value)
			=> MovementSettings.SmoothTurnSpeed = value;
	}
}
