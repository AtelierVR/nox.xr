using Nox.CCK.Events;
using Nox.CCK.Utils;

namespace Nox.XR.Runtime.Settings {
	public static class PreviewPoseSettings {
		private const string ENABLED_KEY = "settings.xr.interaction.preview_pose";

		public static readonly NoxEvent<bool> Changed = new();

		public static bool Enabled {
			get => Config.Load().Get(ENABLED_KEY, false);
			set {
				var config = Config.Load();
				if (config.Get(ENABLED_KEY, false) == value) return;
				config.Set(ENABLED_KEY, value);
				config.Save();
				Changed.Invoke(value);
			}
		}
	}
}
