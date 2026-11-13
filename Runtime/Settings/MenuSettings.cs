using Nox.CCK.Events;
using Nox.CCK.Utils;
using UnityEngine;

namespace Nox.XR.Runtime.Settings {
	/// <summary>What the player menu is attached to once it is placed.</summary>
	public enum XRMenuAnchor {
		/// <summary>Stays in front of the view: it follows the head, always readable.</summary>
		Head,

		/// <summary>Keeps its offset to the player: it travels with them but not with the head.</summary>
		Player,

		/// <summary>Stays where it was put, and only comes back past the recall distance.</summary>
		World
	}

	/// <summary>What happens when the player menu is left farther than the recall distance.</summary>
	public enum XRMenuRecall {
		/// <summary>Hidden until the player comes back within reach.</summary>
		Despawn,

		/// <summary>Brought back in front of the view.</summary>
		Teleport,

		/// <summary>Dragged along at the edge of the reach circle, like on a leash.</summary>
		Clamp
	}

	/// <summary>
	/// Menu options persisted in the config (how far the player menu may be left behind, what it is
	/// anchored to, and what happens when it is out of reach). <see cref="MaxMenuDistanceSetting"/>,
	/// <see cref="MenuAnchorSetting"/> and <see cref="MenuRecallSetting"/> are UI adapters only; the values, the
	/// persistence and the events live here.
	/// </summary>
	public static class MenuSettings {
		private const string MaxDistanceKey = "settings.xr.menu.max_distance";
		private const string AnchorKey      = "settings.xr.menu.anchor";
		private const string RecallKey      = "settings.xr.menu.recall";

		/// <summary>Distance after which the panel is brought back in front of the view, in metres.</summary>
		public const float DefaultMaxDistance = 5f;

		/// <summary>Smallest real limit: a metre, below which the menu would always be recalled.</summary>
		public const float MinMaxDistance = 1f;

		public const float MaxMaxDistance = 100f;

		/// <summary>Stored value meaning "no limit": the panel is never brought back.</summary>
		public const float NoLimit = 0f;

		public static readonly NoxEvent<float> MaxDistanceChanged = new();
		public static readonly NoxEvent<XRMenuAnchor> AnchorChanged = new();
		public static readonly NoxEvent<XRMenuRecall> RecallChanged = new();

		/// <summary>
		/// How far, in metres (times the player scale), the panel may stay away from the player.
		/// <see cref="NoLimit"/> turns the recall off entirely.
		/// </summary>
		public static float MaxDistance {
			get => Normalize(Config.Load().Get(MaxDistanceKey, DefaultMaxDistance));
			set {
				var config   = Config.Load();
				var oldValue = Normalize(config.Get(MaxDistanceKey, DefaultMaxDistance));
				var newValue = Normalize(value);
				if (Mathf.Approximately(oldValue, newValue))
					return;
				config.Set(MaxDistanceKey, newValue);
				config.Save();
				MaxDistanceChanged.Invoke(newValue);
			}
		}

		/// <summary>Whether a limit is applied at all (<see cref="NoLimit"/> disables it).</summary>
		public static bool HasMaxDistance
			=> MaxDistance > NoLimit;

		/// <summary>What the panel is anchored to while it is shown.</summary>
		public static XRMenuAnchor Anchor {
			get => Parse(Config.Load().Get(AnchorKey, Key(XRMenuAnchor.World)));
			set {
				var config   = Config.Load();
				var oldValue = config.Get(AnchorKey, Key(XRMenuAnchor.World));
				var newValue = Key(value);
				if (oldValue == newValue)
					return;
				config.Set(AnchorKey, newValue);
				config.Save();
				AnchorChanged.Invoke(value);
			}
		}

		/// <summary>String stored in the config (<c>"head"</c> / <c>"player"</c> / <c>"world"</c>).</summary>
		public static string Key(XRMenuAnchor anchor)
			=> anchor switch {
				XRMenuAnchor.Head   => "head",
				XRMenuAnchor.Player => "player",
				_                   => "world"
			};

		/// <summary>Inverse of <see cref="Key"/>; any unknown value falls back to <see cref="XRMenuAnchor.World"/>.</summary>
		public static XRMenuAnchor Parse(string value) {
			if (string.Equals(value, "head", System.StringComparison.OrdinalIgnoreCase))
				return XRMenuAnchor.Head;
			if (string.Equals(value, "player", System.StringComparison.OrdinalIgnoreCase))
				return XRMenuAnchor.Player;
			return XRMenuAnchor.World;
		}

		/// <summary>What happens once the panel is out of reach.</summary>
		public static XRMenuRecall Recall {
			get => ParseRecall(Config.Load().Get(RecallKey, Key(XRMenuRecall.Despawn)));
			set {
				var config   = Config.Load();
				var oldValue = config.Get(RecallKey, Key(XRMenuRecall.Despawn));
				var newValue = Key(value);
				if (oldValue == newValue)
					return;
				config.Set(RecallKey, newValue);
				config.Save();
				RecallChanged.Invoke(value);
			}
		}

		/// <summary>String stored in the config (<c>"despawn"</c> / <c>"teleport"</c> / <c>"clamp"</c>).</summary>
		public static string Key(XRMenuRecall recall)
			=> recall switch {
				XRMenuRecall.Teleport => "teleport",
				XRMenuRecall.Clamp    => "clamp",
				_                     => "despawn"
			};

		/// <summary>Inverse of <see cref="Key(XRMenuRecall)"/>; unknown values fall back to <see cref="XRMenuRecall.Despawn"/>.</summary>
		public static XRMenuRecall ParseRecall(string value) {
			if (string.Equals(value, "teleport", System.StringComparison.OrdinalIgnoreCase))
				return XRMenuRecall.Teleport;
			if (string.Equals(value, "clamp", System.StringComparison.OrdinalIgnoreCase))
				return XRMenuRecall.Clamp;
			return XRMenuRecall.Despawn;
		}

		/// <summary>
		/// Keeps <see cref="NoLimit"/> as the "no limit" marker, and clamps every other value between
		/// <see cref="MinMaxDistance"/> and <see cref="MaxMaxDistance"/>.
		/// </summary>
		public static float Normalize(float value)
			=> value <= NoLimit ? NoLimit : Mathf.Clamp(value, MinMaxDistance, MaxMaxDistance);
	}
}
