using Nox.CCK.Entities;
using Nox.CCK.Utils;
using UnityEngine;

namespace Nox.XR.Player {
	/// <summary>
	/// Generic part for a tracker rig that has no specialized part: it only follows the tracked pose
	/// (hips, chest, knees, toes, custom tracker rigs...).
	/// <para>
	/// The rig slot is always given explicitly (a template prefab cannot know it), so this part has no
	/// default rig.
	/// </para>
	/// </summary>
	public class TrackedPart : Part {
		protected override EntityRig DefaultRig => EntityRig.None;

		/// <summary>Instantiate a generic tracked part from its template prefab.</summary>
		/// <param name="template">Tracked part template prefab.</param>
		/// <param name="rig">Rig slot the part occupies.</param>
		/// <param name="parent">Tracking container receiving the instance.</param>
		public static TrackedPart Create(TrackedPart template, EntityRig rig, Transform parent) {
			var part = template.gameObject.Instantiate<TrackedPart>(parent);
			part.Configure($"{rig} Tracker", rig);
			return part;
		}
	}
}

