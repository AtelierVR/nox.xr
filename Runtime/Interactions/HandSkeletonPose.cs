using Nox.Avatars.Hand;
using Nox.CCK.Entities;
using Nox.Interactions;
using Nox.XR.Player;
using Nox.XR.Runtime.Loaders;
using Nox.XR.Trackers;
using UnityEngine;

namespace Nox.XR.Interactions {
	/// <summary>
	/// Poses the fingers of the avatar hand from the XR provider skeleton while the hand is not
	/// interacting (not holding a grabbable).
	/// <para>
	/// The skeleton comes from the tracker of the hand rig (<see cref="ITracker.TryGetSkeleton"/>), so it
	/// follows whatever runtime produced it - OpenXR hand tracking, a hand-tracking accessory on a
	/// controller, SteamVR. Its <see cref="HandFingerCurls"/> blend directly on the avatar finger poses
	/// (<c>Opened</c> / <c>Closed</c>, and the pinched pair for the thumb and index), which keeps the
	/// orientation convention of the avatar: no retargeting of raw joint axes is needed.
	/// </para>
	/// <para>
	/// The grabber owns the fingers while it grabs and while it lets them relax: this component stays quiet
	/// during that time and only takes over when the hand is free again.
	/// </para>
	/// </summary>
	[AddComponentMenu("Nox/XR/Interactions/Hand Skeleton Pose")]
	[DefaultExecutionOrder(2100)]
	[RequireComponent(typeof(Hand))]
	public class HandSkeletonPose : MonoBehaviour {
		[SerializeField, Tooltip("Hand part whose fingers are posed. Found on this GameObject when empty.")]
		private Hand hand;

		[SerializeField, Tooltip("Grabber that owns the fingers while it grabs. Found on this GameObject when empty.")]
		private HandGrabber grabber;

		[SerializeField, Tooltip("Pose the fingers from the provider skeleton. Disable to leave the fingers to the animation.")]
		private bool enabledPose = true;

		[SerializeField, Range(0f, 1f), Tooltip("Pinch value at or above which the thumb and index use the pinched poses.")]
		private float pinchThreshold = 0.5f;

		[SerializeField, Min(0f), Tooltip("How fast a curl follows the skeleton, per second. 0 snaps.")]
		private float followSpeed = 20f;

		[SerializeField, Min(0f), Tooltip("How fast the fingers open again when the skeleton is lost or the hand is idle.")]
		private float relaxSpeed = 10f;

		[SerializeField, Tooltip("Maximum abduction of the fingers for a fully fanned hand, in degrees (spread = 1). The middle stays neutral, the sides move by ±this angle. Negative flips the direction.")]
		private float spreadAngle = 10f;

		/// <summary>Spread value at which the fingers keep their opened pose (no abduction applied).</summary>
		private const float NeutralSpread = 0.5f;

		private IHand _source;
		private float[] _curls = System.Array.Empty<float>();
		private float _spread = NeutralSpread;
		private bool _posed;

		/// <summary>Hand part whose fingers are posed.</summary>
		public Hand HandPart {
			get => hand;
			set => hand = value;
		}

		/// <summary>Whether the fingers are currently driven by the provider skeleton.</summary>
		public bool PoseEnabled {
			get => enabledPose;
			set => enabledPose = value;
		}

		/// <summary>Wires the component to its hand and grabber.</summary>
		public void Configure(Hand part, HandGrabber owner = null) {
			hand = part;
			grabber = owner;

			if (!hand)
				return;

			hand.SourceChanged.RemoveListener(OnSourceChanged);
			hand.SourceChanged.AddListener(OnSourceChanged);
			OnSourceChanged(hand.Source);
		}

		private void Awake() {
			if (!hand)
				hand = GetComponent<Hand>();

			if (!grabber)
				grabber = GetComponent<HandGrabber>();

			if (hand) {
				hand.SourceChanged.RemoveListener(OnSourceChanged);
				hand.SourceChanged.AddListener(OnSourceChanged);
				OnSourceChanged(hand.Source);
			}
		}

		private void OnDestroy() {
			if (hand)
				hand.SourceChanged.RemoveListener(OnSourceChanged);
		}

		private void OnSourceChanged(IHand source) {
			_source = source;

			var count = source?.Fingers?.Length ?? 0;
			_curls = new float[count];
			_spread = NeutralSpread;
			_posed = false;
		}

		/// <summary>
		/// The grabber owns the fingers as long as it grabs or lets them relax: nothing to do here, the
		/// conformer is writing them.
		/// </summary>
		private void LateUpdate() {
			if (!enabledPose || !hand || _source == null)
				return;

			if (grabber && grabber.IsConformingFingers)
				return;

			var tracked = TryReadCurls(out var curls, out var pinch);

			// No skeleton ever seen: leave the fingers to the animation instead of forcing them open.
			if (!tracked && !_posed)
				return;

			if (tracked)
				_posed = true;

			var deltaTime = Time.deltaTime;
			var stillPosed = false;

			var targetSpread = tracked ? curls.Spread : NeutralSpread;
			var spreadSpeed = tracked ? followSpeed : relaxSpeed;
			_spread = spreadSpeed <= 0f
				? targetSpread
				: Mathf.MoveTowards(_spread, targetSpread, spreadSpeed * deltaTime);

			var palm = _source.Palm ? _source.Palm : hand.transform;
			var fingers = _source.Fingers;

			GetLateralSpan(fingers, palm, out var lateralCenter, out var lateralHalfSpan);

			for (var i = 0; i < fingers.Length && i < _curls.Length; i++) {
				var finger = fingers[i];
				if (finger == null)
					continue;

				var target = tracked ? CurlOf(finger.Type, curls) : 0f;
				var speed = tracked ? followSpeed : relaxSpeed;
				_curls[i] = speed <= 0f
					? target
					: Mathf.MoveTowards(_curls[i], target, speed * deltaTime);

				if (_curls[i] > 0.001f)
					stillPosed = true;

				var abduction = ProximalDelta(finger, palm, lateralCenter, lateralHalfSpan);
				Apply(finger, _curls[i], tracked && pinch && IsPinchFinger(finger.Type), abduction);
			}

			if (!tracked && !stillPosed)
				_posed = false;
		}

		/// <summary>
		/// Lateral extent of the fingers, measured along the palm's right axis: the centre and the half
		/// span normalise each finger to a -1 (one side) .. +1 (other side) abduction coefficient.
		/// The thumb is ignored: it abducts on its own articulation.
		/// </summary>
		private static void GetLateralSpan(IFinger[] fingers, Transform palm, out float center, out float halfSpan) {
			center = 0f;
			halfSpan = 0.0001f;

			if (fingers == null || !palm)
				return;

			var origin = palm.position;
			var right = palm.right;
			var min = float.MaxValue;
			var max = float.MinValue;

			for (var i = 0; i < fingers.Length; i++) {
				var finger = fingers[i];
				if (finger == null || finger.Type == FingerType.Thumb || !finger.Proximal)
					continue;

				var lateral = Vector3.Dot(finger.Proximal.position - origin, right);
				if (lateral < min) min = lateral;
				if (lateral > max) max = lateral;
			}

			if (min > max)
				return;

			center = (min + max) * 0.5f;
			halfSpan = Mathf.Max((max - min) * 0.5f, 0.0001f);
		}

		/// <summary>
		/// Abduction of a finger at its proximal joint, around the palm normal. The coefficient is negative
		/// on one side of the palm and positive on the other, so a rising spread fans the fingers apart and a
		/// falling one brings them together, whatever the handedness of the rig.
		/// </summary>
		private Quaternion ProximalDelta(IFinger finger, Transform palm, float center, float halfSpan) {
			if (Mathf.Approximately(spreadAngle, 0f) || !palm || finger.Type == FingerType.Thumb)
				return Quaternion.identity;

			var proximal = finger.Proximal;
			if (!proximal || !proximal.parent)
				return Quaternion.identity;

			var lateral = Vector3.Dot(proximal.position - palm.position, palm.right);
			var coefficient = Mathf.Clamp((lateral - center) / halfSpan, -1f, 1f);

			// Negative: a rising spread has to move the fingers apart. The sign flips with the handedness
			// of the rig, which is why `spreadAngle` itself stays signed.
			var angle = -spreadAngle * (_spread * 2f - 1f) * coefficient;

			if (Mathf.Abs(angle) < 0.0001f)
				return Quaternion.identity;

			var axis = proximal.parent.InverseTransformDirection(palm.forward);
			if (axis.sqrMagnitude < 0.000001f)
				return Quaternion.identity;

			return Quaternion.AngleAxis(angle, axis.normalized);
		}

		/// <summary>Reads the curls of this hand from the tracker of its rig.</summary>
		private bool TryReadCurls(out HandFingerCurls curls, out bool pinch) {
			curls = default;
			pinch = false;

			var provider = XRLoaderManager.Provider?.Features?.Trackers;
			if (provider == null || !provider.TryGet(hand.Rig.ToIndex(), out var tracker) || tracker == null)
				return false;

			if (!tracker.TryGetSkeleton(out var skeleton) || !skeleton.Tracked)
				return false;

			curls = skeleton.Curls;
			pinch = curls.Pinch >= pinchThreshold;
			return true;
		}

		private static float CurlOf(FingerType type, in HandFingerCurls curls)
			=> type switch {
				FingerType.Thumb => curls.Thumb,
				FingerType.Index => curls.Index,
				FingerType.Middle => curls.Middle,
				FingerType.Ring => curls.Ring,
				FingerType.Pinky => curls.Little,
				_ => 0f
			};

		private static bool IsPinchFinger(FingerType type)
			=> type is FingerType.Thumb or FingerType.Index;

		private static void Apply(IFinger finger, float curl, bool pinch, in Quaternion abduction) {
			var normalOpen = PoseValues(finger, FingerCurl.Opened);
			var normalClosed = PoseValues(finger, FingerCurl.Closed);

			var open = normalOpen;
			var closed = normalClosed;

			// A pinched pose the avatar never saved is identity: using it would fold the finger onto its
			// parent. Fall back to the normal poses when the pinched pair is not a real pose.
			if (pinch) {
				var pinchedOpen = PoseValues(finger, FingerCurl.PinchedOpened);
				var pinchedClosed = PoseValues(finger, FingerCurl.PinchedClosed);

				if (IsSavedPose(pinchedOpen) && IsSavedPose(pinchedClosed)) {
					open = pinchedOpen;
					closed = pinchedClosed;
				}
			}

			if (open == null || closed == null)
				return;

			var joints = new[] { finger.Proximal, finger.Intermediate, finger.Distal, finger.Tip };

			for (var i = 0; i < joints.Length && i < open.Length; i++) {
				var joint = joints[i];
				if (!joint)
					continue;

				joint.localRotation = Quaternion.Slerp(open[i], closed[i], curl);

				// Abduction happens at the knuckle (proximal bone) only: the rotation is applied on the
				// left, i.e. in the space of the bone's parent (the hand), around the palm normal.
				if (i == 0 && abduction != Quaternion.identity)
					joint.localRotation = abduction * joint.localRotation;
			}
		}

		private static bool IsSavedPose(Quaternion[] pose) {
			if (pose == null)
				return false;

			for (var i = 0; i < pose.Length; i++)
				if (Quaternion.Angle(pose[i], Quaternion.identity) > 0.01f)
					return true;

			return false;
		}

		private static Quaternion[] PoseValues(IFinger finger, FingerCurl curl) {
			var poses = finger.Poses;
			if (poses == null)
				return null;

			for (var i = 0; i < poses.Length; i++) {
				var pose = poses[i];
				if (pose != null && pose.Curl == curl)
					return pose.Values;
			}

			return null;
		}
	}
}
