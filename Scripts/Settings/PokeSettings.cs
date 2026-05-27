using Nox.CCK.Events;
using Nox.CCK.Utils;
using UnityEngine;

namespace Nox.XR.Settings {
	public static class PokeSettings {
		private const string EnabledKey = "settings.xr.poke.enabled";
		private const string DisablePercentKey = "settings.xr.poke.disable_percent";
		public const float DefaultDisablePercent = 0.8f;

		public static readonly NoxEvent<bool>  Changed               = new();
		public static readonly NoxEvent<float> DisablePercentChanged = new();

		public static bool Enabled {
			get => Config.Load().Get(EnabledKey, true);
			set {
				var config = Config.Load();
				var oldValue = config.Get(EnabledKey, true);
				if (oldValue == value) return;
				config.Set(EnabledKey, value);
				config.Save();
				Changed.Invoke(value);
			}
		}

		public static float DisablePokePercent {
			get => Config.Load().Get(DisablePercentKey, DefaultDisablePercent);
			set {
				var config = Config.Load();
				var oldValue = config.Get(DisablePercentKey, DefaultDisablePercent);
				var v = Mathf.Clamp(value, 0f, 1f);
				if (Mathf.Approximately(oldValue, v)) return;
				config.Set(DisablePercentKey, v);
				config.Save();
				DisablePercentChanged.Invoke(v);
			}
		}
	}
}
