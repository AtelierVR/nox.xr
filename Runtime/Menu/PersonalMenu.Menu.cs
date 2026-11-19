using Cysharp.Threading.Tasks;
using Nox.CCK.Interactions;
using Nox.Interactions;
using Nox.UI;
using Nox.XR.Runtime.Settings;
using UnityEngine;

namespace Nox.XR.Menu {
	/// <summary>
	/// Hosting and placement of the menu: the platform half of the panel.
	/// <para>
	/// <see cref="Container"/> is the rect the UI mod builds the menu into; the transform of this component
	/// is the panel around it, the object the player sees and a hand carries. This part only reaches the
	/// container, shows and hides it, and puts the panel in front of the view when it opens.
	/// </para>
	/// </summary>
	public partial class PersonalMenu : IMenuProvider {
		/// <summary>
		/// Container of the menu: the rect <see cref="IUiAPI.Make"/> builds into, with its own size and scale.
		/// <para>
		/// It comes from outside - a rect assigned in the prefab or set by code - because the platform is
		/// what decides how big the menu is. It stays a child of the panel: nothing is built or moved here.
		/// </para>
		/// </summary>
		[SerializeField, Tooltip("Rect the UI mod builds the menu into.")]
		private RectTransform _container;

		/// <summary>
		/// Body of the panel: the board the player sees and the collider a hand hits, a child of the panel
		/// and outside the container. It is toggled with the menu - the panel root stays alive, because it
		/// carries this component (toggle bindings, placement, carried pose).
		/// </summary>
		[SerializeField, Tooltip("Board of the panel (renderer and collider), toggled with the menu.")]
		private Transform _body;

		private GrabbableBase _grabbable;

		/// <summary>Container the UI mod builds the menu into: what the menu shows and hides.</summary>
		public RectTransform Container {
			get => _container;
			set => _container = value;
		}

		/// <summary>World pose the panel is held at while it is a child of the rig.</summary>
		private Vector3 _anchorPosition;
		private Quaternion _anchorRotation = Quaternion.identity;

		/// <summary>Same place, expressed in the space of the player: what <see cref="XRMenuAnchor.Player"/> follows.</summary>
		private Vector3 _anchorLocalPosition;
		private Quaternion _anchorLocalRotation = Quaternion.identity;

		/// <summary>Same place, expressed in the view frame (eye position, flattened yaw): what
		/// <see cref="XRMenuAnchor.Head"/> follows.</summary>
		private Vector3 _anchorHeadPosition;
		private Quaternion _anchorHeadRotation = Quaternion.identity;

		private bool _hasAnchor;

		[SerializeField, Tooltip("Space the panel travels with. Defaults to the parent of the panel.")]
		private Transform _origin;

		[SerializeField, Tooltip("Camera the panel is placed in front of. Defaults to the main camera.")]
		private Camera _view;

		/// <summary>Space the panel travels with (<see cref="XRMenuAnchor.Player"/> and the recall distance).</summary>
		public Transform Origin {
			get {
				if (!_origin)
					_origin = transform.parent;

				return _origin;
			}
			set => _origin = value;
		}

		/// <summary>Camera the panel is placed in front of, and measured against for the recall.</summary>
		public Camera View {
			get {
				if (!_view)
					_view = Camera.main;

				return _view;
			}
			set => _view = value;
		}

		/// <summary>Distance in front of the view where the menu opens, in metres.</summary>
		public float Distance {
			get => distance;
			set => distance = Mathf.Max(0.1f, value);
		}


		/// <summary>Menu of this player, or <c>null</c> while the UI mod has not built it yet.</summary>
		public IMenu Menu
			=> _menu;

		/// <summary>Whether the menu is currently shown: the container is what the menu toggles.</summary>
		public bool IsOpen
			=> _container && _container.gameObject.activeSelf;

		/// <summary>Grabbable of the panel: what lets a hand carry it (found on this object).</summary>
		private GrabbableBase Grabbable {
			get {
				if (!_grabbable)
					_grabbable = GetComponent<GrabbableBase>();

				return _grabbable;
			}
		}

		/// <summary>Whether a hand is carrying the panel: it follows the hand instead of its anchor.</summary>
		private bool Held
			=> Grabbable != null && Grabbable.State == GrabState.Held;

		/// <summary>
		/// Shown state of the menu. The container is toggled, never the content alone: the UI mod builds the
		/// menu once, and hiding its pages would leave a canvas rendering an empty menu.
		/// </summary>
		public bool Active {
			get => IsOpen;
			set => SetActive(value);
		}

		/// <summary>Shows the menu in front of the view, or hides it.</summary>
		public void Toggle() {
			if (IsOpen)
				Close();
			else
				Open();
		}

		/// <summary>Shows the menu in front of the view.</summary>
		public void Open() {
			_openRequested = true;

			if (_menu == null) {
				Generate(_context != null ? _context.Token : default).Forget();
				return;
			}

			// A menu dropped in the world is not placed again: the player put it there on purpose.
			PlaceInFrontOfView();

			_menu.Active = true;
		}

		/// <summary>Hides the menu.</summary>
		public void Close() {
			_openRequested = false;
			SetActive(false);
		}

		/// <summary>
		/// Toggles what the menu shows: the container the UI mod built into, and the body of the panel. The
		/// panel root itself is left active - it carries this component. Deliberately independent from
		/// <see cref="IMenu.Active"/>: the menu reports its state through this provider, so writing back to
		/// it would loop.
		/// </summary>
		private void SetActive(bool value) {
			if (IsOpen == value)
				return;

			if (_container)
				_container.gameObject.SetActive(value);

			if (_body)
				_body.gameObject.SetActive(value);

			if (value)
				Opened.Invoke(this);
			else
				Closed.Invoke(this);
		}

		/// <summary>
		/// Places the panel in front of the view, level and facing the player: it has to be readable whatever
		/// the head does, and a panel that inherits the head roll is unreadable.
		/// <para>
		/// Only the world pose is written: the panel keeps its parent for its whole life. A menu that is
		/// moved this way is still a child of the rig, so it travels with the player rather than being
		/// left behind in the world.
		/// </para>
		/// </summary>
		private void PlaceInFrontOfView() {
			if (!TryGetHeadFrame(out var position, out var rotation))
				return;

			transform.SetPositionAndRotation(position + rotation * Vector3.forward * distance, rotation);
			Anchor();
		}

		/// <summary>
		/// Frame the panel is placed in: the eye position, with the yaw of the view and the pitch and roll
		/// dropped (a panel that inherits them is unreadable).
		/// </summary>
		/// <returns><c>false</c> when the view points straight up or down: there is no level yaw to use.</returns>
		private bool TryGetHeadFrame(out Vector3 position, out Quaternion rotation) {
			var camera = View;
			var origin = camera ? camera.transform : transform;

			var forward = origin.forward;
			forward.y = 0f;

			if (forward.sqrMagnitude < 1e-6f) {
				position = default;
				rotation = Quaternion.identity;
				return false;
			}

			position = origin.position;
			rotation = Quaternion.LookRotation(forward.normalized, Vector3.up);
			return true;
		}

		/// <summary>
		/// Puts the panel where the settings ask for, every frame:
		/// <list type="bullet">
		/// <item><see cref="XRMenuAnchor.Head"/>: right in front of the view, always readable.</item>
		/// <item><see cref="XRMenuAnchor.Player"/>: at the offset it had when the mode was picked, so it travels
		/// with the player but not with the head.</item>
		/// <item><see cref="XRMenuAnchor.World"/>: at the place it was put.</item>
		/// </list>
		/// <para>
		/// Whatever the mode, a panel farther than <see cref="MenuSettings.MaxDistance"/> from the view is brought
		/// back: from there it can neither be read nor grabbed.
		/// </para>
		/// <para>
		/// Only the world pose is written, so the panel keeps the parent it has: it is a child of the rig, and
		/// this is what stops the rig from dragging it around. Held by a hand, it follows the hand instead and
		/// this leaves it alone.
		/// </para>
		/// </summary>
		private void UpdateAnchor() {
			if (!_hasAnchor || Held)
				return;

			var beyondReach = TryGetReach(out var reachOrigin, out var reachLimit);

			if (beyondReach) {
				ApplyRecall(reachOrigin, reachLimit);
				return;
			}

			switch (MenuSettings.Anchor) {
				case XRMenuAnchor.Head:
					if (!TryGetHeadFrame(out var headPosition, out var headRotation))
						return;

					transform.SetPositionAndRotation(
						headPosition + headRotation * _anchorHeadPosition,
						headRotation * _anchorHeadRotation);
					break;

				case XRMenuAnchor.Player:
					var playerTransform = Origin;

					transform.SetPositionAndRotation(
						playerTransform.TransformPoint(_anchorLocalPosition),
						playerTransform.rotation * _anchorLocalRotation);
					break;

				default:
					transform.SetPositionAndRotation(_anchorPosition, _anchorRotation);
					break;
			}
		}

		/// <summary>
		/// Applies what <see cref="MenuSettings.Recall"/> asks for once the panel is out of reach: hide it, bring
		/// it back in front of the view, or hold it at the edge of the reach circle.
		/// </summary>
		private void ApplyRecall(Vector3 reachOrigin, float reachLimit) {
			switch (MenuSettings.Recall) {
				case XRMenuRecall.Despawn:
					Close();
					break;

				case XRMenuRecall.Clamp:
					ClampToReach(reachOrigin, reachLimit);
					break;

				default:
					PlaceInFrontOfView();
					break;
			}
		}

		/// <summary>
		/// Pulls the panel back to the edge of the reach circle without changing its facing: it keeps following at
		/// the maximum distance, like on a leash.
		/// </summary>
		private void ClampToReach(Vector3 reachOrigin, float reachLimit) {
			var offset = transform.position - reachOrigin;
			var distanceToPlayer = offset.magnitude;

			if (distanceToPlayer <= reachLimit || distanceToPlayer < 1e-4f)
				return;

			transform.position = reachOrigin + offset / distanceToPlayer * reachLimit;
			Anchor();
		}

		/// <summary>
		/// Whether the panel is farther from the player camera than <see cref="MenuSettings.MaxDistance"/>, scaled
		/// by the player, and where that reach is centred. Always <c>false</c> when the limit is off.
		/// </summary>
		private bool TryGetReach(out Vector3 origin, out float limit) {
			origin = default;
			limit  = MenuSettings.MaxDistance;

			if (limit <= MenuSettings.NoLimit)
				return false;

			var camera = View;
			var reference = camera
				? camera.transform
				: Origin;
			var scale = Origin.lossyScale;
			var unit = Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z));
			if (unit < 0.01f)
				unit = 1f;

			origin = reference.position;
			limit *= unit;
			return (transform.position - origin).sqrMagnitude > limit * limit;
		}

		/// <summary>
		/// Takes the current world pose of the panel as the place it has to stay at, both in the world and
		/// relative to the player. Called when the menu is placed, when a hand lets it go, and when the anchor
		/// mode changes - the mode decides how it follows from now on, it never moves it.
		/// </summary>
		private void Anchor() {
			_anchorPosition = transform.position;
			_anchorRotation = transform.rotation;

			var playerTransform = Origin;
			_anchorLocalPosition = playerTransform.InverseTransformPoint(_anchorPosition);
			_anchorLocalRotation = Quaternion.Inverse(playerTransform.rotation) * _anchorRotation;

			if (TryGetHeadFrame(out var headPosition, out var headRotation)) {
				_anchorHeadPosition = Quaternion.Inverse(headRotation) * (_anchorPosition - headPosition);
				_anchorHeadRotation = Quaternion.Inverse(headRotation) * _anchorRotation;
			}

			_hasAnchor = true;
		}

		private void OnEnable() {
			MenuSettings.AnchorChanged.AddListener(OnAnchorSettingChanged);

			if (Grabbable != null)
				Grabbable.OnReleasedEvent.AddListener(OnGrabbableReleased);
		}

		private void OnDisable() {
			MenuSettings.AnchorChanged.RemoveListener(OnAnchorSettingChanged);

			if (_grabbable)
				_grabbable.OnReleasedEvent.RemoveListener(OnGrabbableReleased);
		}

		/// <summary>The hand let the panel go: it stays where it was released, the anchor follows it there.</summary>
		private void OnGrabbableReleased(GrabbableBase grabbable, GrabberBase grabber)
			=> Anchor();

		/// <summary>
		/// The mode changed: the panel stays exactly where it is and only follows differently from now on, so
		/// the pose it has right now becomes the new reference (no teleport).
		/// </summary>
		private void OnAnchorSettingChanged(XRMenuAnchor anchor)
			=> Anchor();
	}
}
