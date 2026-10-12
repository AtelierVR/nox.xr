using Nox.CCK.Events;
using Nox.CCK.Utils;
using UnityEngine;

namespace Nox.XR.Runtime.Settings {
	public static class PokeSettings {
		private const string EnabledKey = "settings.xr.poke.enabled";
		private const string DisablePercentKey = "settings.xr.poke.disable_percent";
		private const string DisableNearFarKey = "settings.xr.poke.disable_near_far";
		private const string OpenHandIndexKey = "settings.xr.poke.open_hand_index";
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

		/// <summary>
		/// Cuts a hand's near/far ray while one of its pokes has a <b>target in range</b>, so the ray and
		/// the poke do not compete for the same surface. Off keeps the ray active while poking.
		/// </summary>
		public static bool DisableNearFar {
			get => Config.Load().Get(DisableNearFarKey, true);
			set {
				var config = Config.Load();
				var oldValue = config.Get(DisableNearFarKey, true);
				if (oldValue == value) return;
				config.Set(DisableNearFarKey, value);
				config.Save();
			}
		}

		/// <summary>
		/// Keeps the <b>index</b> poke while the hand is <b>fully open</b>. Off (the default) cuts every
		/// poke of an open hand, so pointing with an extended finger never pokes by itself; on restores
		/// the "point to poke" behaviour.
		/// </summary>
		public static bool OpenHandIndex {
			get => Config.Load().Get(OpenHandIndexKey, false);
			set {
				var config = Config.Load();
				var oldValue = config.Get(OpenHandIndexKey, false);
				if (oldValue == value) return;
				config.Set(OpenHandIndexKey, value);
				config.Save();
			}
		}
	}
}
