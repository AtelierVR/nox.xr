using System;
using Nox.XR.Runtime.Settings;
using UnityEngine;
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
	/// <para>
	/// The UI click is left to XRI (the tracked-device poke dispatches down/up/click itself), on the
	/// <b>release</b>: the button stays pressed while the finger is in and fires when it comes out. The
	/// drag that would otherwise swallow that click on a moving poke is compensated on the UI module's
	/// tracked drag threshold (see <see cref="XRInteractorBinder"/>).
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
		/// Multiplier applied to the poke's <b>world radius</b> to size its <c>pokeDepth</c>. The world radius
		/// already follows the avatar scale (<c>localRadius * radiusMultiplier * lossyScale</c>), so the depth
		/// does too and always stays above the finger colliders whatever the avatar size.
		/// </summary>
		[Tooltip("Multiplier applied to the poke's world radius to size its detection depth.")]
		public float depthMultiplier = 5f;

		/// <summary>Lower bound of the poke depth (world metres), so a small avatar still detects from a hand's length.</summary>
		[Tooltip("Lower bound of the poke depth, in world metres.")]
		public float minPokeDepth = 0.08f;

		/// <summary>
		/// Trim applied to the distance the poke point is pushed along the finger, so it sits on the surface the
		/// finger touches instead of the bone centre.
		/// <para>
		/// The distance itself is measured on the fingertip collider (see <see cref="Offset"/>): the colliders stop
		/// the hand where the finger visibly ends, so a poke point left at the bone centre stays a whole radius
		/// behind the surface. XRI then reads that gap as the poke depth of a UI element - a UI button follows the
		/// poke point, so a point that never reaches the canvas never sinks the button, while the poke still
		/// selects it.
		/// </para>
		/// </summary>
		[Tooltip("Trim on the distance the poke point is pushed along the finger, so it presses from the fingertip surface instead of the bone centre.")]
		public float pokeOffsetRatio = 1f;

		[Tooltip("Physics material the fingertip collider takes while this poke is active (the PokeGrip asset of the hand). " +
				 "A frictionless tip keeps slipping on what it presses, which trembles once the poke is engaged.")]
		public PhysicsMaterial gripMaterial;

		[Tooltip("Collider that takes the grip material while this poke is enabled (the distal phalanx capsule). Empty uses " +
				 "the closest collider in the parents.")]
		public Collider gripCollider;

		private PhysicsMaterial _restingMaterial;
		private bool _gripping;
		private Func<Vector3> _pokePointProvider;

		public bool Enable {
			get => enabled;
			set {
				if (enabled == value)
					return;

				enabled = value;

				// The toolkit takes a registration slot per enabled poke (eight for the whole rig, five pokes per hand):
				// this project does not use UI Toolkit world space, so the slot is given back right away.
				if (value)
					UiToolkitSlots.Release(this);
			}
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
			if (!PokeSettings.Enabled) {
				Enable = false;
				return;
			}

			if (updatePhase == XRInteractionUpdateOrder.UpdatePhase.Dynamic) {
				ApplyRadius();
				ApplyDepth();
				ApplyOffset();
			}

			base.ProcessInteractor(updatePhase);
		}

		protected override void Awake() {
			base.Awake();
			_pokePointProvider = GetPokePoint;
			ApplyRadius();
			ApplyDepth();
			ApplyOffset();
			ApplyClick();

			PokeSettings.Changed.AddListener(OnPokeEnabledChanged);
		}

		protected override void OnDestroy() {
			PokeSettings.Changed.RemoveListener(OnPokeEnabledChanged);
			base.OnDestroy();
		}

		protected override void OnEnable() {
			base.OnEnable();

			if (!PokeSettings.Enabled) {
				Enable = false;
				return;
			}

			UiToolkitSlots.Release(this);

			ApplyRadius();
			ApplyDepth();
			ApplyOffset();
			ApplyClick();
			ApplyGrip(true);
		}

		/// <summary>
		/// Cuts the poke as soon as the poke setting is turned off. It is left to the connector to bring it back
		/// (curl threshold, open hand, grab), so the setting always wins over the per-finger rules.
		/// </summary>
		private void OnPokeEnabledChanged(bool enabled) {
			if (!enabled)
				Enable = false;
		}

		protected override void OnDisable() {
			ApplyGrip(false);
			base.OnDisable();
		}

		/// <summary>
		/// Applies the grip again if this poke is enabled: something may have rewritten the physics material of the
		/// collider (<see cref="Nox.XR.Player.PhysicalHand.RefreshColliders"/> does it on every rebuild), which
		/// would otherwise silently drop it.
		/// </summary>
		public void RefreshGrip() {
			_gripping = false;
			ApplyGrip(isActiveAndEnabled);
		}

		/// <summary>
		/// Swaps the fingertip collider between its own physics material and the gripping one for as long as the
		/// poke is enabled (see <see cref="OnEnable"/> and <see cref="OnDisable"/>). A frictionless tip slides on
		/// the surface it presses, and the servo holding the finger there then trembles; a bit of grip settles it.
		/// </summary>
		private void ApplyGrip(bool active) {
			if (!gripMaterial || active == _gripping)
				return;

			if (!gripCollider)
				gripCollider = GetComponentInParent<Collider>();

			if (!gripCollider)
				return;

			_gripping = active;

			if (!active) {
				if (gripCollider.sharedMaterial != _restingMaterial)
					gripCollider.sharedMaterial = _restingMaterial;

				return;
			}

			// Writing the same material again is not free: the collider is rebuilt and its ignore pairs are dropped.
			if (gripCollider.sharedMaterial == gripMaterial)
				return;

			_restingMaterial = gripCollider.sharedMaterial;
			gripCollider.sharedMaterial = gripMaterial;
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
		/// Sizes the poke's detection depth from its world radius, so it follows the avatar scale and stays
		/// above the finger colliders: <c>pokeDepth = pokeRadius * depthMultiplier</c>, with a floor for small
		/// avatars.
		/// <para>
		/// XRI uses <c>pokeDepth</c> both as the UI detection range (the raycaster spherecasts with that
		/// radius, so a depth left at XRI's default 0.1 m only detected a button from ~10 cm) and as the base
		/// of the press window (<c>2.5% * pokeDepth</c>).
		/// </para>
		/// </summary>
		public void ApplyDepth() {
			var world = Mathf.Max(minPokeDepth, pokeWidth * depthMultiplier);
			if (!Mathf.Approximately(world, pokeDepth))
				pokeDepth = world;
		}

		/// <summary>
		/// Clicks the UI on the <b>release</b>, like a mouse: the button stays pressed as long as the finger
		/// is in, and its event fires when the finger is pulled out.
		/// <para>
		/// Clicking on the press instead (XRI's <c>clickUIOnDown</c>) sends down, up and click in a single
		/// frame, so the button visually un-presses while the finger is still in - measured live as "the
		/// button releases although I am still pressing". The release click only needs the pointer not to
		/// have dragged; <see cref="XRInteractorBinder"/> raises the module's tracked drag threshold for
		/// exactly that reason, a poke following the finger.
		/// </para>
		/// </summary>
		public void ApplyClick()
			=> clickUIOnDown = false;

		/// <summary>
		/// Finger axis of the poke: the tip bone's <c>up</c> axis (AutoHand convention).
		/// </summary>
		public Vector3 Axis
			=> transform.up;

		/// <summary>
		/// Distance from the tip bone to the surface of the fingertip, along <see cref="Axis"/>, trimmed by
		/// <see cref="pokeOffsetRatio"/>.
		/// <para>
		/// Measured on the collider the poke grips with (the last phalanx): the furthest point of that collider
		/// along the finger is where the finger stops on what it presses. Without a collider it falls back to the
		/// fingertip radius, scaled with the avatar.
		/// </para>
		/// </summary>
		public float Offset {
			get {
				var axis = Axis;

				if (gripCollider && gripCollider.enabled && gripCollider.gameObject.activeInHierarchy) {
					var surface = gripCollider.ClosestPoint(transform.position + axis);
					var distance = Vector3.Dot(surface - transform.position, axis);

					if (distance > 0f)
						return distance * pokeOffsetRatio;
				}

				return localRadius * Mathf.Abs(transform.lossyScale.x) * pokeOffsetRatio;
			}
		}

		/// <summary>
		/// Point the poke presses from: the fingertip surface, not the bone centre.
		/// <para>
		/// Everything XRI reads is taken from <c>GetAttachTransform</c> (the tip bone) and that cannot be offset on
		/// the transform itself, so the point is provided where XRI asks for it: the UI model (the raycast origin
		/// and the point the poke depth is measured from) and, for the world poke interactables, through
		/// <see cref="XRPokeInteractor.pokeInteractionOffset"/>.
		/// </para>
		/// </summary>
		public Vector3 PokePoint
			=> transform.position + Axis * Offset;

		/// <summary>
		/// Feeds the UI model the fingertip point instead of the bone centre. XRI's own provider returns the raw
		/// attach pose, and a point a radius behind the finger keeps a UI button from following the press.
		/// </summary>
		public override void UpdateUIModel(ref TrackedDeviceModel model) {
			base.UpdateUIModel(ref model);

			if (!isActiveAndEnabled || this.IsBlockedByInteractionWithinGroup())
				return;

			model.positionProvider = _pokePointProvider;
		}

		private Vector3 GetPokePoint()
			=> PokePoint;

		/// <summary>
		/// Pushes the poke point onto the fingertip surface: the offset XRI applies to a poke against a world
		/// interactable (see <see cref="XRPokeInteractor.pokeInteractionOffset"/>). Without it the depth is read
		/// from the bone centre, i.e. a whole finger radius short of what the finger touches.
		/// </summary>
		public void ApplyOffset() {
			var offset = Offset;

			if (!Mathf.Approximately(offset, pokeInteractionOffset))
				pokeInteractionOffset = offset;
		}

		private void OnDrawGizmos() {
			var point = PokePoint;

			Gizmos.color = Color.yellow;
			Gizmos.DrawLine(transform.position, point);

			Gizmos.color = Color.red;
			Gizmos.DrawWireSphere(point, pokeWidth);
		}
	}
}
