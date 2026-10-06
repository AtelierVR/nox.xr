using System;
using Nox.CCK.Utils;
using UnityEngine;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.XR.Runtime.FullBody {
	/// <summary>
	/// Persisted link between one physical tracker and one avatar bone, capturing the pose delta
	/// measured during calibration.
	/// <para>
	/// The offsets are expressed in the <b>tracker's local space</b> so that moving or rotating the
	/// tracking rig naturally follows them. <see cref="Scale"/> is the avatar scale at calibration
	/// time: the position offset is re-scaled by <c>currentScale / Scale</c> when applied, which keeps
	/// the calibration resistant to avatar scaling.
	/// </para>
	/// </summary>
	[Serializable]
	public class FullBodyTrackerBinding {
		/// <summary>Device serial (see <see cref="Nox.XR.Trackers.TrackerPose.Id"/>) this binding is attached to.</summary>
		public string TrackerId;

		/// <summary>Target bone, stored as an <see cref="HumanBodyBones"/> value.</summary>
		public int Bone;

		/// <summary>Bone pose relative to the tracker, in tracker space.</summary>
		public Vector3 OffsetPosition;

		/// <summary>Bone rotation relative to the tracker.</summary>
		public Quaternion OffsetRotation = Quaternion.identity;

		/// <summary>Avatar scale at the moment of calibration.</summary>
		public float Scale = 1f;

		public HumanBodyBones BodyBone {
			get => (HumanBodyBones)Bone;
			set => Bone = (int)value;
		}
	}

	/// <summary>All bindings produced by a calibration session.</summary>
	[Serializable]
	public class FullBodyCalibrationData {
		public FullBodyTrackerBinding[] Bindings = Array.Empty<FullBodyTrackerBinding>();

		public bool IsEmpty
			=> Bindings == null || Bindings.Length == 0;
	}

	/// <summary>Loads/saves <see cref="FullBodyCalibrationData"/> through the Nox config.</summary>
	public static class FullBodyCalibrationStore {
		private const string ConfigKey = "settings.xr.fbt_calibration";

		public static FullBodyCalibrationData Load() {
			var json = Config.Load().Get(ConfigKey, string.Empty);
			if (string.IsNullOrEmpty(json))
				return new FullBodyCalibrationData();

			try {
				return JsonUtility.FromJson<FullBodyCalibrationData>(json) ?? new FullBodyCalibrationData();
			} catch (Exception e) {
				Logger.LogWarning($"Failed to parse the stored full-body calibration, ignoring it: {e.Message}", tag: nameof(FullBodyCalibrationStore));
				return new FullBodyCalibrationData();
			}
		}

		public static void Save(FullBodyCalibrationData data) {
			var config = Config.Load();
			config.Set(ConfigKey, JsonUtility.ToJson(data ?? new FullBodyCalibrationData()));
			config.Save();
		}

		public static void Clear() {
			var config = Config.Load();
			config.Set(ConfigKey, string.Empty);
			config.Save();
		}
	}
}
