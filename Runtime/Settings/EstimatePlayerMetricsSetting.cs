using Nox.CCK.Settings;
using Nox.Settings;
using Nox.XR.Runtime.FullBody;
using UnityEngine;

namespace Nox.XR.Runtime.Settings {
	/// <summary>
	/// "Calibrate" button that estimates the player's real height and arm span: stay in T-pose and don't move for
	/// <see cref="FullBodyCalibration.MetricsEstimateDelay"/> seconds, the values are then written to
	/// <see cref="RealHeightSetting"/> / <see cref="PlayerArmSpanSetting"/> (the avatar is never rescaled, its size
	/// is the reference the player's container is fitted to).
	/// </summary>
	public sealed class EstimatePlayerMetricsSetting : ButtonHandler {
		public override string[] Path
			=> new[] { "xr", "fbt", "estimate_player_metrics" };

		public override int Order => 60002;

		public override bool IsActive
			=> Client.Instance != null
			   && FullBodyTrackingSetting.Value
			   && FullBodyCalibration.IsXRControllerActive;

		public EstimatePlayerMetricsSetting() {
			SetLabel("settings.entry.xr.general.estimate_player_metrics.label");
			RefreshLabel();
		}

		public override void OnUpdated(IHandler handler)
			=> RefreshLabel();

		private void RefreshLabel() {
			var calibration = FullBodyCalibration.Instance;
			if (calibration is { IsEstimatingMetrics: true }) {
				SetButtonText(
					"settings.entry.xr.general.estimate_player_metrics.estimating",
					Mathf.CeilToInt(calibration.MetricsEstimateRemaining).ToString()
				);
				return;
			}

			SetButtonText("settings.entry.xr.general.estimate_player_metrics.label");
		}

		protected override GameObject GetPrefab()
			=> Main.CoreAPI.AssetAPI.GetAsset<GameObject>("settings:prefabs/button.prefab");

		public override void OnClick(IContext context) {
			FullBodyCalibration.Instance?.StartMetricsEstimate();
			RefreshLabel();
		}
	}
}
