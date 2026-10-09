using Cysharp.Threading.Tasks;
using Nox.CCK.Settings;
using Nox.Settings;
using Nox.XR.Runtime.Loaders;
using UnityEngine;

namespace Nox.XR.Runtime.Settings {
	/// <summary>
	/// Button to start or stop XR at runtime.
	/// </summary>
	public sealed class StartVRSetting : ButtonHandler {
		public override string[] Path
			=> new[] { "xr", "general", "start_vr" };

		public override int Order => 60000;

		public override bool IsActive => true;

		/// <summary>
		/// Le texte dépend de <see cref="XRLoaderManager.IsRunning"/>, qui change aussi sans passer par ce
		/// réglage (démarrage automatique en arrivant en jeu, sortie de VR par le bouton, entrée/sortie depuis
		/// les mods) : on le rafraîchit donc à chaque notification, quel que soit le réglage qui l'a déclenchée.
		/// </summary>
		public override void OnUpdated(IHandler handler) {
			SetInteractable(EnableXRSetting.Value);
			RefreshLabel();
		}

		public StartVRSetting() {
			SetLabel("settings.entry.xr.general.start_vr.label");
			SetInteractable(EnableXRSetting.Value);
			RefreshLabel();
		}

		private void RefreshLabel() 
			=> SetButtonText(XRLoaderManager.IsRunning
				? "settings.entry.xr.general.start_vr.stop"
				: "settings.entry.xr.general.start_vr.start");

		protected override GameObject GetPrefab()
			=> Main.CoreAPI.AssetAPI.GetAsset<GameObject>("settings:prefabs/button.prefab");

		public override void OnClick(IContext context)
			=> OnClickAsync().Forget();

		private async UniTask OnClickAsync() {
			if (Client.Instance == null) return;

			// Le même bouton entre et sort de la XR. La sortie doit réellement quitter la
			// VR : arrêt du loader + retrait du proxy (StopLoader seul laisserait le proxy
			// XR courant avec un tracking mort).
			if (XRLoaderManager.IsRunning)
				await Client.Instance.Quit();
			else
				await Client.Instance.Enter();

			RefreshLabel();
		}
	}
}
