using System;
using Newtonsoft.Json;
using Nox.CCK.Convertors;
using UnityEngine;

namespace Nox.XR.Calibration {
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
	public class TrackerBinding {
		/// <summary>Device serial (see <see cref="Nox.XR.Trackers.TrackerPose.Id"/>) this binding is attached to.</summary>
		[JsonProperty("id")]
		public string TrackerId;

		/// <summary>Rig slot driven by the tracker, as an <see cref="Nox.CCK.Entities.EntityRig"/> index.</summary>
		[JsonProperty("rig")]
		public ushort Rig;

		/// <summary>Bone pose relative to the tracker, in tracker space.</summary>
		[JsonProperty("position")]
		[JsonConverter(typeof(Vector3JsonConverter))]
		public Vector3 OffsetPosition;

		/// <summary>Bone rotation relative to the tracker.</summary>
		[JsonProperty("rotation")]
		[JsonConverter(typeof(QuaternionJsonConverter))]
		public Quaternion OffsetRotation = Quaternion.identity;

		/// <summary>
		/// Rotation of the tracker when the offsets were measured. The offsets are turned by the rotation the tracker
		/// went through since, which is stable whatever the attitude the device is mounted at. Left at zero (an
		/// invalid rotation) by a calibration stored before this field existed: those offsets keep their old frame.
		/// </summary>
		[JsonProperty("capture")]
		[JsonConverter(typeof(QuaternionJsonConverter))]
		public Quaternion CaptureRotation;

		/// <summary>Whether the binding carries the tracker pose of its calibration (see <see cref="CaptureRotation"/>).</summary>
		[JsonIgnore]
		public bool HasCapture
			=> CaptureRotation.w != 0f;

		/// <summary>Avatar scale at the moment of calibration.</summary>
		[JsonProperty("scale")]
		public float Scale = 1f;
	}
}
