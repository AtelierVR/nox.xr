using Cysharp.Threading.Tasks;
using Nox.CCK.Settings;
using Nox.Settings;
using Nox.UI;
using UnityEngine;

namespace Nox.XR.Settings {
	/// <summary>
	/// Button to start or stop XR at runtime.
	/// </summary>
	public sealed class StartVRSetting : ButtonHandler {
		public override string[] GetPath()
			=> new[] { "xr", "general", "start_vr" };

		public override int GetOrder() => 0;

		public override bool IsActive() => true;

		public override void OnUpdated(IHandler handler) {
			if (handler == this)
				RefreshLabel();
		}

		public StartVRSetting() {
			SetLabel($"settings.entry.xr.general.start_vr.label");
			RefreshLabel();
		}

		private void RefreshLabel() {
			var isInit = Client.Instance?.IsXRInitialized() ?? false;
			SetButtonText(isInit
				? "settings.entry.xr.general.start_vr.stop"
				: "settings.entry.xr.general.start_vr.start");
		}

		protected override GameObject GetPrefab()
			=> Main.CoreAPI.AssetAPI.GetAsset<GameObject>("settings:prefabs/button.prefab");

		protected override void OnClick(IMenu menu)
			=> OnClickAsync().Forget();

		private async UniTask OnClickAsync() {
			if (Client.Instance == null) return;

			if (Client.Instance.IsXRInitialized())
				Client.Instance.StopLoader();
			else
				await Client.Instance.StartLoader();

			RefreshLabel();
		}
	}
}
