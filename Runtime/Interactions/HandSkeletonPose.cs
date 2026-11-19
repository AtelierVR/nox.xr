using Nox.Avatars.Hand;
using Nox.CCK.Entities;
using Nox.XR.Bindings;
using Nox.XR.Player;
using Nox.XR.Runtime;
using Nox.XR.Runtime.Loaders;
using Nox.XR.Trackers;
using UnityEngine;
using EntityRig = Nox.CCK.Entities.EntityRig;
using Logger = Nox.CCK.Utils.Logger;

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
	/// <para>
	/// When the hand is a physical controller, the fingers come from its analog axes: the trigger blends the index and
	/// the grip the other three. A control without an analog value (button or stick touch) closes its finger
	/// outright. Such a controller reports no abduction, so a fully opened hand keeps the slight spread authored in
	/// its <c>Opened</c> pose; a finger that stays extended while the others are closed (a pointing index) is folded
	/// toward the middle one by <c>controllerGather</c>, so it points straight instead of splayed. A bare hand (no
	/// controller on that slot) always follows its skeleton.
	/// </para>
	/// </summary>
	[AddComponentMenu("Nox/XR/Interactions/Hand Skeleton Pose")]
	[DefaultExecutionOrder(2100)]
	[RequireComponent(typeof(Hand))]
	public class HandSkeletonPose : MonoBehaviour {
		[SerializeField, Tooltip("Hand part whose fingers are posed. Found on this GameObject when empty.")]
		private Hand hand;

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

		[SerializeField, Tooltip("Drive the fingers of a controller from its analog axes (trigger, grip, touch) instead of the skeleton the runtime reports for that hand. Disable it to follow a hand-tracking accessory such as a UDCAP on an Index.")]
		private bool controllerCurls = true;

		[SerializeField, Range(0f, 2f), Tooltip("How far a finger that stays extended while the others are closed folds toward the middle one (a pointing index, straightened instead of splayed). 0 leaves the opened pose as authored, 1 makes it parallel to the middle finger. Nothing is applied while the whole hand is opened or on a finger that is folded.")]
		private float controllerGather = 1f;

		[SerializeField, Tooltip("Log what drives the fingers of this hand: controller mode, live skeleton, raw touch controls, bindings, curls, finger poses and gather angles. Logged only when the state changes, every quarter second at most.")]
		private bool debugLog = true;

		/// <summary>Spread value at which the fingers keep their opened pose (no abduction applied).</summary>
		private const float NeutralSpread = 0.5f;

		private const float DebugLogInterval = 0.25f;

		private IHand _source;
		private HandPhysical _owner;
		private float[] _curls = System.Array.Empty<float>();
		private float _spread = NeutralSpread;
		private bool _posed;
		private bool _controllerFingers;

		private ITracker _debugTracker;
		private HandTrackingMode _debugMode;
		private bool _debugTracked;
		private bool _debugThumbTouched;
		private float _nextDebugLog;
		private string _lastDebugLog;

		/// <summary>Hand part whose fingers are posed.</summary>
		public Hand HandPart {
			get => hand;
			set => hand = value;
		}

		/// <summary>
		/// Hand the fingers are actually written on: the physical hand of the part when it has one (the physical owns
		/// the pose and gives it back to the avatar), the avatar hand otherwise.
		/// </summary>
		public IHand PoseHand {
			get {
				var physical = _owner ? _owner.Physical : null;
				return physical ? physical : _source;
			}
		}

		/// <summary>Whether the fingers are currently driven by the provider skeleton.</summary>
		public bool PoseEnabled {
			get => enabledPose;
			set => enabledPose = value;
		}

		/// <summary>Wires the component to its hand.</summary>
		public void Configure(Hand part) {
			hand = part;

			if (!hand)
				return;

			TakePhysical();

			hand.SourceChanged.RemoveListener(OnSourceChanged);
			hand.SourceChanged.AddListener(OnSourceChanged);
			OnSourceChanged(hand.Source);
		}

		private void TakePhysical() {
			_owner = hand ? hand.GetComponent<HandPhysical>() : null;

			if (_owner)
				_owner.ExternalPose = true;
		}

		private void Awake() {
			if (!hand)
				hand = GetComponent<Hand>();

			if (hand) {
				TakePhysical();

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

			var count = PoseHand?.Fingers?.Length ?? 0;
			_curls = new float[count];
			_spread = NeutralSpread;
			_posed = false;
		}

		/// <summary>
		/// The grabber owns the fingers as long as it grabs or lets them relax: nothing to do here, the
		/// conformer is writing them.
		/// </summary>
		private void LateUpdate() {
			var pose = PoseHand;

			if (!enabledPose || !hand || pose == null)
				return;

			if (hand && hand.IsConformingFingers)
				return;

			var fingers = pose.Fingers;
			if (fingers == null || fingers.Length == 0)
				return;

			if (_curls.Length != fingers.Length)
				_curls = new float[fingers.Length];

			var tracked = TryReadCurls(out var curls, out var pinch);

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

			var palm = pose.Palm ? pose.Palm : hand.transform;

			GetLateralSpan(fingers, palm, out var lateralCenter, out var lateralHalfSpan);
			var middle = FindFinger(fingers, FingerType.Middle);

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

				var abduction = _controllerFingers
					? GatherDelta(finger, middle, palm, EffectiveGather(fingers, i), out _)
					: ProximalDelta(finger, palm, lateralCenter, lateralHalfSpan);

				Apply(finger, _curls[i], tracked && pinch && IsPinchFinger(finger.Type), abduction);
			}

			if (!tracked && !stillPosed)
				_posed = false;
		}

		/// <summary>
		/// How far the gather folds one finger, in [0, <c>controllerGather</c>]: only for a finger that is the last one
		/// left out, i.e. the pointing index - the other fingers must all be folded, and the finger itself must not be.
		/// A fully opened hand keeps the slight spread authored in its <c>Opened</c> pose, and folding one finger
		/// (lowering the index on the trigger) never gathers the ones that stay open.
		/// </summary>
		private float EffectiveGather(IFinger[] fingers, int index) {
			if (controllerGather <= 0f || fingers == null || index < 0 || index >= _curls.Length)
				return 0f;

			var curl = _curls[index];
			if (curl >= 1f)
				return 0f;

			var folded = 1f;
			for (var i = 0; i < fingers.Length && i < _curls.Length; i++) {
				if (i == index)
					continue;

				var other = fingers[i];
				if (other == null || other.Type == FingerType.Thumb)
					continue;

				if (_curls[i] < folded)
					folded = _curls[i];
			}

			return controllerGather * (1f - curl) * folded;
		}

		private static IFinger FindFinger(IFinger[] fingers, FingerType type) {
			if (fingers == null)
				return null;

			for (var i = 0; i < fingers.Length; i++)
				if (fingers[i] != null && fingers[i].Type == type)
					return fingers[i];

			return null;
		}

		/// <summary>
		/// Folds a finger toward the middle one, at its proximal joint, around the palm normal.
		/// <para>
		/// The reference direction of each finger is its <b>opened pose</b>, not its live bones (see
		/// <see cref="TryRestLateral"/>): the gather therefore stays the same whether the finger is extended
		/// (pointing with the index) or curled (grip squeezed), instead of swinging or collapsing.
		/// </para>
		/// <para>
		/// Everything is expressed in the frame of the proximal's parent - the space of the local rotation the
		/// delta multiplies - so the sense of the rotation stays correct on a mirrored hand. The angle is the
		/// signed one between the two rest directions, scaled by <paramref name="fraction"/>: 1 makes the finger
		/// parallel to the middle one, above 1 it keeps rotating so the tips come closer.
		/// </para>
		/// </summary>
		private static Quaternion GatherDelta(IFinger finger, IFinger middle, Transform palm, float fraction, out float degrees) {
			degrees = 0f;

			if (fraction <= 0f || !palm || finger == null || middle == null)
				return Quaternion.identity;

			if (finger.Type is FingerType.Thumb or FingerType.Middle)
				return Quaternion.identity;

			var proximal = finger.Proximal;
			if (!proximal || !proximal.parent)
				return Quaternion.identity;

			var frame = proximal.parent;
			var normal = frame.InverseTransformDirection(palm.forward);
			if (normal.sqrMagnitude < 1e-10f)
				return Quaternion.identity;

			normal.Normalize();

			if (!TryRestLateral(finger, frame, normal, out var direction) || !TryRestLateral(middle, frame, normal, out var target))
				return Quaternion.identity;

			var angle = Vector3.SignedAngle(direction, target, normal) * fraction;
			if (Mathf.Abs(angle) < 0.0001f)
				return Quaternion.identity;

			if (Vector3.Dot(Quaternion.AngleAxis(angle, normal) * direction, target)
				< Vector3.Dot(Quaternion.AngleAxis(-angle, normal) * direction, target))
				angle = -angle;

			degrees = angle;
			return Quaternion.AngleAxis(angle, normal);
		}

		/// <summary>
		/// Direction a finger grows in at its <b>opened pose</b>, measured in <paramref name="frame"/>, projected on
		/// the plane of the palm so only its abduction remains.
		/// <para>
		/// The reference comes from the rig and the <c>Opened</c> pose instead of the live bones: a curled finger
		/// points into the palm, where its direction projected on the palm plane collapses - normalising that
		/// residue turned the gather into a random twist (the ring finger folding over itself) as soon as the grip
		/// was squeezed. The child's local offset is constant, so the rest direction only depends on the avatar.
		/// </para>
		/// </summary>
		private static bool TryRestLateral(IFinger finger, Transform frame, Vector3 normal, out Vector3 direction) {
			direction = default;

			var proximal = finger.Proximal;
			var next = finger.Intermediate ? finger.Intermediate : finger.Distal;

			if (!proximal || !next)
				return false;

			var offset = proximal.InverseTransformPoint(next.position);
			if (offset.sqrMagnitude < 1e-10f)
				return false;

			var open = PoseValues(finger, FingerCurl.Opened);
			var rest = open is { Length: > 0 } ? open[0] : Quaternion.identity;

			var local = rest * offset;
			local -= normal * Vector3.Dot(local, normal);
			if (local.sqrMagnitude < 1e-10f)
				return false;

			direction = local.normalized;
			return true;
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
			var tracked = ReadCurls(out curls, out pinch);

			if (debugLog && Time.unscaledTime >= _nextDebugLog) {
				_nextDebugLog = Time.unscaledTime + DebugLogInterval;
				LogState(curls, tracked);
			}

			return tracked;
		}

		private bool ReadCurls(out HandFingerCurls curls, out bool pinch) {
			curls = default;
			pinch = false;

			_debugTracker = null;
			_debugMode = HandTrackingMode.Controller;
			_debugTracked = false;
			_debugThumbTouched = false;

			var provider = XRLoaderManager.Provider?.Features?.Trackers;
			if (provider == null || !provider.TryGet(hand.Rig.ToIndex(), out var tracker) || tracker == null)
				return false;

			var mode = tracker.ToHandTrackingMode();
			var tracked = tracker.TryGetSkeleton(out var skeleton) && skeleton.Tracked;
			var features = tracker.Features;

			var controller = features.HasFlag(TrackerFeatures.Buttons) && !features.HasFlag(TrackerFeatures.Handed);

			_debugTracker = tracker;
			_debugMode = mode;
			_debugTracked = tracked;

			if (tracked && (!controllerCurls || !controller || mode == HandTrackingMode.Hand)) {
				curls = skeleton.Curls;
				pinch = mode == HandTrackingMode.Hand && curls.Pinch >= pinchThreshold;
				_controllerFingers = false;
				return true;
			}

			if (!controller || !controllerCurls) {
				_controllerFingers = false;
				return false;
			}

			_controllerFingers = true;
			ApplyControllerCurls(ref curls, tracker, hand.Rig == EntityRig.LeftHand);
			return true;
		}

		/// <summary>
		/// Replaces the curls of the fingers a controller physically drives with its axes: the index follows the
		/// trigger, the middle/ring/pinky the grip, and the thumb the touch of the buttons or of the stick. The axis
		/// itself blends the avatar pose progressively, where a runtime skeleton would jump to a full curl as soon as
		/// the axis is only touched.
		/// <para>
		/// A control without an analog value (a button touch, a stick touch) has no pressure to blend with, so its
		/// finger goes to the closed pose at once: on a Touch, touching A/B or the stick brings the thumb down.
		/// A controller has no abduction either, so the spread stays neutral and the fingers are folded toward the
		/// middle one by <c>controllerGather</c>.
		/// </para>
		/// </summary>
		private void ApplyControllerCurls(ref HandFingerCurls curls, ITracker tracker, bool left) {
			var trigger = Keybindings.GetFloatValue(left ? XRBinding.FingerLeftIndex : XRBinding.FingerRightIndex);
			var grip = Keybindings.GetFloatValue(left ? XRBinding.FingerLeftMiddle : XRBinding.FingerRightMiddle);
			var thumbTouched = IsThumbTouched(tracker, left);

			_debugThumbTouched = thumbTouched;

			curls.Index = Mathf.Clamp01(trigger);
			curls.Middle = curls.Ring = curls.Little = Mathf.Clamp01(grip);
			curls.Thumb = thumbTouched ? 1f : 0f;

			curls.Spread = NeutralSpread;
			curls.Pinch = 0f;
		}

		/// <summary>
		/// Whether the thumb rests on the controller: the thumb binding first (a thumbrest when the controller has
		/// one), then the raw state of the controls it covers - the face buttons and the stick, touched or pressed.
		/// Those are binary, so the finger has to close outright instead of blending a pressure it never has.
		/// <para>
		/// The raw controls are read from the device itself because the binding resolves to a single path: a
		/// controller that exposes a thumbrest would never report a finger on A/B or on the stick through it.
		/// </para>
		/// </summary>
		private static bool IsThumbTouched(ITracker tracker, bool left) {
			if (Keybindings.GetFloatValue(left ? XRBinding.FingerLeftThumb : XRBinding.FingerRightThumb) > 0.5f)
				return true;

			if (tracker == null)
				return false;

			if (tracker.Get<Vector2>(TrackerKeys.Primary2DAxis).sqrMagnitude > 0.01f
				|| tracker.Get<Vector2>(TrackerKeys.Secondary2DAxis).sqrMagnitude > 0.01f)
				return true;

			return tracker.Get<bool>(TrackerKeys.PrimaryTouch)
				|| tracker.Get<bool>(TrackerKeys.SecondaryTouch)
				|| tracker.Get<bool>(TrackerKeys.Primary2DAxisTouch)
				|| tracker.Get<bool>(TrackerKeys.Secondary2DAxisTouch)
				|| tracker.Get<bool>(TrackerKeys.Primary2DAxisClick)
				|| tracker.Get<bool>(TrackerKeys.Secondary2DAxisClick)
				|| tracker.Get<bool>(TrackerKeys.PrimaryButton)
				|| tracker.Get<bool>(TrackerKeys.SecondaryButton);
		}

		/// <summary>
		/// Writes what drives the fingers of this hand, when that state changed since the previous log: which
		/// tracker answered, whether it served a skeleton, the values read for the thumb, the applied curls, the
		/// gather of every finger and the motion each saved pose allows. Enabled by <c>debugLog</c>.
		/// </summary>
		private void LogState(in HandFingerCurls curls, bool tracked) {
			var message = new System.Text.StringBuilder();

			message.Append("[HandSkeletonPose] ")
				.Append(hand ? hand.name : "no hand")
				.Append(" rig=").Append(hand ? hand.Rig.ToString() : "none")
				.Append(" mode=").Append(_debugMode)
				.Append(" skeleton=").Append(_debugTracked)
				.Append(" controllerFingers=").Append(_controllerFingers);

			var tracker = _debugTracker;
			if (tracker == null) {
				message.Append(" | no tracker for this rig");
			} else {
				message.Append(" | tracker=\"").Append(tracker.Name)
					.Append("\" id=").Append(tracker.Id)
					.Append(" rig=").Append(tracker.Rig)
					.Append(" features=").Append(tracker.Features)
					.Append(" | raw touch ").Append(DescribeTouch(tracker));
			}

			message.Append(" | bindings index L/R=")
				.Append(Keybindings.GetFloatValue(XRBinding.FingerLeftIndex).ToString("0.00"))
				.Append('/').Append(Keybindings.GetFloatValue(XRBinding.FingerRightIndex).ToString("0.00"))
				.Append(" grip L/R=")
				.Append(Keybindings.GetFloatValue(XRBinding.FingerLeftMiddle).ToString("0.00"))
				.Append('/').Append(Keybindings.GetFloatValue(XRBinding.FingerRightMiddle).ToString("0.00"))
				.Append(" thumb L/R=")
				.Append(Keybindings.GetFloatValue(XRBinding.FingerLeftThumb).ToString("0.00"))
				.Append('/').Append(Keybindings.GetFloatValue(XRBinding.FingerRightThumb).ToString("0.00"));

			message.Append(" | thumbTouched=").Append(_debugThumbTouched)
				.Append(" curls t=").Append(curls.Thumb.ToString("0.00"))
				.Append(" i=").Append(curls.Index.ToString("0.00"))
				.Append(" m=").Append(curls.Middle.ToString("0.00"));

			message.Append(" | devices on this rig:").Append(DescribeDevices());
			message.Append(" | gather=").Append(controllerGather.ToString("0.00")).Append(DescribeGather());
			message.Append(" | open->closed").Append(DescribePoses());

			var text = message.ToString();
			if (text == _lastDebugLog)
				return;

			_lastDebugLog = text;
			Logger.Log(text, this, tag: nameof(HandSkeletonPose));
		}

		/// <summary>
		/// Every device the provider exposes on this hand's rig. A runtime with hand tracking enabled registers the
		/// bare hand next to the controller on the same slot; seeing both here tells which one answered.
		/// </summary>
		private string DescribeDevices() {
			var provider = XRLoaderManager.Provider?.Features?.Trackers;
			var rig = hand ? hand.Rig.ToIndex() : EntityRig.None.ToIndex();

			if (provider == null)
				return " no provider";

			var builder = new System.Text.StringBuilder();
			var trackers = provider.Trackers;
			for (var i = 0; i < trackers.Count; i++) {
				var candidate = trackers[i];
				if (candidate == null || candidate.Rig != rig)
					continue;

				builder.Append(' ').Append(candidate.Name)
					.Append('[').Append(candidate.Features).Append(']')
					.Append(ReferenceEquals(candidate, _debugTracker) ? "(used)" : "");
			}

			return builder.Length == 0 ? " none" : builder.ToString();
		}

		/// <summary>Raw state of every control the thumb covers, so a missing touch value is visible.</summary>
		private static string DescribeTouch(ITracker tracker) {			var stick = tracker.Get<Vector2>(TrackerKeys.Primary2DAxis);

			return $"P={tracker.Get<bool>(TrackerKeys.PrimaryTouch)}"
				   + $" S={tracker.Get<bool>(TrackerKeys.SecondaryTouch)}"
				   + $" stickTouch={tracker.Get<bool>(TrackerKeys.Primary2DAxisTouch)}"
				   + $" stickClick={tracker.Get<bool>(TrackerKeys.Primary2DAxisClick)}"
				   + $" stick=({stick.x:0.00},{stick.y:0.00})"
				   + $" A={tracker.Get<bool>(TrackerKeys.PrimaryButton)}"
				   + $" B={tracker.Get<bool>(TrackerKeys.SecondaryButton)}";
		}

		/// <summary>Angle the gather applies to each finger of this hand, in degrees.</summary>
		private string DescribeGather() {
			if (_source == null)
				return " no hand";

			var fingers = _source.Fingers;
			if (fingers == null || fingers.Length == 0)
				return " no finger";

			var palm = _source.Palm ? _source.Palm : hand.transform;
			var middle = FindFinger(fingers, FingerType.Middle);
			var builder = new System.Text.StringBuilder();

			for (var i = 0; i < fingers.Length; i++) {
				var finger = fingers[i];
				if (finger == null)
					continue;

				GatherDelta(finger, middle, palm, EffectiveGather(fingers, i), out var degrees);
				builder.Append(' ').Append(finger.Type).Append('=').Append(degrees.ToString("0.0"));
			}

			return builder.ToString();
		}

		/// <summary>Angle of the opened and closed poses of each finger, in degrees: 0 means the pose does nothing.</summary>
		private string DescribePoses() {
			if (_source == null)
				return " no hand";

			var fingers = _source.Fingers;
			if (fingers == null || fingers.Length == 0)
				return " no finger";

			var builder = new System.Text.StringBuilder();

			for (var i = 0; i < fingers.Length; i++) {
				var finger = fingers[i];
				if (finger == null)
					continue;

				var open = PoseValues(finger, FingerCurl.Opened);
				var closed = PoseValues(finger, FingerCurl.Closed);
				var angle = 0f;

				if (open != null && closed != null)
					for (var j = 0; j < open.Length && j < closed.Length; j++)
						angle = Mathf.Max(angle, Quaternion.Angle(open[j], closed[j]));

				builder.Append(' ').Append(finger.Type).Append('=').Append(angle.ToString("0.0"));

				// The thumb is the finger that misbehaves most: its proximal rotation tells an inverted or a
				// missing pose apart (opened and closed swapped, or both identical).
				if (finger.Type == FingerType.Thumb && open is { Length: > 0 } && closed is { Length: > 0 })
					builder.Append("[proximal ").Append(open[0].eulerAngles.ToString("0"))
						.Append(" -> ").Append(closed[0].eulerAngles.ToString("0")).Append(']');
			}

			return builder.ToString();
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
