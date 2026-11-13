using System;
using Newtonsoft.Json.Linq;
using Nox.CCK.Utils;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.XR.Calibration {
	/// <summary>Loads/saves <see cref="CalibrationData"/> through the Nox config.</summary>
	public static class CalibrationStore {
		private const string ConfigKey = "settings.xr.fbt_calibration";

		public static CalibrationData Load() {
			var token = Config.Load().Get(ConfigKey);
			if (token == null || token.Type != JTokenType.Object)
				return new CalibrationData();

			try {
				return token.ToObject<CalibrationData>() ?? new CalibrationData();
			} catch (Exception e) {
				Logger.LogWarning($"Failed to parse the stored full-body calibration, ignoring it: {e.Message}", tag: nameof(CalibrationStore));
				return new CalibrationData();
			}
		}

		public static void Save(CalibrationData data) {
			var config = Config.Load();
			config.Set(ConfigKey, data ?? new CalibrationData());
			config.Save();
		}

		public static void Clear() {
			var config = Config.Load();
			config.Remove(ConfigKey);
			config.Save();
		}
	}
}
