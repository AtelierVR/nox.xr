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
			if (handler is EnableXRSetting) {
				SetInteractable(EnableXRSetting.Value);
				RefreshLabel();
			} else if (handler == this)
				RefreshLabel();
		}

		public StartVRSetting() {
			SetLabel("settings.entry.xr.general.start_vr.label");
			SetInteractable(EnableXRSetting.Value);
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

		public override void OnClick(IContext context)
			=> OnClickAsync().Forget();

		private async UniTask OnClickAsync() {
			if (Client.Instance == null) return;

			// Le même bouton entre et sort de la XR. La sortie doit réellement quitter la
			// VR : arrêt du loader + retrait du proxy (StopLoader seul laisserait le proxy
			// XR courant avec un tracking mort).
			if (Client.Instance.IsXRInitialized())
				await Client.Instance.QuitXR();
			else
				await Client.Instance.EnterXR();

			RefreshLabel();
		}
	}
}
