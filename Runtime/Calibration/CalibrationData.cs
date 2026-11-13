using System;
using Newtonsoft.Json;
using Nox.CCK.Entities;

namespace Nox.XR.Calibration {
	/// <summary>All bindings produced by a calibration session.</summary>
	[Serializable]
	public class CalibrationData {
		[JsonProperty("bindings")]
		public TrackerBinding[] Bindings = Array.Empty<TrackerBinding>();

		[JsonIgnore]
		public bool IsEmpty
			=> Bindings == null || Bindings.Length == 0;

		/// <summary>Calibration entry driving a rig slot, including its position/rotation offsets.</summary>
		public bool TryGetBinding(EntityRig rig, out TrackerBinding binding) {
			if (rig != EntityRig.None && Bindings != null)
				for (var i = 0; i < Bindings.Length; i++) {
					var candidate = Bindings[i];
					if (candidate == null || candidate.Rig.ToEntityRig() != rig)
						continue;

					binding = candidate;
					return true;
				}

			binding = null;
			return false;
		}

		/// <summary>Rig slot driven by a tracker serial, according to the stored calibration.</summary>
		public bool TryGetRig(string trackerId, out EntityRig rig) {
			if (!string.IsNullOrEmpty(trackerId) && Bindings != null)
				for (var i = 0; i < Bindings.Length; i++) {
					var binding = Bindings[i];
					if (binding == null || !string.Equals(binding.TrackerId, trackerId, StringComparison.OrdinalIgnoreCase))
						continue;

					var found = binding.Rig.ToEntityRig();
					if (found == EntityRig.None)
						continue;

					rig = found;
					return true;
				}

			rig = EntityRig.None;
			return false;
		}

		/// <summary>Tracker serial driving a rig slot, according to the stored calibration.</summary>
		public bool TryGetTrackerId(EntityRig rig, out string trackerId) {
			if (rig != EntityRig.None && Bindings != null)
				for (var i = 0; i < Bindings.Length; i++) {
					var binding = Bindings[i];
					if (binding == null || binding.Rig.ToEntityRig() != rig)
						continue;

					trackerId = binding.TrackerId;
					return !string.IsNullOrEmpty(trackerId);
				}

			trackerId = null;
			return false;
		}
	}
}
