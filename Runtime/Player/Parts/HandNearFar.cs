using Nox.Avatars.Hand;
using Nox.CCK.Theme;
using Nox.CCK.Utils;
using Nox.Interactions;
using Nox.XR.Runtime;
using Nox.XR.Runtime.Connectors;
using Nox.XR.Runtime.Settings;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Interactors.Casters;
using UnityEngine.XR.Interaction.Toolkit.Interactors.Visuals;
using Logger = Nox.CCK.Utils.Logger;
using NearFarInteractor = Nox.XR.Runtime.Connectors.NearFarInteractor;

namespace Nox.XR.Player {
	[AddComponentMenu("Nox/XR/Player/Hand Near Far")]
	[RequireComponent(typeof(Hand))]
	[DefaultExecutionOrder(-100)]
	public class HandNearFar : MonoBehaviour {
		private const string PREFAB_ADDRESS = "near_far_interactor.prefab";

		[SerializeField, Tooltip("Hand part of this side. Found on this GameObject when empty.")]
		private Hand hand;

		[SerializeField, Tooltip("Distance (world metres) between a fingertip and its poke target under which the ray stops interacting.")]
		private float pokeRayStopDistance = 0.15f;

		[SerializeField, Tooltip("Factor over the stop distance before the ray is allowed back, so it cannot blink at the threshold.")]
		private float pokeRayReleaseFactor = 1.6f;

		[SerializeField, Tooltip("Opacity of the ray at its end.")]
		private float lineOpacity = 0.9f;

		[Header("Aim")]
		[SerializeField, Tooltip("Origin of the ray in the palm space: fixed on the hand, it never lags behind.")]
		private Vector3 aimPosition = new(-0.1013f, -0.0707f, -0.0222f);

		[SerializeField, Tooltip("Rotation of the ray in the palm space.")]
		private Vector3 aimEuler = new(76.566f, 256.477f, 356.3f);

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
		private HandPokes _pokes;
		private IGrabber _grabber;
		private bool _held;
		private bool _pressed;

		public NearFarInteractor Ray
			=> _ray;

		private void Reset()
			=> hand = GetComponent<Hand>();

		private void Awake() {
			if (!hand)
				hand = GetComponent<Hand>();
		}

		private void OnEnable() {
			if (!hand)
				return;

			hand.SourceChanged.RemoveListener(OnSourceChanged);
			hand.SourceChanged.AddListener(OnSourceChanged);
			ThemeManager.OnThemeChanged.RemoveListener(OnThemeChanged);
			ThemeManager.OnThemeChanged.AddListener(OnThemeChanged);

			OnSourceChanged(hand.Source);
		}

		private void OnDisable() {
			if (hand)
				hand.SourceChanged.RemoveListener(OnSourceChanged);

			ThemeManager.OnThemeChanged.RemoveListener(OnThemeChanged);

			DestroyRay();
		}

		private void OnThemeChanged(string theme)
			=> ApplyTheme();

		private void OnSourceChanged(IHand source) {
			DestroyRay();

			if (source == null)
				return;

			var client = Client.Instance;
			var prefab = client != null ? client.InstantiateAsset(PREFAB_ADDRESS) : null;
			if (!prefab) {
				Logger.LogWarning($"Near/far interactor prefab not found: {PREFAB_ADDRESS}", this, tag: nameof(HandNearFar));
				return;
			}

			ResolveAnchor(source, out var parent, out var position, out var rotation);

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
			ray.Hand = hand.Side == HandSide.Left ? InteractorHandedness.Left : InteractorHandedness.Right;

			_ray = ray;

			ConfigureStabilization();

			ApplyMaterial();

			ApplyTheme();
		}

		/// <summary>
		/// Where the ray starts on the hand, fixed in the palm space: the origin follows the hand rigidly, the
		/// smoothing is left to the casters so the line starts exactly where the hand says.
		/// </summary>
		private void ResolveAnchor(IHand source, out Transform parent, out Vector3 position, out Quaternion rotation) {
			if (source.NearFar) {
				parent   = source.NearFar;
				position = Vector3.zero;
				rotation = Quaternion.identity;
				return;
			}

			parent   = source.Palm ? source.Palm : source.Anchor;
			position = aimPosition;
			rotation = Quaternion.Euler(aimEuler);
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
		/// <para>
		/// While the UI pointer is held, the ray is turned towards that point instead: the held point sits where the player
		/// grabbed, off the aim, and letting the caster keep its aim would bow the line towards the pointer.
		/// </para>
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

			if (_ray.HasHeldPointer && _ray.TryGetCurveEndPoint(out var end) != EndPointType.None) {
				var direction = end - position;
				if (direction.sqrMagnitude > 1e-8f)
					rotation = Quaternion.LookRotation(direction);
			}

			_stabilizer.SetPositionAndRotation(position, rotation);
		}

		/// <summary>Part of the movement left for this frame: the whole of it past the stabilization range.</summary>
		private static float Blend(float ratio, float delta)
			=> ratio >= 1f ? 1f : 1f - Mathf.Pow(1f - Mathf.Max(ratio, 0f), delta * 90f);

		private void ApplyTheme() {
			if (!_ray)
				return;

			var visual = _ray.GetComponentInChildren<CurveVisualController>(true);
			if (!visual)
				return;

			var theme = ThemeManager.Current;

			visual.customizeLinePropertiesForState = true;

			Apply(visual.noValidHitProperties, theme ? theme.Map(ThemeAsset.RayIdle) : ThemeAsset.RayIdle);
			Apply(visual.hoverHitProperties, theme ? theme.Map(ThemeAsset.RayHover) : ThemeAsset.RayHover);
			Apply(visual.uiHitProperties, theme ? theme.Map(ThemeAsset.RayHover) : ThemeAsset.RayHover);
			Apply(visual.selectHitProperties, theme ? theme.Map(ThemeAsset.RayPress) : ThemeAsset.RayPress);
			Apply(visual.uiPressHitProperties, theme ? theme.Map(ThemeAsset.RayPress) : ThemeAsset.RayPress);
		}

		private void Apply(LineProperties properties, Color color) {
			if (properties == null)
				return;

			properties.adjustGradient = true;
			properties.gradient = BuildGradient(color);
		}

		/// <summary>
		/// The built in line material brightens the gradient: the ray is drawn with the sprite shader instead,
		/// the same one as the UI, so a dark theme color stays dark on screen.
		/// </summary>
		private void ApplyMaterial() {
			if (!_ray)
				return;

			var line = _ray.GetComponentInChildren<LineRenderer>(true);
			if (!line)
				return;

			var shader = Shader.Find("Sprites/Default");
			if (shader)
				line.material = new Material(shader) { name = "Nox Ray" };
		}

		private Gradient BuildGradient(Color color) {
			color.a = Mathf.Clamp01(lineOpacity);

			return new Gradient {
				colorKeys = new[] { new GradientColorKey(color, 0f), new GradientColorKey(color, 1f) },
				alphaKeys = new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(color.a, 1f) }
			};
		}

		private void Update() {
			if (!_ray)
				return;

			Stabilize();

			if (!_pokes)
				_pokes = GetComponent<HandPokes>();

			if (_grabber == null)
				_grabber = GetComponent<IGrabber>();

			var grabbing = _grabber != null && _grabber.State == GrabState.Held;
			var pressing = UiPressed();
			var poking = !pressing && !_pressed && PokeSettings.DisableNearFar && PokeHoldsRay();

			_pressed = pressing;

			_ray.Enable = !grabbing && !poking;

			var module = XRInteractorBinder.Current();
			if (module)
				XRInteractorBinder.Bind(_ray, module);
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
			var pokes = _pokes != null ? _pokes.Pokes : null;
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
					var point = poke.attachTransform != null ? poke.attachTransform.position : poke.transform.position;
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
		}
	}
}
