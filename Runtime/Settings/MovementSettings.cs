using Nox.CCK.Events;
using Nox.CCK.Utils;
using UnityEngine;

namespace Nox.XR.Runtime.Settings {
	/// <summary>Player rotation mode in VR.</summary>
	public enum XRTurnMode {
		/// <summary>Fixed-angle rotation per controller flick.</summary>
		Snap,

		/// <summary>Continuous rotation while the stick is tilted.</summary>
		Smooth
	}

	/// <summary>
	/// XR movement options persisted in the config (turn mode, snap angle, smooth turn speed).
	/// <see cref="TurnModeSetting"/>, <see cref="SnapTurnAngleSetting"/> and <see cref="SmoothTurnSpeedSetting"/>
	/// are UI adapters only; the value, the persistence and the events live here.
	/// </summary>
	public static class MovementSettings {
		private const string TurnModeKey         = "settings.xr.movement.turn_mode";
		private const string SnapTurnAngleKey    = "settings.xr.movement.snap_turn_angle";
		private const string SmoothTurnSpeedKey  = "settings.xr.movement.smooth_turn_speed";

		public const float DefaultSnapTurnAngle   = 30f;
		public const float DefaultSmoothTurnSpeed = 180f;

		public const float MinSnapTurnAngle   = 5f;
		public const float MaxSnapTurnAngle   = 180f;
		public const float MinSmoothTurnSpeed = 10f;
		public const float MaxSmoothTurnSpeed = 720f;

		public static readonly NoxEvent<XRTurnMode> TurnModeChanged              = new();
		public static readonly NoxEvent<float>      SnapTurnAngleChanged         = new();
		public static readonly NoxEvent<float>      SmoothTurnSpeedChanged       = new();

		public static XRTurnMode TurnMode {
			get => Parse(Config.Load().Get(TurnModeKey, Key(XRTurnMode.Snap)));
			set {
				var config   = Config.Load();
				var oldValue = config.Get(TurnModeKey, Key(XRTurnMode.Snap));
				var newValue = Key(value);
				if (oldValue == newValue)
					return;
				config.Set(TurnModeKey, newValue);
				config.Save();
				TurnModeChanged.Invoke(value);
			}
		}

		public static float SnapTurnAngle {
			get => Config.Load().Get(SnapTurnAngleKey, DefaultSnapTurnAngle);
			set {
				var config   = Config.Load();
				var oldValue = config.Get(SnapTurnAngleKey, DefaultSnapTurnAngle);
				var newValue = Mathf.Clamp(value, MinSnapTurnAngle, MaxSnapTurnAngle);
				if (Mathf.Approximately(oldValue, newValue))
					return;
				config.Set(SnapTurnAngleKey, newValue);
				config.Save();
				SnapTurnAngleChanged.Invoke(newValue);
			}
		}

		public static float SmoothTurnSpeed {
			get => Config.Load().Get(SmoothTurnSpeedKey, DefaultSmoothTurnSpeed);
			set {
				var config   = Config.Load();
				var oldValue = config.Get(SmoothTurnSpeedKey, DefaultSmoothTurnSpeed);
				var newValue = Mathf.Clamp(value, MinSmoothTurnSpeed, MaxSmoothTurnSpeed);
				if (Mathf.Approximately(oldValue, newValue))
					return;
				config.Set(SmoothTurnSpeedKey, newValue);
				config.Save();
				SmoothTurnSpeedChanged.Invoke(newValue);
			}
		}

		/// <summary>String stored in the config (<c>"snap"</c> / <c>"smooth"</c>).</summary>
		public static string Key(XRTurnMode mode)
			=> mode == XRTurnMode.Smooth ? "smooth" : "snap";

		/// <summary>Inverse of <see cref="Key"/>; any unknown value falls back to <see cref="XRTurnMode.Snap"/>.</summary>
		public static XRTurnMode Parse(string value)
			=> string.Equals(value, "smooth", System.StringComparison.OrdinalIgnoreCase)
				? XRTurnMode.Smooth
				: XRTurnMode.Snap;
	}
}
