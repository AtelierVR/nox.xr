using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

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
		public float depthMultiplier = 12f;

		/// <summary>Lower bound of the poke depth (world metres), so a small avatar still detects from a hand's length.</summary>
		[Tooltip("Lower bound of the poke depth, in world metres.")]
		public float minPokeDepth = 0.15f;

		/// <summary>
		/// Fraction of the fingertip radius (<see cref="localRadius"/>) the poke point is pushed along the
		/// finger so it sits on the fingertip surface instead of the bone centre.
		/// <para>
		/// The finger colliders stop the hand a few mm short of a surface, so a poke point left at the bone
		/// centre never reaches what the finger is visibly touching; <c>localRadius</c> (the fingertip
		/// radius) places it on the surface instead.
		/// </para>
		/// <para>
		/// The offset is proportional to the fingertip so it keeps working at any avatar scale, and it is
		/// written as a <b>local</b> position: the transform hierarchy applies the avatar scale to it.
		/// </para>
		/// </summary>
		[Tooltip("Fraction of the fingertip radius the poke point is pushed along the finger, so it presses from the fingertip surface instead of the bone centre.")]
		public float pokeOffsetRatio = 1f;

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
				ApplyDepth();
				ApplyOffset();
			}

			base.ProcessInteractor(updatePhase);
		}

		protected override void Awake() {
			base.Awake();
			ApplyRadius();
			ApplyDepth();
			ApplyOffset();
			ApplyClick();
		}

		protected override void OnEnable() {
			base.OnEnable();
			ApplyRadius();
			ApplyDepth();
			ApplyOffset();
			ApplyClick();
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
		/// Moves the auto-created attach transform onto the fingertip surface.
		/// The finger axis is the tip bone's <c>up</c> axis (AutoHand convention), and the attach
		/// transform is a child with an identity rotation, so a local <c>up</c> offset follows the finger
		/// and is scaled with the avatar.
		/// <para>
		/// The push (<c>localRadius</c>) is what lets the poke point reach the target: the finger colliders
		/// stop the hand short of the surface, so the bone centre never gets close enough on its own. It is
		/// applied everywhere, UI included - a poke point left at the bone centre hovers a UI element but
		/// never enters the depth window XRI needs to press it.
		/// </para>
		/// </summary>
		public void ApplyOffset() {
			var attach = attachTransform;
			if (attach == null || attach == transform)
				return;

			var distance = localRadius * pokeOffsetRatio;

			var local = Vector3.up * distance;
			if (attach.localPosition != local)
				attach.localPosition = local;
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
