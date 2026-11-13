using Nox.CCK.Entities;

namespace Nox.XR.Trackers {
	/// <summary>Slot helpers for <see cref="ITracker"/>, applicable to custom rig ids too.</summary>
	public static class TrackerExtensions {
		/// <summary>Rig slot as a known <see cref="EntityRig"/> value, or <see cref="EntityRig.None"/> for a custom id.</summary>
		public static EntityRig ToEntityRig(this ITracker tracker)
			=> tracker.Rig.ToEntityRig();

		/// <summary>What a hand driven by this tracker can follow (see <see cref="HandTrackingMode"/>).</summary>
		public static HandTrackingMode ToHandTrackingMode(this ITracker tracker)
			=> tracker != null
				? tracker.Features.ToHandTrackingMode()
				: HandTrackingMode.Controller;

		/// <summary>
		/// Reads the features as a hand tracking mode: a skeleton with buttons is a controller that also
		/// tracks the hand (UDCAP, Index controller + hand tracking), a bare skeleton is a tracked hand.
		/// </summary>
		public static HandTrackingMode ToHandTrackingMode(this TrackerFeatures features) {
			var skeleton = features.HasFlag(TrackerFeatures.Skeleton);
			var buttons = features.HasFlag(TrackerFeatures.Buttons);

			if (skeleton)
				return buttons
					? HandTrackingMode.ControllerAndHand
					: HandTrackingMode.Hand;

			return HandTrackingMode.Controller;
		}

		/// <summary>Whether this tracker drives the HMD.</summary>
		public static bool IsHead(this ITracker tracker)
			=> tracker.Rig == EntityRig.Head.ToIndex();

		/// <summary>Whether this tracker drives either hand.</summary>
		public static bool IsHand(this ITracker tracker)
			=> tracker.IsLeftHand() || tracker.IsRightHand();

		/// <summary>Whether this tracker drives the left hand.</summary>
		public static bool IsLeftHand(this ITracker tracker)
			=> tracker.Rig == EntityRig.LeftHand.ToIndex();

		/// <summary>Whether this tracker drives the right hand.</summary>
		public static bool IsRightHand(this ITracker tracker)
			=> tracker.Rig == EntityRig.RightHand.ToIndex();

		/// <summary>Whether this tracker drives either foot.</summary>
		public static bool IsFoot(this ITracker tracker)
			=> tracker.IsLeftFoot() || tracker.IsRightFoot();

		/// <summary>Whether this tracker drives either foot (alias of <see cref="IsFoot"/>).</summary>
		public static bool IsFeet(this ITracker tracker)
			=> tracker.IsFoot();

		/// <summary>Whether this tracker drives the left foot.</summary>
		public static bool IsLeftFoot(this ITracker tracker)
			=> tracker.Rig == EntityRig.LeftFoot.ToIndex();

		/// <summary>Whether this tracker drives the right foot.</summary>
		public static bool IsRightFoot(this ITracker tracker)
			=> tracker.Rig == EntityRig.RightFoot.ToIndex();

		/// <summary>Whether this tracker drives a known body or hand slot.</summary>
		public static bool IsBody(this ITracker tracker)
			=> tracker.ToEntityRig() != EntityRig.None;

		/// <summary>Whether this tracker uses a custom rig id (prop tracker, unassigned device...).</summary>
		public static bool IsCustom(this ITracker tracker)
			=> tracker.ToEntityRig() == EntityRig.None;
	}
}
