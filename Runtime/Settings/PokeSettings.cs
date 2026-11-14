using Nox.CCK.Events;
using Nox.CCK.Utils;
using UnityEngine;

namespace Nox.XR.Runtime.Settings {
	public static class PokeSettings {
		private const string ENABLED_KEY = "settings.xr.poke.enabled";
		private const string DISABLE_PERCENT_KEY = "settings.xr.poke.disable_percent";
		private const string DISABLE_NEAR_FAR_KEY = "settings.xr.poke.disable_near_far";
		private const string OPEN_HAND_INDEX_KEY = "settings.xr.poke.open_hand_index";
		private const string HOVER_INDICATOR_KEY = "settings.xr.poke.hover_indicator";
		public const float DEFAULT_DISABLE_PERCENT = 0.8f;

		/// <summary>
		/// Margin of the finger curl threshold: a finger has to unfold past it before the hand is considered
		/// open again, so a poke is never cut by a finger drifting around the threshold mid-press.
		/// </summary>
		public const float BEND_HYSTERESIS = 0.15f;

		public static readonly NoxEvent<bool>  Changed               = new();
		public static readonly NoxEvent<float> DisablePercentChanged = new();

		public static bool Enabled {
			get => Config.Load().Get(ENABLED_KEY, true);
			set {
				var config = Config.Load();
				var oldValue = config.Get(ENABLED_KEY, true);
				if (oldValue == value) return;
				config.Set(ENABLED_KEY, value);
				config.Save();
				Changed.Invoke(value);
			}
		}

		public static float DisablePokePercent {
			get => Config.Load().Get(DISABLE_PERCENT_KEY, DEFAULT_DISABLE_PERCENT);
			set {
				var config = Config.Load();
				var oldValue = config.Get(DISABLE_PERCENT_KEY, DEFAULT_DISABLE_PERCENT);
				var v = Mathf.Clamp(value, 0f, 1f);
				if (Mathf.Approximately(oldValue, v)) return;
				config.Set(DISABLE_PERCENT_KEY, v);
				config.Save();
				DisablePercentChanged.Invoke(v);
			}
		}

		/// <summary>
		/// Cuts a hand's near/far ray while one of its pokes has a <b>target in range</b>, so the ray and
		/// the poke do not compete for the same surface. Off keeps the ray active while poking.
		/// </summary>
		public static bool DisableNearFar {
			get => Config.Load().Get(DISABLE_NEAR_FAR_KEY, true);
			set {
				var config = Config.Load();
				var oldValue = config.Get(DISABLE_NEAR_FAR_KEY, true);
				if (oldValue == value) return;
				config.Set(DISABLE_NEAR_FAR_KEY, value);
				config.Save();
			}
		}

		/// <summary>
		/// Keeps the <b>index</b> poke while the hand is <b>fully open</b>. Off (the default) cuts every
		/// poke of an open hand, so pointing with an extended finger never pokes by itself; on restores
		/// the "point to poke" behaviour.
		/// </summary>
		public static bool OpenHandIndex {
			get => Config.Load().Get(OPEN_HAND_INDEX_KEY, false);
			set {
				var config = Config.Load();
				var oldValue = config.Get(OPEN_HAND_INDEX_KEY, false);
				if (oldValue == value) return;
				config.Set(OPEN_HAND_INDEX_KEY, value);
				config.Save();
			}
		}

		/// <summary>
		/// Marks the UI element a finger or a ray can touch with a low opacity ring.
		/// </summary>
		public static bool HoverIndicator {
			get => Config.Load().Get(HOVER_INDICATOR_KEY, true);
			set {
				var config = Config.Load();
				var oldValue = config.Get(HOVER_INDICATOR_KEY, true);
				if (oldValue == value) return;
				config.Set(HOVER_INDICATOR_KEY, value);
				config.Save();
			}
		}
	}
}
