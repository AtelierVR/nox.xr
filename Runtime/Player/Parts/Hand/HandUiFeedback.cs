using System.Collections.Generic;
using Nox.CCK.Theme;
using Nox.CCK.UI;
using Nox.XR.Runtime.Connectors;
using Nox.XR.Runtime.Settings;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;
namespace Nox.XR.Player {
	/// <summary>
	/// Feeds the hands of a <see cref="Player"/> to the UI: every poke and every near/far ray of the hands is
	/// published to <see cref="UIPointer"/> (so the scroll fixes follow them, and a drag started on a button
	/// scrolls its list), and the element a pointer can touch is marked with a low opacity ring.
	/// </summary>
	[AddComponentMenu("Nox/XR/Player/Hand UI Feedback")]
	public class HandUiFeedback : MonoBehaviour {
		private sealed class Pointer : IUiPointerSource {
			public HandUiFeedback Owner;
			public IUIInteractor Interactor;

			private Vector3 _world;
			private bool _hasWorld;
			private bool _selecting;
			private Transform _lockSpace;
			private ScrollRect _scroll;
			private Transform _scrollSpace;
			private Vector3 _scrollLocal;
			private Vector3 _scrollPoint;
			private Vector3 _holdPoint;
			private bool _clamped;
			private readonly List<Vector3> _lockLocal = new();
			private readonly List<Vector3> _lockWorld = new();
			private Canvas _canvas;

			private bool Held
				=> _lockSpace != null || _scroll != null;

			/// <summary>
			/// Drops everything the pointer holds, including the points frozen on the interactor: a pointer removed in the
			/// middle of a press (the element it pressed was destroyed with its menu) would otherwise leave the ray of the
			/// hand frozen in the world, and that hand no longer hits any UI.
			/// </summary>
			public void LetGo() {
				if (Interactor is NearFarInteractor near)
					near.ReleaseUiPointer();

				_selecting   = false;
				_lockSpace   = null;
				_scroll      = null;
				_scrollSpace = null;
				_clamped     = false;
				_lockLocal.Clear();
				_lockWorld.Clear();
			}

			public bool TryRead(out UiPointerState state) {
				state = default;

				var module = XRInteractorBinder.Current();
				if (!module || Interactor == null)
					return false;

				if (!module.GetTrackedDeviceModel(Interactor, out var model))
					return false;

				HoldPointer(model);

				var hit = model.currentRaycast;
				if (!hit.isValid && !model.select)
					return false;

				var target = hit.isValid ? hit.gameObject : null;
				var canvas = target ? target.GetComponentInParent<Canvas>() : _canvas;
				var onSurface = hit.isValid;

				if (hit.isValid) {
					_canvas   = canvas;
					_world    = _clamped
						? _scrollPoint
						: Held && TryLiveOnCanvas(canvas, out var live)
							? live
							: hit.worldPosition;
					_hasWorld = true;
				} else if (_clamped) {
					hit = new RaycastResult {
						worldPosition = _holdPoint,
						worldNormal   = _canvas ? -_canvas.transform.forward : Vector3.back
					};
					_world    = _scrollPoint;
					_hasWorld = true;
					onSurface = true;
				} else if (canvas && TryOnCanvas(canvas, model.position, model.orientation * Vector3.forward, out var point, out var normal)) {
					hit = new RaycastResult { worldPosition = point, worldNormal = normal, distance = 0f };
					_world    = point;
					_hasWorld = true;
					onSurface = true;
				} else if (!_hasWorld) {
					_world    = model.position;
					_hasWorld = true;
				}

				var camera = _canvas && _canvas.worldCamera
					? _canvas.worldCamera
					: Owner ? Owner.ViewCamera : Camera.main;

				state.Pressed    = model.select;
				state.HasTarget  = hit.isValid && hit.gameObject;
				state.OnSurface  = onSurface;
				state.Hit        = hit;
				state.Camera     = camera;
				state.Scroll     = model.scrollDelta.y;
				state.World      = _world;
				state.HasWorld   = true;
				state.Position   = camera
					? RectTransformUtility.WorldToScreenPoint(camera, _world)
					: Vector2.zero;

				return true;
			}

			/// <summary>
			/// A press keeps the UI pointer on the element it started on, so the ray may move without the cursor sliding
			/// onto the neighbours. The points are kept in the space of that element: while its list scrolls the pointer
			/// stays on the same control. A slider or a scrollbar reads the ray itself, so it is never held.
			/// <para>
			/// A press that drags a list is held differently: the row it started on scrolls out of the visible area, and a
			/// pointer following it ends up over a masked element, which the UI raycast drops (the ray then blinks between
			/// the list and nothing). The pointer stops on the border of the visible area instead.
			/// </para>
			/// </summary>
			private void HoldPointer(in TrackedDeviceModel model) {
				if (Interactor is not Nox.XR.Runtime.Connectors.NearFarInteractor near)
					return;

				if (!model.select) {
					LetGo();
					return;
				}

				// The decision is made once, at the press: a press started in the void must not capture the first control
				// the ray sweeps over, that one is a scroll grab.
				if (!_selecting) {
					_selecting = true;

					var hit    = model.currentRaycast;
					var points = model.raycastPoints;

					if (hit.isValid && hit.gameObject && !IsDraggable(hit.gameObject) && points != null && points.Count > 0) {
						_scroll = Scrollable(hit.gameObject);

						if (_scroll) {
							// The pointer keeps its place in the content: the rows stay under the hand instead of sliding
							// through it.
							_scrollSpace = _scroll.content ? _scroll.content : _scroll.transform;
							_scrollLocal = _scrollSpace.InverseTransformPoint(hit.worldPosition);
							_scrollPoint = hit.worldPosition;
						} else {
							_lockSpace = hit.gameObject.transform;
							_lockLocal.Clear();

							for (var i = 0; i < points.Count; i++)
								_lockLocal.Add(_lockSpace.InverseTransformPoint(points[i]));
						}
					}
				}

				if (_scroll) {
					HoldOnScroll(near);
					return;
				}

				_clamped = false;

				if (!_lockSpace) {
					near.ReleaseUiPointer();
					return;
				}

				_lockWorld.Clear();

				for (var i = 0; i < _lockLocal.Count; i++)
					_lockWorld.Add(_lockSpace.TransformPoint(_lockLocal[i]));

				near.HoldUiPointer(_lockWorld);
			}

			/// <summary>
			/// Holds the pointer on the list: once grabbed it keeps its place <b>in the content</b>, so the rows stay under the
			/// hand instead of sliding through it, and it stops on the border of the visible area instead of running over the
			/// elements the mask hides. The list follows the hand one to one, so the grabbed place stays on the ray; it clamps
			/// on its own ends, and only there does the pointer leave the ray.
			/// </summary>
			private void HoldOnScroll(NearFarInteractor near) {
				_clamped = false;
				_lockWorld.Clear();

				var view = Viewport(_scroll);
				if (!view || !_scrollSpace || !TryReadRay(out var origin, out var direction))
					return;

				_scrollPoint = DragPoint(view, origin, direction);
				_holdPoint   = Clamp(view, _scrollSpace.TransformPoint(_scrollLocal));
				_clamped     = true;

				_lockWorld.Add(origin);
				_lockWorld.Add(_holdPoint + (_holdPoint - origin).normalized * Overshoot);

				near.HoldUiPointer(_lockWorld);
			}

			private static RectTransform Viewport(ScrollRect scroll)
				=> scroll && scroll.isActiveAndEnabled
					? scroll.viewport ? scroll.viewport : scroll.transform as RectTransform
					: null;

			/// <summary>
			/// Point the scroll follows: where the ray crosses the plane of the list, or the ray taken to its far end when it
			/// does not cross it any more (aiming past the panel). It never freezes, so the list can always be dragged to its
			/// own ends, and stops only there.
			/// </summary>
			private static Vector3 DragPoint(RectTransform view, Vector3 origin, Vector3 direction) {
				var plane    = new Plane(-view.forward, view.position);
				var distance = plane.Raycast(new Ray(origin, direction), out var enter) ? enter : MaxDragDistance;

				return origin + direction * Mathf.Min(Mathf.Max(distance, 0f), MaxDragDistance);
			}

			/// <summary>How far along the ray the drag is read when the ray no longer crosses the list.</summary>
			private const float MaxDragDistance = 20f;

			/// <summary>Point of the visible area: kept inside it, on its plane, the border is left out by a hair.</summary>
			private static Vector3 Clamp(RectTransform view, Vector3 world) {
				var rect  = view.rect;
				var local = view.InverseTransformPoint(world);
				var inset = Mathf.Min(rect.width, rect.height) * 0.002f;

				local.x = Mathf.Clamp(local.x, rect.xMin + inset, rect.xMax - inset);
				local.y = Mathf.Clamp(local.y, rect.yMin + inset, rect.yMax - inset);
				local.z = 0f;

				return view.TransformPoint(local);
			}

			/// <summary>
			/// Live origin and direction of the ray, read on the interactor itself. The far caster is not read back: it is
			/// stabilized, and its stabilizer aims at the endpoint this class holds (see <c>IXRRayProvider.rayEndPoint</c>),
			/// so following it would close a loop where the ray chases the point it is holding and the scroll locks up.
			/// </summary>
			private bool TryReadRay(out Vector3 origin, out Vector3 direction) {
				origin    = default;
				direction = default;

				if (Interactor is not Component component)
					return false;

				var transform = component.transform;
				origin    = transform.position;
				direction = transform.forward;
				return true;
			}

			/// <summary>How far past the plane the held ray is pushed, so a point on the plane is still a hit of the segment.</summary>
			private const float Overshoot = 0.002f;

			/// <summary>
			/// Where the ray is now, projected on the canvas: the scroll keeps following the ray while the pointer itself
			/// stays on the pressed element. It reads the live transform of the interactor, not the model, which is not
			/// refreshed on every frame and would hand back the held points.
			/// </summary>
			private bool TryLiveOnCanvas(Canvas canvas, out Vector3 point) {
				point = default;

				if (!canvas || Interactor is not Component ray)
					return false;

				var transform = ray.transform;
				return TryOnPlane(canvas, transform.position, transform.forward, out point);
			}

			/// <summary>Point where the pointer crosses the plane of the canvas, when it lands inside its rect.</summary>
			private static bool TryOnCanvas(Canvas canvas, Vector3 origin, Vector3 direction, out Vector3 point, out Vector3 normal) {
				point  = default;
				normal = default;

				var rect = canvas ? canvas.transform as RectTransform : null;
				if (!rect)
					return false;

				if (!TryOnPlane(canvas, origin, direction, out point))
					return false;

				normal = -canvas.transform.forward;

				var local     = rect.InverseTransformPoint(point);
				var localRect = rect.rect;

				return local.x >= localRect.xMin && local.x <= localRect.xMax
					&& local.y >= localRect.yMin && local.y <= localRect.yMax;
			}

			private static bool TryOnPlane(Canvas canvas, Vector3 origin, Vector3 direction, out Vector3 point)
				=> TryOnPlane(canvas ? canvas.transform : null, origin, direction, out point);

			private static bool TryOnPlane(Transform surface, Vector3 origin, Vector3 direction, out Vector3 point) {
				point = default;

				if (!surface)
					return false;

				var plane = new Plane(-surface.forward, surface.position);
				if (!plane.Raycast(new Ray(origin, direction), out var distance))
					return false;

				point = origin + direction * distance;
				return true;
			}
		}

		[SerializeField, Tooltip("Player whose hands are published to the UI. Found in the parents when empty.")]
		private Player player;

		[Header("Hover indicator")]
		[SerializeField, Tooltip("Diameter of the hover dot, in metres.")]
		private float dotDiameter = 0.008f;

		[SerializeField, Range(0f, 1f), Tooltip("Opacity of the hover dot.")]
		private float dotOpacity = 0.35f;

		private const int DotCount = 4;

		private readonly List<Pointer> _pointers = new();
		private readonly List<SpriteRenderer> _dots = new();
		private readonly List<Hand> _hands = new();
		private readonly List<UiPointerState> _states = new();

		/// <summary>Whether a pointer currently rests on a scrollable list (read by the movement driver).</summary>
		public static bool ScrollableFocused { get; private set; }

		private bool _dirty = true;
		private bool _wired;

		public Player Player {
			get => player;
			set {
				if (player == value)
					return;

				if (isActiveAndEnabled)
					Unwire();

				player = value;

				if (isActiveAndEnabled)
					Wire();

				_dirty = true;
			}
		}

		private void Awake() {
			if (!player)
				player = GetComponentInParent<Player>();

			for (var i = 0; i < DotCount; i++)
				_dots.Add(CreateDot(i));
		}

		private void OnEnable() {
			if (!player)
				player = GetComponentInParent<Player>();

			Wire();
			_dirty = true;
		}

		private void OnDisable() {
			Unwire();
			Release();
		}

		private void Wire() {
			if (_wired || !player)
				return;

			_wired = true;

			player.OnPartRegistered.AddListener(OnPartChanged);
			player.OnPartUnregistered.AddListener(OnPartChanged);
			ThemeManager.OnThemeChanged.AddListener(OnThemeChanged);

			foreach (var pair in player.Parts)
				if (pair.Value is Hand hand)
					Subscribe(hand);
		}

		private void Unwire() {
			if (!_wired)
				return;

			_wired = false;

			if (player) {
				player.OnPartRegistered.RemoveListener(OnPartChanged);
				player.OnPartUnregistered.RemoveListener(OnPartChanged);
			}

			ThemeManager.OnThemeChanged.RemoveListener(OnThemeChanged);

			for (var i = 0; i < _hands.Count; i++)
				if (_hands[i])
					_hands[i].SourceChanged.RemoveListener(OnHandSourceChanged);

			_hands.Clear();
		}

		private void OnPartChanged(Part part) {
			if (part is Hand hand)
				Subscribe(hand);

			_dirty = true;
		}

		private void Subscribe(Hand hand) {
			if (!hand || _hands.Contains(hand))
				return;

			_hands.Add(hand);
			hand.SourceChanged.RemoveListener(OnHandSourceChanged);
			hand.SourceChanged.AddListener(OnHandSourceChanged);
		}

		private void OnHandSourceChanged(Nox.Avatars.Hand.IHand source)
			=> _dirty = true;

		private void OnThemeChanged(string theme)
			=> ApplyDotColor();

		private void LateUpdate() {
			if (_dirty) {
				_dirty = false;
				Rebuild();
			}

			var show = PokeSettings.HoverIndicator;
			var used = 0;
			var scrollable = false;

			_states.Clear();
			for (var i = 0; i < _pointers.Count; i++)
				if (_pointers[i].TryRead(out var state))
					_states.Add(state);

			for (var i = 0; i < _states.Count; i++) {
				var state = _states[i];

				if (state.HasTarget && !scrollable && IsScrollable(state.Hit.gameObject))
					scrollable = true;

				if (!show || !state.OnSurface || used >= _dots.Count)
					continue;

				Place(_dots[used++], state);
			}

			ScrollableFocused = scrollable;

			for (var i = used; i < _dots.Count; i++) {
				if (_dots[i].enabled)
					_dots[i].enabled = false;
			}
		}

		private static bool IsScrollable(GameObject target)
			=> Scrollable(target) != null;

		private static ScrollRect Scrollable(GameObject target) {
			if (!target)
				return null;

			var scroll = target.GetComponentInParent<ScrollRect>();
			return scroll && scroll.isActiveAndEnabled && (scroll.vertical || scroll.horizontal) ? scroll : null;
		}

		/// <summary>True for the controls that read the pointer itself: a slider or a scrollbar keeps following the ray.</summary>
		private static bool IsDraggable(GameObject target) {
			if (!target)
				return false;

			return target.GetComponentInParent<Slider>() || target.GetComponentInParent<Scrollbar>();
		}

		private void Place(SpriteRenderer dot, in UiPointerState state) {
			var hit = state.Hit;
			var normal = hit.worldNormal;

			if (normal.sqrMagnitude < 0.0001f)
				normal = state.Camera ? -state.Camera.transform.forward : Vector3.up;

			var transformDot = dot.transform;
			transformDot.SetPositionAndRotation(
				hit.worldPosition + normal * 0.002f,
				Quaternion.LookRotation(normal, Vector3.up)
			);
			transformDot.localScale = Vector3.one * dotDiameter;

			if (!dot.enabled)
				dot.enabled = true;
		}

		private void Rebuild() {
			Release();

			if (!player)
				return;

			foreach (var pair in player.Parts) {
				if (pair.Value is not Hand hand)
					continue;

				var found = hand.GetComponentsInChildren<PokeInteractor>(true);
				for (var i = 0; i < found.Length; i++)
					Add(found[i]);

				var ray = hand.GetComponent<HandNearFar>();
				if (ray && ray.Ray)
					Add(ray.Ray);
			}

			ApplyDotColor();
		}

		private void Add(IUIInteractor interactor) {
			if (interactor == null)
				return;

			for (var i = 0; i < _pointers.Count; i++)
				if (ReferenceEquals(_pointers[i].Interactor, interactor))
					return;

			var pointer = new Pointer { Owner = this, Interactor = interactor };
			_pointers.Add(pointer);
			UIPointer.Add(pointer);
		}

		/// <summary>Camera the UI is rendered with when the canvas does not name one.</summary>
		private Camera ViewCamera {
			get {
				if (player && player.headCamera)
					return player.headCamera;

				return Camera.main;
			}
		}

		private void Release() {
			for (var i = 0; i < _pointers.Count; i++) {
				_pointers[i].LetGo();
				UIPointer.Remove(_pointers[i]);
			}

			_pointers.Clear();
			ScrollableFocused = false;

			for (var i = 0; i < _dots.Count; i++)
				_dots[i].enabled = false;
		}

		private SpriteRenderer CreateDot(int index) {
			var go = new GameObject($"UI Hover Dot {index}");
			go.transform.SetParent(transform, false);

			var dot = go.AddComponent<SpriteRenderer>();
			dot.sprite = CircleSprite;
			dot.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
			dot.receiveShadows = false;
			dot.enabled = false;
			return dot;
		}

		private static Sprite _circle;

		/// <summary>Soft edged filled circle, built once and shared by every dot.</summary>
		private static Sprite CircleSprite {
			get {
				if (_circle)
					return _circle;

				const int size = 64;
				var texture = new Texture2D(size, size, TextureFormat.RGBA32, false) {
					wrapMode   = TextureWrapMode.Clamp,
					filterMode = FilterMode.Bilinear
				};

				var pixels = new Color32[size * size];
				const float radius = size * 0.5f;

				for (var y = 0; y < size; y++)
				for (var x = 0; x < size; x++) {
					var dx = x + 0.5f - radius;
					var dy = y + 0.5f - radius;
					var alpha = Mathf.Clamp01(radius - 1f - Mathf.Sqrt(dx * dx + dy * dy));
					pixels[y * size + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(alpha * 255f));
				}

				texture.SetPixels32(pixels);
				texture.Apply();

				_circle = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), size);
				return _circle;
			}
		}

		private void ApplyDotColor() {
			var theme = ThemeManager.Current;
			var color = theme ? theme.Map(ThemeAsset.Accent) : ThemeAsset.Accent;
			color.a = Mathf.Clamp01(dotOpacity);

			for (var i = 0; i < _dots.Count; i++)
				_dots[i].color = color;
		}
	}
}
