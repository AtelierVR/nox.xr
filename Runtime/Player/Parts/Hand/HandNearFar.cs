using Nox.Avatars.Hand;
using Nox.CCK.Theme;
using Nox.CCK.UI;
using Nox.CCK.Utils;
using Nox.Interactions;
using Nox.XR.Runtime;
using Nox.XR.Runtime.Connectors;
using Nox.XR.Runtime.Settings;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Events;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Interactors.Casters;
using UnityEngine.XR.Interaction.Toolkit.Interactors.Visuals;
using Logger = Nox.CCK.Utils.Logger;
using NearFarInteractor = Nox.XR.Runtime.Connectors.NearFarInteractor;

namespace Nox.XR.Player {
	/// <summary>
	/// Hosts the near/far interactor of a hand. The interactor casts and talks to the UI; this component is the only one
	/// that draws the ray: the <see cref="CurveVisualController"/> of the prefab is kept disabled, so that two writers
	/// never fight over the same line renderer.
	/// </summary>
	[AddComponentMenu("Nox/XR/Player/Hand Near Far")]
	[RequireComponent(typeof(Hand))]
	[DefaultExecutionOrder(-100)]
	public class HandNearFar : MonoBehaviour {
		private const string PREFAB_ADDRESS = "near_far_interactor.prefab";
		private const int LinePointCount = 20;

		[SerializeField, Tooltip("Hand part of this side. Found on this GameObject when empty.")]
		private Hand hand;

		[SerializeField, Tooltip("Distance (world metres) between a fingertip and its poke target under which the ray stops interacting.")]
		private float pokeRayStopDistance = 0.15f;

		[SerializeField, Tooltip("Factor over the stop distance before the ray is allowed back, so it cannot blink at the threshold.")]
		private float pokeRayReleaseFactor = 1.6f;

		[Header("Line")]
		[SerializeField, Min(0f), Tooltip("Width of the ray in metres. The same in every state: only the colour changes.")]
		private float lineWidth = 0.004f;

		[SerializeField, Range(0f, 1f), Tooltip("How far along the aim the curve is pulled. The same curve is drawn on an element and on nothing.")]
		private float bendRatio = 0.5f;

		[SerializeField, Min(0.01f), Tooltip("Length of the ray in metres when it points at nothing.")]
		private float restingLength = 0.25f;

		[SerializeField, Tooltip("Opacity of the ray at its end.")]
		private float lineOpacity = 0.9f;

		[SerializeField, Range(0f, 1f), Tooltip("How much the accent of a pointed control is washed out. The full colour is kept for a press.")]
		private float hoverDesaturation = 0.45f;

		[SerializeField, Min(0f), Tooltip("Speed of the fade between the resting grey, the accent of a control and the press colour.")]
		private float uiHoverFadeSpeed = 12f;

		[SerializeField, Min(0f), Tooltip("Seconds the ray keeps the distance of the last element it pointed at, so the gap between two elements does not throw the line to its resting length.")]
		private float hitLinger = 0.3f;

		[SerializeField, Min(0f), Tooltip("Seconds the line takes to go from the length it had on the last element to its resting length once the ray points at nothing. The curve itself never changes.")]
		private float restCurveFade = 0.4f;

		[Header("Aim")]
		[SerializeField, Tooltip("Origin of the ray in the palm space: fixed on the hand, it never lags behind.")]
		private Vector3 aimPosition = new(-0.1013f, -0.0707f, -0.0222f);

		[SerializeField, Tooltip("Rotation added to the index finger axis, in the palm space. Zero aims along the finger.")]
		private Vector3 aimOffset;

		[SerializeField, Tooltip("Anchor of the ray in the hand template, used when there is no avatar hand to take the aim from: the ray leaves along its forward. Empty starts from the hand itself.")]
		private Transform anchor;

		[Header("Stabilization")]
		[SerializeField, Tooltip("Lets the far caster absorb the tremor: the line is smoothed, the origin stays on the hand.")]
		private bool stabilizeFarCaster = true;

		[SerializeField, Min(0f), Tooltip("Metres over which the far caster absorbs the tremor.")]
		private float farPositionStabilization = 0.25f;

		[SerializeField, Min(0f), Tooltip("Degrees over which the far caster absorbs the tremor.")]
		private float farAngleStabilization = 20f;

		private NearFarInteractor _ray;
		private Transform _aim;
		private Transform _stabilizer;
		private float _stabilizeTime;
		private HandPhysical _physical;
		private IGrabber[] _grabbers;
		private bool _held;
		private bool _pressed;

		private LineRenderer _line;
		private CurveVisualController _controller;
		private UnityAction _beforeRender;
		private readonly Vector3[] _linePoints = new Vector3[LinePointCount];
		private readonly Gradient _gradient = new();
		private readonly GradientColorKey[] _colors = new GradientColorKey[2];
		private readonly GradientAlphaKey[] _alphas = new GradientAlphaKey[2];

		private Color _idleColor;
		private Color _hoverColor;
		private Color _pressColor;
		private Color _color;

		private GameObject _hoverTarget;
		private bool _hoverInteractive;
		private float _lingerTime;

		private IUiPressTarget _pressTarget;
		private GameObject _pressHit;
		private bool _pressingUi;

		private bool _rayEnabled;
		private bool _lineEnabled;
		private bool _curved;
		private Vector3 _curveEnd;
		private float _endDistance;
		private float _curveTime;
		private float _drawDistance;
		private float _restBlend;

		public NearFarInteractor Ray
			=> _ray;

		private void Reset()
			=> hand = GetComponent<Hand>();

		private void Awake() {
			if (!hand)
				hand = GetComponent<Hand>();

			_beforeRender = DrawLine;
		}

		private void OnEnable() {
			if (!hand)
				return;

			hand.SourceChanged.RemoveListener(OnSourceChanged);
			hand.SourceChanged.AddListener(OnSourceChanged);
			ThemeManager.OnThemeChanged.RemoveListener(OnThemeChanged);
			ThemeManager.OnThemeChanged.AddListener(OnThemeChanged);

			Application.onBeforeRender -= _beforeRender;
			Application.onBeforeRender += _beforeRender;

			OnSourceChanged(hand.Source);
		}

		private void OnDisable() {
			if (hand)
				hand.SourceChanged.RemoveListener(OnSourceChanged);

			ThemeManager.OnThemeChanged.RemoveListener(OnThemeChanged);

			if (_beforeRender != null)
				Application.onBeforeRender -= _beforeRender;

			ApplyPress(null, 0f);

			DestroyRay();
		}

		private void OnThemeChanged(string theme)
			=> ApplyTheme();

		private void OnSourceChanged(IHand source) {
			DestroyRay();

			ResolveAnchor(source, out var parent, out var position, out var rotation);
			if (!parent)
				return;

			var client = Client.Instance;
			var prefab = client != null ? client.InstantiateAsset(PREFAB_ADDRESS) : null;
			if (!prefab) {
				Logger.LogWarning($"Near/far interactor prefab not found: {PREFAB_ADDRESS}", this, tag: nameof(HandNearFar));
				return;
			}

			var ray = prefab.GetComponent<NearFarInteractor>();
			if (!ray) {
				prefab.Destroy();
				return;
			}

			_aim = new GameObject($"{name} Aim").transform;
			_aim.SetParent(parent, false);
			_aim.localPosition = position;
			_aim.localRotation = rotation;

			ray.transform.SetParent(_aim, false);
			ray.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
			ray.Hand = Handedness();

			_ray = ray;

			ConfigureStabilization();

			BindLine();

			ApplyTheme();
		}

		/// <summary>
		/// Where the ray starts on the hand: the position is fixed in the palm space, the orientation follows the index
		/// finger. The palm frames of the two hands are mirrored, so a rotation fixed in the palm space points the right
		/// ray backwards: the axis has to come from the bones of the hand itself.
		/// <para>
		/// The orientation is read once, here, and never again: the hand pose blends the finger live, and following it
		/// turned the ray while the finger closed.
		/// </para>
		/// </summary>
		private void ResolveAnchor(IHand source, out Transform parent, out Vector3 position, out Quaternion rotation) {
			if (source?.NearFar) {
				parent   = source.NearFar;
				position = Vector3.zero;
				rotation = Quaternion.identity;
				return;
			}

			parent = source?.Palm ? source.Palm : source?.Anchor;
			if (!parent) {
				parent   = anchor ? anchor : hand ? hand.transform : null;
				position = Vector3.zero;
				rotation = Quaternion.identity;
				return;
			}

			Mirror(source, out position, out rotation);

			if (!source.Palm || !TryFingerAxis(source, parent, out var forward))
				return;

			rotation = Quaternion.LookRotation(forward) * rotation;
		}

		/// <summary>
		/// The aim offset is authored in the palm space of the left hand. The palm frame of the right hand mirrors it
		/// across the Y axis (the fingers run along -Y on the left, +Y on the right), so the same offset would put the
		/// ray behind the knuckles: the offset is reflected there, and the rotation with it.
		/// </summary>
		private void Mirror(IHand source, out Vector3 position, out Quaternion rotation) {
			if (source == null || source.Type != HandType.Right) {
				position = aimPosition;
				rotation = Quaternion.Euler(aimOffset);
				return;
			}

			position = new Vector3(aimPosition.x, -aimPosition.y, aimPosition.z);
			rotation = Quaternion.Euler(new Vector3(-aimOffset.x, aimOffset.y, -aimOffset.z));
		}

		private InteractorHandedness Handedness()
			=> hand && hand.Side == HandSide.Left ? InteractorHandedness.Left : InteractorHandedness.Right;

		/// <summary>
		/// Direction the tip of the index points, in the palm space, read on the opened pose of the finger: the authored
		/// pose is what a pointing hand looks like, where the live bones are already blended with the tracker - and still
		/// near the T-pose when the hand is built, which is why a direction taken from them is off.
		/// </summary>
		private static bool TryFingerAxis(IHand source, Transform palm, out Vector3 forward) {
			forward = default;

			var fingers = source.Fingers;
			if (fingers == null)
				return false;

			for (var i = 0; i < fingers.Length; i++) {
				var index = fingers[i];
				if (index == null || index.Type != FingerType.Index)
					continue;

				var proximal     = index.Proximal;
				var intermediate = index.Intermediate;
				var distal       = index.Distal;
				var tip          = index.Tip;
				var poses        = OpenedPose(index);

				if (!proximal || !proximal.parent || !intermediate || !distal || !tip || poses == null)
					return false;

				// the chain of the pose: a bone offset never changes with the curl, only the rotations do
				var rotation = poses[0];
				var start    = rotation * intermediate.localPosition;

				rotation *= poses[1];
				var position = start + rotation * distal.localPosition;

				rotation *= poses[2];
				position += rotation * tip.localPosition;

				forward = Quaternion.Inverse(palm.rotation) * proximal.parent.TransformDirection(position - start);
				return forward.sqrMagnitude > 1e-6f;
			}

			return false;
		}

		private static Quaternion[] OpenedPose(IFinger finger) {
			var poses = finger.Poses;
			if (poses == null)
				return null;

			for (var i = 0; i < poses.Length; i++) {
				if (poses[i] == null || poses[i].Curl != FingerCurl.Opened)
					continue;

				var values = poses[i].Values;
				return values is { Length: >= 3 } ? values : null;
			}

			return null;
		}

		/// <summary>
		/// The tremor is absorbed away from the hand: the ray keeps its origin fixed on the palm, the caster reads a
		/// smoothed copy of that pose instead, so the line and the cursor never shake with the hand.
		/// <para>
		/// The caster stabilization of the toolkit is not used: it creates its anchor under an <c>XROrigin</c>, which
		/// this rig does not have, and it switches itself off when it cannot find one.
		/// </para>
		/// </summary>
		private void ConfigureStabilization() {
			if (!_ray)
				return;

			var caster = _ray.GetComponent<CurveInteractionCaster>();
			if (!caster)
				return;

			caster.enableStabilization = false;
			caster.castOrigin          = stabilizeFarCaster ? CreateStabilizer() : _ray.transform;
			_stabilizeTime             = 0f;
		}

		/// <summary>Smoothed copy of the aim pose, kept at the root so it never inherits the tremor of the hand.</summary>
		private Transform CreateStabilizer() {
			if (_stabilizer)
				return _stabilizer;

			_stabilizer = new GameObject($"{name} Ray Stabilizer").transform;
			_stabilizer.SetParent(_ray.transform.root, false);
			_stabilizer.SetPositionAndRotation(_ray.transform.position, _ray.transform.rotation);
			return _stabilizer;
		}

		/// <summary>
		/// Moves the cast origin towards the aim pose. A micro movement only covers a fraction of the distance in a frame,
		/// a real one is followed immediately, and the smoothing does not depend on the frame rate.
		/// </summary>
		private void Stabilize() {
			if (!_stabilizer || !_ray)
				return;

			var aim      = _ray.transform;
			var position = aim.position;
			var rotation = aim.rotation;

			if (stabilizeFarCaster) {
				var now   = Time.unscaledTime;
				var delta = _stabilizeTime <= 0f ? Time.unscaledDeltaTime : now - _stabilizeTime;
				_stabilizeTime = now;

				if (delta > 0f) {
					if (farPositionStabilization > 0f)
						position = Vector3.Lerp(_stabilizer.position, aim.position, Blend(Vector3.Distance(_stabilizer.position, aim.position) / farPositionStabilization, delta));

					if (farAngleStabilization > 0f)
						rotation = Quaternion.Slerp(_stabilizer.rotation, aim.rotation, Blend(Quaternion.Angle(_stabilizer.rotation, aim.rotation) / farAngleStabilization, delta));
				}
			}

			_stabilizer.SetPositionAndRotation(position, rotation);
		}

		/// <summary>Part of the movement left for this frame: the whole of it past the stabilization range.</summary>
		private static float Blend(float ratio, float delta)
			=> ratio >= 1f ? 1f : 1f - Mathf.Pow(1f - Mathf.Max(ratio, 0f), delta * 90f);

		/// <summary>
		/// Origin and forward of the ray the user sees (the stabilizer when it is enabled), so a grab aimed with the
		/// line uses the same ray as the line itself.
		/// </summary>
		public Transform RayOrigin
			=> CastOrigin;

		/// <summary>
		/// Whether the ray currently points at something the hand can grab, and where. Published by the distance grabber
		/// of the hand (it lives in another assembly, so it pushes instead of being read here): the ray shows the same
		/// hover on a grabbable as on an element, and stops on it.
		/// </summary>
		public bool GrabHover { get; set; }

		/// <summary>Point the ray meets the grabbable it can take (see <see cref="GrabHover"/>).</summary>
		public Vector3 GrabPoint { get; set; }

		/// <summary>Whether <see cref="HoldPoint"/> is set: the point of the held object the ray touched.</summary>
		public bool HoldPointValid { get; set; }

		/// <summary>
		/// Point of the object the ray holds, where it was touched. The line ends there and not on the pivot, so it stays
		/// attached to what the user aimed at (published by the distance grabber, which owns the hold).
		/// </summary>
		public Vector3 HoldPoint { get; set; }

		/// <summary>
		/// The grabber of this hand holding an object, the near one or the ray (null while the hand is free). The ray only
		/// finds and holds remotely: what it holds is what the line is drawn to.
		/// </summary>
		private IGrabber Holder {
			get {
				EnsureGrabbers();

				for (var i = 0; i < _grabbers.Length; i++)
					if (_grabbers[i] != null && _grabbers[i].State == GrabState.Held)
						return _grabbers[i];

				return null;
			}
		}

		/// <summary>
		/// Whether the hand itself carries an object (a direct grabber holds it): the near/far is then out of the interaction.
		/// </summary>
		private bool HandHolding {
			get {
				EnsureGrabbers();

				for (var i = 0; i < _grabbers.Length; i++) {
					var grabber = _grabbers[i];

					if (grabber != null && grabber.State == GrabState.Held && grabber.Kind == GrabKind.Direct)
						return true;
				}

				return false;
			}
		}

		private void EnsureGrabbers() {
			if (_grabbers == null || _grabbers.Length == 0)
				_grabbers = GetComponents<IGrabber>();
		}

		private Transform CastOrigin
			=> _stabilizer ? _stabilizer : _ray ? _ray.transform : null;

		/// <summary>
		/// Takes over the line renderer of the prefab. The visual of the toolkit is switched off for good: it draws a ray
		/// that stops on nothing as another, straight line, and two writers on one renderer made the ray flicker between
		/// two shapes.
		/// </summary>
		private void BindLine() {
			_line       = _ray ? _ray.GetComponentInChildren<LineRenderer>(true) : null;
			_controller = _ray ? _ray.GetComponentInChildren<CurveVisualController>(true) : null;

			if (_controller)
				_controller.enabled = false;

			if (_line) {
				_line.useWorldSpace = true;
				_line.positionCount = LinePointCount;
				_line.startWidth    = lineWidth;
				_line.endWidth      = lineWidth;
				_line.enabled       = false;
			}

			_lingerTime   = 0f;
			_curved       = false;
			_endDistance  = 0f;
			_curveTime    = 0f;
			_drawDistance = restingLength;
			_restBlend    = 0f;
		}

		/// <summary>
		/// Three steps of colour on the ray: the grey of a surface the ray merely rests on, the washed out accent of a
		/// control it can act on, and the full accent while it is pressed. Nothing else changes with the state.
		/// </summary>
		private void ApplyTheme() {
			_hoverTarget      = null;
			_hoverInteractive = false;

			var theme = ThemeManager.Current;

			var idle  = theme ? theme.Map(ThemeAsset.RayIdle) : ThemeAsset.RayIdle;
			var hover = theme ? theme.Map(ThemeAsset.RayHover) : ThemeAsset.RayHover;
			var press = theme ? theme.Map(ThemeAsset.RayPress) : ThemeAsset.RayPress;

			_idleColor  = idle;
			_hoverColor = Desaturate(hover, hoverDesaturation);
			_pressColor = press;
			_color      = _idleColor;

			ApplyColor();
		}

		/// <summary>Accent of a hovered control, washed out towards grey, the full colour being kept for a press.</summary>
		private static Color Desaturate(Color color, float amount) {
			if (amount <= 0f)
				return color;

			Color.RGBToHSV(color, out var hue, out var saturation, out var value);
			return Color.HSVToRGB(hue, saturation * (1f - Mathf.Clamp01(amount)), value);
		}

		private void ApplyColor() {
			if (!_line)
				return;

			var color = _color;
			color.a = Mathf.Clamp01(lineOpacity);

			_colors[0] = new GradientColorKey(color, 0f);
			_colors[1] = new GradientColorKey(color, 1f);
			_alphas[0] = new GradientAlphaKey(0f, 0f);
			_alphas[1] = new GradientAlphaKey(color.a, 1f);

			_gradient.SetKeys(_colors, _alphas);
			_line.colorGradient = _gradient;
		}

		/// <summary>The colour follows the state of the ray, with the same fade whatever the step.</summary>
		private void UpdateColor(bool pressed, bool hovered) {
			var target = pressed ? _pressColor : hovered ? _hoverColor : _idleColor;
			var factor = uiHoverFadeSpeed > 0f ? 1f - Mathf.Exp(-uiHoverFadeSpeed * Time.unscaledDeltaTime) : 1f;
			var next   = Color.Lerp(_color, target, factor);

			if (next == _color)
				return;

			_color = next;
			ApplyColor();
		}

		private void Update() {
			if (!_ray)
				return;

			Stabilize();

			if (!_physical)
				_physical = GetComponent<HandPhysical>();

			var holder = Holder;

			// Something may switch the visual back on with the interactor: it must never draw next to this component.
			if (_controller && _controller.enabled)
				_controller.enabled = false;

			var handedness = Handedness();
			if (_ray.Hand != handedness)
				_ray.Hand = handedness;

			var grabbing = holder != null;
			var pressing = UiPressed();
			var poking = !pressing && !_pressed && PokeSettings.DisableNearFar && PokeHoldsRay();

			// A hold of the hand itself takes the whole near/far out: the hand carries the object, so the ray has nothing left
			// to point at or to take. A hold of the ray keeps the line, which stays drawn on what it carries.
			var busy = HandHolding;

			_pressed = pressing;

			// a held object takes the ray out of the interaction, not out of the view: the line keeps being drawn
			_rayEnabled  = !grabbing && !poking && !busy;
			_lineEnabled = !poking && !busy;
			_ray.Enable  = _rayEnabled;

			GameObject target = null;
			var distance = 0f;

			var hit = _rayEnabled && TryReadHit(out target, out distance);
			var keep = hit || (hitLinger > 0f && Time.unscaledTime - _lingerTime <= hitLinger);

			if (hit)
				_lingerTime = Time.unscaledTime;

			ApplyPress(hit ? target : null, pressing ? _ray.PressValue : 0f);

			var curveEnd = Vector3.zero;
			var type     = _rayEnabled ? ReadCurveEnd(out curveEnd) : EndPointType.None;
			var curved   = type is EndPointType.UI or EndPointType.ValidCastHit or EndPointType.AttachPoint;
			var object3d = type is EndPointType.ValidCastHit or EndPointType.AttachPoint;

			// a grabbable the grabber can take is a hover of its own: the ray of the toolkit stops only on what it knows,
			// and a nox grabbable is not one of its interactables (published by the distance grabber, see GrabHover)
			var canGrab = _rayEnabled && GrabHover;

			if (canGrab && !curved) {
				curveEnd = GrabPoint;
				curved   = true;
			}

			// the hand holds something: the line goes to it, so the pointer does not vanish on the grab (a distant
			// object stays where it was caught, the line keeps it attached to the ray)
			if (grabbing && !poking && holder.HeldGrabbable != null) {
				var node = holder.HeldGrabbable.Target;

				if (node) {
					curveEnd = HoldPointValid ? HoldPoint : node.position;
					curved   = true;
				}
			}

			UpdateHover(hit ? target : keep ? _hoverTarget : null);
			UpdateCurve(curved, curveEnd);
			UpdateColor(pressing || (_rayEnabled && _ray.hasSelection), object3d || _hoverInteractive || canGrab);

			var module = XRInteractorBinder.Current();
			if (module)
				XRInteractorBinder.Bind(_ray, module);
		}

		/// <summary>
		/// Feeds the pressed element the analog value of the press input, so a control that animates its press sinks
		/// by the force of the trigger instead of snapping down: the uGUI press the ray sends is binary.
		/// <para>
		/// The element is resolved only when the ray moves onto another one; it is released with a zero on the frame
		/// the press stops, which is also what puts a control back at rest.
		/// </para>
		/// </summary>
		private void ApplyPress(GameObject hit, float amount) {
			if (_pressTarget is Object dead && !dead) {
				_pressTarget = null;
				_pressHit    = null;
			}

			if (amount <= 0f) {
				if (_pressingUi)
					_pressTarget?.SetPress(0f);

				_pressingUi  = false;
				_pressTarget = null;
				_pressHit    = null;
				return;
			}

			if (hit && hit != _pressHit) {
				var next = hit.GetComponentInParent<IUiPressTarget>(true);

				if (next != _pressTarget) {
					_pressTarget?.SetPress(0f);
					_pressTarget = next;
				}

				_pressHit = hit;
			}

			_pressingUi = true;
			_pressTarget?.SetPress(amount);
		}

		/// <summary>
		/// The element the ray stops on is only tested when it changes: walking the ancestors costs an array of
		/// components, and the ray sits on the same element for as long as the hand does not move.
		/// </summary>
		private void UpdateHover(GameObject target) {
			if (target == _hoverTarget)
				return;

			_hoverTarget      = target;
			_hoverInteractive = target && IsInteractive(target.transform);
		}

		/// <summary>
		/// Keeps track of where the curve ends. On an element the end is the point the ray reaches. Once the ray points at
		/// nothing, the end stays on the same cast axis at the last distance for <see cref="hitLinger"/>, then slides to
		/// the resting length over <see cref="restCurveFade"/>: the line leaves an element with the exact shape it had on
		/// it, only shorter.
		/// </summary>
		private void UpdateCurve(bool curved, Vector3 end) {
			var now = Time.unscaledTime;

			_curved = curved;

			if (curved) {
				_curveEnd    = end;
				_endDistance = Vector3.Distance(CastOrigin.position, end);
				_curveTime   = now;
				_restBlend   = 1f;
			} else if (now - _curveTime > hitLinger) {
				_restBlend = restCurveFade > 0f ? _restBlend * Mathf.Exp(-Time.unscaledDeltaTime / restCurveFade) : 0f;

				if (_restBlend <= 0.005f)
					_restBlend = 0f;
			}

			_drawDistance = Mathf.Lerp(restingLength, _endDistance, _restBlend);
		}

		/// <summary>
		/// Draws the ray, right before the frame is rendered so it follows the hand of that very frame. One quadratic
		/// bezier whatever the ray points at: it leaves the origin along the aim and ends on the cast axis, on the element
		/// when there is one and at <see cref="_drawDistance"/> when there is none. The aim and the stabilized cast axis
		/// differ while the hand moves, which is what bends the line, on an element and on nothing alike.
		/// </summary>
		private void DrawLine() {
			if (!_ray || !_line)
				return;

			if (!_lineEnabled) {
				_line.enabled = false;
				return;
			}
			var cast   = CastOrigin;
			var aim    = _ray.transform;
			var origin = aim.position;
			var end    = _curved ? _curveEnd : cast.position + cast.forward * _drawDistance;
			var length = Vector3.Distance(origin, end);

			if (length < 0.005f) {
				_line.enabled = false;
				return;
			}

			var control = origin + aim.forward * (length * bendRatio);

			for (var i = 0; i < LinePointCount; i++) {
				var t = i / (LinePointCount - 1f);
				var u = 1f - t;

				_linePoints[i] = u * u * origin + 2f * u * t * control + t * t * end;
			}

			_line.positionCount = LinePointCount;
			_line.SetPositions(_linePoints);
			_line.enabled = true;
		}

		/// <summary>
		/// Whether the ray ends on a real point, and where. Read on the interactor, not on the UI raycast of the module:
		/// the latter can stay valid while the interactor already lost its end point.
		/// </summary>
		private EndPointType ReadCurveEnd(out Vector3 end) {
			end = default;

			if (!_ray)
				return EndPointType.None;

			return _ray.TryGetCurveEndPoint(out end, true, true);
		}

		private bool TryReadHit(out GameObject target, out float distance) {
			target   = null;
			distance = 0f;

			var module = XRInteractorBinder.Current();
			if (!module || !_ray || !module.GetTrackedDeviceModel(_ray, out var model) || !model.currentRaycast.isValid)
				return false;

			target   = model.currentRaycast.gameObject;
			distance = model.currentRaycast.distance;
			return target;
		}

		/// <summary>
		/// Whether the element under the ray is a control: a <see cref="Selectable"/>, an <see cref="EventTrigger"/> or an
		/// object with a pointer handler, at or above the graphic the ray hits. A backdrop is a raycast target without any
		/// of them: the ray stops on it, but there is nothing to press.
		/// </summary>
		private static bool IsInteractive(Transform from) {
			for (var t = from; t; t = t.parent) {
				foreach (var behaviour in t.GetComponents<MonoBehaviour>()) {
					if (!behaviour)
						continue;

					if (behaviour is Selectable
					    || behaviour is EventTrigger
					    || behaviour is IPointerClickHandler
					    || behaviour is IPointerDownHandler
					    || behaviour is IPointerUpHandler
					    || behaviour is IPointerEnterHandler
					    || behaviour is IPointerExitHandler
					    || behaviour is IDragHandler
					    || behaviour is IBeginDragHandler
					    || behaviour is IEndDragHandler
					    || behaviour is IDropHandler
					    || behaviour is IScrollHandler)
						return true;
				}
			}

			return false;
		}

		/// <summary>
		/// Whether the ray is pressing the UI. The poke gating must not cut a ray that owns a press: the interactor
		/// would leave the UI module and drop the pointer mid gesture, the scroll below it with it.
		/// </summary>
		private bool UiPressed() {
			var module = XRInteractorBinder.Current();
			return module && _ray && module.GetTrackedDeviceModel(_ray, out var model) && model.select;
		}

		private bool PokeHoldsRay() {
			var stop = _held ? pokeRayStopDistance * Mathf.Max(pokeRayReleaseFactor, 1f) : pokeRayStopDistance;
			_held = PokeTargetDistance() <= stop;
			return _held;
		}

		private float PokeTargetDistance() {
			var pokes = _physical ? _physical.Pokes : null;
			if (pokes == null)
				return float.PositiveInfinity;

			var module = XRInteractorBinder.Current();
			var nearest = float.PositiveInfinity;

			for (var i = 0; i < pokes.Count; i++) {
				var poke = pokes[i].poke;
				if (poke == null || !poke.isActiveAndEnabled)
					continue;

				if (poke.hasSelection)
					return 0f;

				var data = poke.pokeStateData.Value;
				if (data.target != null) {
					var point = poke.PokePoint;
					nearest = Mathf.Min(nearest, Vector3.Distance(point, data.axisAlignedPokeInteractionPoint));
				}

				if (module && module.GetTrackedDeviceModel(poke, out var model) && model.currentRaycast.isValid)
					nearest = Mathf.Min(nearest, model.currentRaycast.distance);
			}

			return nearest;
		}

		private void DestroyRay() {
			if (_ray) {
				var caster = _ray.GetComponent<CurveInteractionCaster>();
				if (caster)
					caster.castOrigin = _ray.transform;

				_ray.gameObject.Destroy();
			}

			if (_aim)
				_aim.gameObject.Destroy();

			if (_stabilizer)
				_stabilizer.gameObject.Destroy();

			_ray = null;
			_aim = null;
			_stabilizer = null;
			_stabilizeTime = 0f;
			_held = false;
			_pressed = false;

			_line = null;
			_controller = null;
			_color = _idleColor;
			_hoverTarget = null;
			_hoverInteractive = false;
			_lingerTime = 0f;

			_rayEnabled = false;
			_curved = false;
			_endDistance = 0f;
			_curveTime = 0f;
			_drawDistance = 0f;
			_restBlend = 0f;
		}
	}
}
