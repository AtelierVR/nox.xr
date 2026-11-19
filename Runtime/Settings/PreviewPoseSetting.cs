using Nox.CCK.Settings;
using UnityEngine;

namespace Nox.XR.Runtime.Settings {
	/// <summary>
	/// Lets a free hand take the grab preview pose as soon as it aims at a grabbable: the hand moves onto
	/// the object, and its fingers are shaped on it, before the grab input is pressed.
	/// </summary>
	public sealed class PreviewPoseSetting : ToggleHandler {
		public override string[] Path
			=> new[] { "xr", "interaction", "preview_pose" };

		public override int Order => 60004;

		public override bool IsActive
			=> true;

		public PreviewPoseSetting() {
			SetValue(PreviewPoseSettings.Enabled, notify: false);
			SetLabelKey("settings.entry.xr.interaction.preview_pose.label");
		}

		protected override GameObject GetPrefab()
			=> Main.CoreAPI.AssetAPI.GetAsset<GameObject>("settings:prefabs/toggle.prefab");

		protected override void OnValueChanged(bool value)
			=> PreviewPoseSettings.Enabled = value;
	}
}
