using Nox.CCK.Settings;
using Nox.Settings;
using Nox.XR.Runtime.Tracking;
using UnityEngine;

namespace Nox.XR.Runtime.Settings {
	/// <summary>
	/// Read-only row under the full-body toggle: how many trackers are connected and how many are
	/// currently tracking. The counts come from <see cref="TrackerWatcher"/>, which also announces
	/// the tracker events as notifications.
	/// </summary>
	public sealed class TrackerStatusSetting : TextHandler {
		public override string[] Path
			=> new[] { "xr", "fbt", "tracker_status" };

		public override int Order => 60000;

		public override bool IsActive
			=> Client.Instance != null;

		public TrackerStatusSetting()
			=> Refresh();

		public override void OnUpdated(IHandler handler)
			=> Refresh();

		protected override GameObject GetPrefab()
			=> Main.CoreAPI.AssetAPI.GetAsset<GameObject>("settings:prefabs/text.prefab");

		private void Refresh()
			=> SetText(
				"settings.entry.xr.fbt.tracker_status.value",
				TrackerWatcher.Connected.ToString(),
				TrackerWatcher.Tracking.ToString()
			);
	}
}
