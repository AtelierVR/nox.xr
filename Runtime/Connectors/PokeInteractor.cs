using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace Nox.XR.Runtime.Connectors {
	/// <summary>
	/// Nox poke interactor for a fingertip.
	/// <para>
	/// XRI's poke radius (<c>pokeWidth</c>/<c>pokeHoverRadius</c>) is a <b>world</b> value, but a fingertip
	/// lives under the avatar and is scaled with it. Everything here is therefore derived from the unscaled
	/// fingertip radius (<see cref="localRadius"/>) and the tip's world scale, so the poke stays finger-sized
	/// whatever the avatar scale: an absolute metre value would be too small on a big avatar (the finger
	/// colliders then keep the poke away from the surface) and too big on a small one.
	/// </para>
	/// </summary>
	public class PokeInteractor : XRPokeInteractor {

		/// <summary>Unscaled poke radius: the world radius is this value scaled by the tip's world scale.</summary>
		[Tooltip("Unscaled poke radius; the world radius follows the tip's world scale.")]
		public float localRadius = 0.01f;

		/// <summary>
		/// Multiplier applied to the radius so the poke sphere stays bigger than the finger colliders, which
		/// otherwise compete with the poke for the same surface (a poke that is too narrow misses a button
		/// the fingertip is visibly on).
		/// </summary>
		[Tooltip("Applied to the radius so the poke sphere stays bigger than the finger colliders.")]
		public float radiusMultiplier = 2f;

		/// <summary>
		/// Fraction of the fingertip radius (<see cref="localRadius"/>) the poke point is pushed along the
		/// finger so it sits on the fingertip surface instead of the bone centre.
		/// <para>
		/// XRI only presses a UI element once the poke point is <c>2.5% * pokeDepth</c> past the target
		/// plane (about 2.5 mm with the default depth). The finger colliders stop the hand before the tip
		/// bone can get that close to a panel, so the poke has to start ahead of the bone to ever press:
		/// the button hovers (it is detected) but never plays its press animation.
		/// </para>
		/// <para>
		/// The offset is proportional to the fingertip so it keeps working at any avatar scale, and it is
		/// written as a <b>local</b> position: the transform hierarchy applies the avatar scale to it.
		/// </para>
		/// </summary>
		[Tooltip("Fraction of the fingertip radius the poke point is pushed along the finger, so it presses from the fingertip surface instead of the bone centre.")]
		public float pokeOffsetRatio = 1f;

		/// <summary>
		/// World distance (metres) the poke point is pulled back for a couple of frames to force a UI release
		/// (see <see cref="UpdateUITarget"/>).
		/// </summary>
		[Tooltip("World distance (metres) the poke point is pulled back to force a UI release.")]
		public float releaseDistance = 0.5f;

		/// <summary>Seconds between two forced releases used to unblock a press that XRI failed to arm.</summary>
		[Tooltip("Seconds between two forced releases used to unblock a press that failed to arm.")]
		public float assistCooldown = 0.25f;

		/// <summary>Frames a failed press is given to arm by itself before it is forced.</summary>
		[Tooltip("Frames a failed press is given to arm by itself before it is forced.")]
		public int assistDelayFrames = 3;

		/// <summary>Frames the poke point stays pulled back to force a UI release.</summary>
		private int _releaseFrames;

		/// <summary>Time before another fallback release may be forced.</summary>
		private float _nextAssistTime;

		/// <summary>Consecutive frames where the press was required but did not happen.</summary>
		private int _blockedFrames;

		/// <summary>Whether the pointer was pressing on the previous frame.</summary>
		private bool _wasSelecting;

		/// <summary>UI element the pointer was on last frame.</summary>
		private GameObject _uiTarget;

		private XRUIInputModule _inputModule;

		public bool Enable {
			get => enabled;
			set => enabled = value;
		}

		public float Radius {
			get => pokeWidth;
			set {
				requirePokeFilter = false;
				pokeWidth = value;
				pokeSelectWidth = value;
				pokeHoverRadius = value;
			}
		}

		public override void ProcessInteractor(XRInteractionUpdateOrder.UpdatePhase updatePhase) {
			if (updatePhase == XRInteractionUpdateOrder.UpdatePhase.Dynamic) {
				ApplyRadius();
				UpdateUITarget();
				ApplyOffset();

				if (_releaseFrames > 0)
					_releaseFrames--;
			}

			base.ProcessInteractor(updatePhase);
		}

		protected override void Awake() {
			base.Awake();
			ApplyRadius();
			ApplyOffset();
		}

		protected override void OnEnable() {
			base.OnEnable();
			_blockedFrames = 0;
			_wasSelecting = false;
			ApplyRadius();
			ApplyOffset();
		}

		/// <summary>
		/// Sizes the poke from the fingertip radius and the tip's world scale. A disabled poke is not processed
		/// by the interaction manager, so it is also refreshed on enable to be correct on its very first frame.
		/// </summary>
		public void ApplyRadius() {
			var world = localRadius * radiusMultiplier * Mathf.Abs(transform.lossyScale.x);
			if (!Mathf.Approximately(world, pokeWidth))
				Radius = world;
		}

		/// <summary>
		/// Moves the auto-created attach transform onto the fingertip surface.
		/// The finger axis is the tip bone's <c>up</c> axis (AutoHand convention), and the attach
		/// transform is a child with an identity rotation, so a local <c>up</c> offset follows the finger
		/// and is scaled with the avatar.
		/// </summary>
		public void ApplyOffset() {
			var attach = attachTransform;
			if (attach == null || attach == transform)
				return;

			var distance = localRadius * pokeOffsetRatio;
			if (_releaseFrames > 0)
				distance = -releaseDistance / Mathf.Max(Mathf.Abs(transform.lossyScale.y), 1e-5f);

			var local = Vector3.up * distance;
			if (attach.localPosition != local)
				attach.localPosition = local;
		}

		/// <summary>
		/// Keeps the UI pointer usable while the finger stays on the panel. XRI releases the pointer only when
		/// the poke leaves the surface and caches the poked element, so the press has to be forced open again
		/// (by pulling the poke point out of the panel for a couple of frames) in three cases: right after a
		/// press ended - the invisible moment, so the next press arms on its very first frame - when sliding
		/// from one element to the next, and when a met depth requirement stays without a press.
		/// </summary>
		private void UpdateUITarget() {
			var target = CurrentUITarget();
			var selecting = TrackedDeviceGraphicRaycaster.IsPokeSelectingWithUI(this);
			var interacting = TrackedDeviceGraphicRaycaster.IsPokeInteractingWithUI(this);

			_blockedFrames = !selecting && interacting && pokeStateData.Value.meetsRequirements ? _blockedFrames + 1 : 0;

			if (_releaseFrames == 0) {
				// The press just ended: drop what XRI cached for the pointer. Doing it here (and not when a
				// press is needed) is invisible, and the next press can then arm on its very first frame -
				// a fast poke would otherwise be swallowed by the forced release.
				if (_wasSelecting && !selecting && interacting) {
					_releaseFrames = 2;
				}
				// Sliding from one element to the next keeps the pointer down in XRI, so the new
				// element never gets its own press.
				else if (selecting && target != null && target != _uiTarget && _uiTarget != null) {
					_releaseFrames = 2;
				}
				// The depth requirement is met but the press does not come: XRI is holding a stale
				// cached target. Give it a few frames first, so a quick poke is never cut short.
				else if (_blockedFrames >= assistDelayFrames && Time.unscaledTime >= _nextAssistTime) {
					_releaseFrames = 2;
					_nextAssistTime = Time.unscaledTime + assistCooldown;
				}
			}

			if (target != null)
				_uiTarget = target;

			_wasSelecting = selecting;
		}

		/// <summary>
		/// UI element the pointer of this poke is currently on, or <c>null</c>.
		/// The press target is the one uGUI would press (the hierarchy root handling the pointer), so a
		/// graphic layer change inside the same button does not count as a change.
		/// </summary>
		private GameObject CurrentUITarget() {
			var eventSystem = EventSystem.current;
			if (eventSystem == null)
				return null;

			if (_inputModule == null || _inputModule.gameObject != eventSystem.gameObject) {
				_inputModule = eventSystem.GetComponent<XRUIInputModule>();
				if (_inputModule == null)
					return null;
			}

			if (!TryGetUIModel(out var model))
				return null;

			var hovered = _inputModule.GetCurrentGameObject(model.pointerId);
			return hovered != null ? ExecuteEvents.GetEventHandler<IPointerDownHandler>(hovered) : null;
		}

		private void OnDrawGizmos() {
			var attach = attachTransform != null ? attachTransform : transform;

			Gizmos.color = Color.yellow;
			Gizmos.DrawLine(transform.position, attach.position);

			Gizmos.color = Color.red;
			Gizmos.DrawWireSphere(attach.position, pokeWidth);
		}
	}
}
