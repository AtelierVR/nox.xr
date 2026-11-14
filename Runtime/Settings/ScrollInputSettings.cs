using System;
using Nox.CCK.Events;
using Nox.CCK.Utils;

namespace Nox.XR.Runtime.Settings {
	/// <summary>What the joystick does while a hand points at a scrollable list.</summary>
	public enum XRScrollInput {
		/// <summary>The list scrolls and the player stays put (the default).</summary>
		UiOnly,

		/// <summary>Both happen: the list scrolls and the player moves.</summary>
		Both,

		/// <summary>The player moves and the list ignores the stick.</summary>
		MovementOnly
	}

	/// <summary>
	/// Choice persisted in the config for a stick used over a scrollable UI.
	/// <see cref="ScrollInputModeSetting"/> is the UI adapter only.
	/// </summary>
	public static class ScrollInputSettings {
		private const string MODE_KEY = "settings.xr.scroll.mode";

		public const XRScrollInput DefaultMode = XRScrollInput.UiOnly;

		public static readonly NoxEvent<XRScrollInput> ModeChanged = new();

		public static XRScrollInput Mode {
			get => Parse(Config.Load().Get(MODE_KEY, Key(DefaultMode)));
			set {
				var config   = Config.Load();
				var oldValue = config.Get(MODE_KEY, Key(DefaultMode));
				var newValue = Key(value);

				if (oldValue == newValue)
					return;

				config.Set(MODE_KEY, newValue);
				config.Save();
				ModeChanged.Invoke(value);
			}
		}

		/// <summary>String stored in the config.</summary>
		public static string Key(XRScrollInput mode)
			=> mode switch {
				XRScrollInput.Both         => "both",
				XRScrollInput.MovementOnly => "movement",
				_                          => "ui"
			};

		/// <summary>Inverse of <see cref="Key"/>; any unknown value falls back to the default.</summary>
		public static XRScrollInput Parse(string value) {
			if (string.Equals(value, "both", StringComparison.OrdinalIgnoreCase))
				return XRScrollInput.Both;

			if (string.Equals(value, "movement", StringComparison.OrdinalIgnoreCase))
				return XRScrollInput.MovementOnly;

			return XRScrollInput.UiOnly;
		}
	}
}
