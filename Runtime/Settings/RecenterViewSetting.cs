using Nox.CCK.Settings;
using Nox.CCK.XR;
using Nox.Settings;
using UnityEngine;

namespace Nox.XR.Runtime.Settings {
	/// <summary>
	/// "Recenter" button: brings the rig's view back under the player and to the height recommended for the
	/// current avatar (see <see cref="IXRController.ReCenterAndReHeight"/>). Same action as the "Stand up"
	/// widget, available from the settings without a widget on screen.
	/// </summary>
	public sealed class RecenterViewSetting : ButtonHandler {
		public override string[] Path
			=> new[] { "xr", "general", "recenter" };

		/// <summary>Between "Start VR" (0) and the IPD (2): a comfort setting, not a hardware one.</summary>
		public override int Order => 60001;

		public override bool IsActive
			=> Client.ControllerAPI?.Current is IXRController;

		public RecenterViewSetting() {
			SetLabel("settings.entry.xr.general.recenter.label");
			SetButtonText("settings.entry.xr.general.recenter.button");
		}

		protected override GameObject GetPrefab()
			=> Main.CoreAPI.AssetAPI.GetAsset<GameObject>("settings:prefabs/button.prefab");

		public override void OnClick(IContext context) {
			if (Client.ControllerAPI?.Current is IXRController controller)
				controller.ReCenterAndReHeight();
		}
	}
}
