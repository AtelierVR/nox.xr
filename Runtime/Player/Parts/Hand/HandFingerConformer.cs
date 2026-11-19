using System.Collections.Generic;
using Nox.Avatars.Hand;
using Nox.Interactions;
using UnityEngine;

namespace Nox.XR.Player {
	/// <summary>
	/// Blends the fingers of an avatar hand onto the shape of a grabbable, the way AutoHand does it: each
	/// finger is bent step by step from its open pose to its closed pose, and the first bend whose tip
	/// sphere overlaps the object wins. No analytic maths, only a physical sweep - that is what makes a
	/// hand wrap around an edge while staying flat on a panel.
	/// <para>
	/// The poses come from <see cref="Nox.Avatars.Hand.IFinger"/> (<c>Opened</c> / <c>Closed</c>, and the
	/// pinched pair for a pinch grab), so it works on any avatar hand exposed through <see cref="IHand"/>.
	/// The bend is applied in <c>LateUpdate</c>, after the avatar animation wrote the bones, so the pose
	/// stays on screen while the object is held.
	/// </para>
	/// </summary>
	public sealed class HandFingerConformer : IGrabConformer {
		private const int JointCount = 4;

		private sealed class Entry {
			public IFinger finger;
			public Transform[] joints;
			public Quaternion[] open;
			public Quaternion[] closed;
			public Quaternion[] pinchOpen;
			public Quaternion[] pinchClosed;
			public float bend;
			public float target;
			public bool shaped;
		}

		private readonly List<Entry> _entries = new();
		private readonly Collider[] _overlap = new Collider[4];

		private bool _pinch;

		/// <summary>Whether every finger is back to its open pose.</summary>
		public bool IsRelaxed { get; private set; } = true;

		/// <summary>Number of fingers that can conform (0 when the hand carries no usable finger).</summary>
		public int Count
			=> _entries.Count;

		public HandFingerConformer(IHand hand)
			=> Configure(hand);

		/// <summary>Rebuilds the finger table from a hand (the avatar changed).</summary>
		public void Configure(IHand hand) {
			_entries.Clear();
			IsRelaxed = true;

			var fingers = hand?.Fingers;
			if (fingers == null)
				return;

			for (var i = 0; i < fingers.Length; i++) {
				var entry = Build(fingers[i]);
				if (entry != null)
					_entries.Add(entry);
			}
		}

		private static Entry Build(IFinger finger) {
			if (finger == null)
				return null;

			var joints = new[] { finger.Proximal, finger.Intermediate, finger.Distal, finger.Tip };
			if (joints[0] == null && joints[1] == null && joints[2] == null)
				return null;

			var open = PoseValues(finger, FingerCurl.Opened);
			var closed = PoseValues(finger, FingerCurl.Closed);

			if (open == null) {
				open = new Quaternion[JointCount];
				for (var i = 0; i < JointCount; i++)
					open[i] = joints[i] ? joints[i].localRotation : Quaternion.identity;
			}

			if (closed == null)
				closed = (Quaternion[])open.Clone();

			return new Entry {
				finger = finger,
				joints = joints,
				open = open,
				closed = closed,
				pinchOpen = PoseValues(finger, FingerCurl.PinchedOpened),
				pinchClosed = PoseValues(finger, FingerCurl.PinchedClosed)
			};
		}

		private static Quaternion[] PoseValues(IFinger finger, FingerCurl curl) {
			var poses = finger?.Poses;
			if (poses == null)
				return null;

			for (var i = 0; i < poses.Length; i++) {
				var pose = poses[i];
				if (pose == null || pose.Curl != curl)
					continue;

				var values = pose.Values;
				if (values == null || values.Length < JointCount)
					return null;

				var copy = new Quaternion[JointCount];
				System.Array.Copy(values, copy, JointCount);
				return copy;
			}

			return null;
		}

		/// <summary>
		/// Wraps every finger around the grabbable. The object is isolated by layer: only its own colliders
		/// are probed, so a finger never stops on the palm or on a neighbouring prop.
		/// </summary>
		public void Conform(IGrabbable grabbable, in RaycastHit contact, int steps)
			=> TryConform(grabbable, contact, steps, Vector3.zero);

		/// <summary>
		/// Shapes the fingers around the grabbable and reports whether the sweep found the object.
		/// <para>
		/// A finger whose sweep finds nothing keeps its open pose: an object that is not around the fingers
		/// (still flying to the hand, or held out of their reach) must not close them into a fist. The caller
		/// retries instead, and the return value says whether any finger could be shaped.
		/// </para>
		/// </summary>
		public bool TryConform(IGrabbable grabbable, in RaycastHit contact, int steps)
			=> TryConform(grabbable, contact, steps, Vector3.zero);

		public bool TryConform(IGrabbable grabbable, in RaycastHit contact, int steps, Vector3 objectOffset) {
			if (grabbable == null || _entries.Count == 0)
				return false;

			_pinch = grabbable is IGrabAttachment attachment
				&& attachment.PoseType == GrabPoseType.Pinch
				&& _entries.Exists(e => e.pinchOpen != null && e.pinchClosed != null);

			var mask = MaskOf(grabbable);
			if (mask == 0)
				mask = InteractionConstants.GrabbableMask;

			var shaped = false;

			for (var i = 0; i < _entries.Count; i++) {
				var entry = _entries[i];
				var target = Sweep(entry, mask, steps, objectOffset);

				entry.shaped = target >= 0f;
				entry.target = entry.shaped ? target : 0f;

				if (entry.shaped)
					shaped = true;
			}

			ApplyPose();
			IsRelaxed = !shaped;
			return shaped;
		}

		/// <summary>
		/// Forgets the current shape without touching the bones: the fingers keep the pose the animation or the
		/// skeleton gives them until the object is in the hand and a sweep shapes them.
		/// </summary>
		public void Reset() {
			for (var i = 0; i < _entries.Count; i++) {
				var entry = _entries[i];
				entry.target = 0f;
				entry.bend = 0f;
				entry.shaped = false;
			}
		}

		/// <summary>
		/// Puts every finger straight on the bend the last sweep found: the hand takes the pose on the frame the
		/// object is in place, with no easing in between.
		/// </summary>
		public void Snap() {
			for (var i = 0; i < _entries.Count; i++)
				_entries[i].bend = _entries[i].target;

			ApplyPose();
		}

		/// <summary>Hands the fingers back to the animation at the requested speed (see <see cref="Tick"/>).</summary>
		public void Relax(float duration)
			=> IsRelaxed = false;

		/// <summary>
		/// Re-applies the shape of the fingers that are on the object: called every <c>LateUpdate</c> while
		/// holding, after the avatar animation wrote the finger bones. Fingers that found nothing keep the
		/// pose of the animation.
		/// </summary>
		public void ApplyPose() {
			for (var i = 0; i < _entries.Count; i++) {
				var entry = _entries[i];
				if (entry.shaped)
					Apply(entry, entry.bend);
			}
		}

		/// <summary>Opens every shaped finger by <paramref name="delta"/> (a 0..1 fraction of the full bend).</summary>
		public void Tick(float delta) {
			var moving = false;

			for (var i = 0; i < _entries.Count; i++) {
				var entry = _entries[i];
				entry.target = 0f;

				if (entry.bend <= 0f)
					continue;

				entry.bend = Mathf.MoveTowards(entry.bend, 0f, delta);
				moving = true;

				if (entry.bend <= 0f)
					entry.shaped = false;
			}

			ApplyPose();
			IsRelaxed = !moving;
		}

		private static int MaskOf(IGrabbable grabbable) {
			var mask = 0;

			if (grabbable is IGrabColliders holder && holder.GrabColliders != null) {
				var colliders = holder.GrabColliders;
				for (var i = 0; i < colliders.Count; i++) {
					var collider = colliders[i];
					if (collider)
						mask |= 1 << collider.gameObject.layer;
				}
			}

			if (mask == 0 && grabbable.Target)
				mask = 1 << grabbable.Target.gameObject.layer;

			return mask;
		}

		private float Sweep(Entry entry, int mask, int steps, Vector3 objectOffset) {
			steps = Mathf.Max(steps, 4);

			var coarse = 5f / steps;
			var hit = -1f;

			for (var value = 0f; value <= 1f; value += coarse) {
				if (!Overlaps(entry, mask, value, objectOffset))
					continue;

				hit = value;
				break;
			}

			if (hit < 0f)
				return -1f;

			if (hit <= 0f) {
				// The tip already overlaps at the open pose: either the object is not where it will be held
				// (it is still settling into the hand) or it rests against the open fingers. Neither tells
				// how far a finger should bend, so nothing is shaped and the caller measures again.
				return -1f;
			}

			var from = Mathf.Max(0f, hit - coarse);
			var fine = 1f / steps;

			for (var value = from; value <= hit; value += fine) {
				if (Overlaps(entry, mask, value, objectOffset))
					return value;
			}

			return hit;
		}

		private bool Overlaps(Entry entry, int mask, float value, Vector3 objectOffset) {
			Apply(entry, value);
			Physics.SyncTransforms();

			var tip = entry.joints[3] ? entry.joints[3] : entry.joints[2];
			if (!tip)
				return false;

			var radius = entry.finger.TipRadius > 0f
				? entry.finger.TipRadius
				: InteractionConstants.DefaultTipRadius;

			return Physics.OverlapSphereNonAlloc(tip.position - objectOffset, radius, _overlap, mask, QueryTriggerInteraction.Ignore) > 0;
		}

		private void Apply(Entry entry, float value) {
			var open = _pinch ? entry.pinchOpen ?? entry.open : entry.open;
			var closed = _pinch ? entry.pinchClosed ?? entry.closed : entry.closed;

			for (var i = 0; i < JointCount; i++) {
				var joint = entry.joints[i];
				if (joint)
					joint.localRotation = Quaternion.Slerp(open[i], closed[i], value);
			}
		}
	}
}
