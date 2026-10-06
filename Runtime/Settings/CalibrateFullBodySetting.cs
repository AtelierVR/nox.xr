using Nox.CCK.Settings;
using Nox.Settings;
using Nox.XR.Runtime.FullBody;
using UnityEngine;

namespace Nox.XR.Runtime.Settings {
	/// <summary>
	/// "Calibrate" button. The first click starts a full-body calibration (avatar in the
	/// calibration-only pose with the trackers displayed); the next click confirms and saves it.
	/// </summary>
	public sealed class CalibrateFullBodySetting : ButtonHandler {
		public override string[] GetPath()
			=> new[] { "xr", "general", "full_body_calibration" };

		public override int GetOrder() => 4;

		public override bool IsActive()
			=> Client.Instance != null
			   && FullBodyTrackingSetting.Value
			   && FullBodyCalibration.IsXRControllerActive;

		public CalibrateFullBodySetting() {
			SetLabel("settings.entry.xr.general.full_body_calibration.label");
			RefreshLabel();
		}

		public override void OnUpdated(IHandler handler)
			=> RefreshLabel();

		private void RefreshLabel()
			=> SetButtonText(FullBodyCalibration.Instance is { IsCalibrating: true }
				? "settings.entry.xr.general.full_body_calibration.confirm"
				: "settings.entry.xr.general.full_body_calibration.start");

		protected override GameObject GetPrefab()
			=> Main.CoreAPI.AssetAPI.GetAsset<GameObject>("settings:prefabs/button.prefab");

		public override void OnClick(IContext context) {
			var calibration = FullBodyCalibration.Instance;
			if (calibration == null)
				return;

			if (calibration.IsCalibrating)
				calibration.ConfirmCalibration();
			else
				calibration.StartCalibration();

			// Le libellé dépend de IsCalibrating, qui peut aussi changer sans clic (validation aux
			// manettes) : c'est SettingsNotifier qui resynchronise dans ce cas (voir FullBodyCalibration).
			RefreshLabel();
		}
	}
}
