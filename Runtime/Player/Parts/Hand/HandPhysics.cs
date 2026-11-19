using Nox.CCK.Utils;
using UnityEngine;

namespace Nox.XR.Player {
	/// <summary>
	/// Collision settings of a hand part: the layer its colliders live on and the layers it never collides with.
	/// <para>
	/// The volumes themselves come from the physical hand (see <see cref="PhysicalHand"/>): the part carries no
	/// collider of its own, every solid volume of the hand belongs to that compound body.
	/// </para>
	/// </summary>
	[AddComponentMenu("Nox/XR/Player/Hand Physics")]
	[RequireComponent(typeof(Hand))]
	public class HandPhysics : MonoBehaviour {
		[SerializeField, Tooltip("Hand part carrying the body. Found on this GameObject when empty.")]
		private Hand hand;

		[SerializeField, Min(0.005f), Tooltip("Radius of the sphere the player keeps the hand out of the geometry with " +
		                                       "(level, props, menus). It must stay smaller than the palm, so a finger " +
		                                       "can still reach a button in front of the hand.")]
		private float environmentRadius = 0.035f;

		[Header("Collision")]
		[SerializeField, Tooltip("Layer the hand body lives on. Empty keeps the layer of the prefab.")]
		private string bodyLayer = Layers.Hand;

		[SerializeField, Tooltip("Layers the hand never collides with: the player's own rig only (its other hand and its body). Everything else - level, props, UI, other players - is hit.")]
		private string[] ignoredLayers = { Layers.Hand, Layers.LocalPlayer, Layers.Player };

		/// <summary>Hand part this body belongs to.</summary>
		public Hand Hand {
			get => hand;
			set => hand = value;
		}

		/// <summary>Layer the hand body lives on.</summary>
		public string BodyLayer {
			get => bodyLayer;
			set => bodyLayer = value;
		}

		/// <summary>Radius the player keeps the hand out of the geometry with.</summary>
		public float EnvironmentRadius {
			get => environmentRadius;
			set => environmentRadius = Mathf.Max(0.005f, value);
		}

		private void Reset()
			=> hand = GetComponent<Hand>();

		private void OnValidate() {
			if (!hand)
				hand = GetComponent<Hand>();
		}

		private void Awake() {
			if (!hand)
				hand = GetComponent<Hand>();

			ApplyLayer();
			ApplyCollisionRules();
		}

		private void OnEnable() {
			ApplyLayer();
			ApplyCollisionRules();
		}

		private void ApplyLayer() {
			var layer = Layers.LayerExists(bodyLayer) ? LayerMask.NameToLayer(bodyLayer) : gameObject.layer;
			gameObject.layer = layer;
		}

		/// <summary>
		/// Makes the hand collide with everything except the player's own rig, and forces the rest on.
		/// <para>
		/// The hand lives on its own <b>Hand</b> layer: it never touches the other hand or the player body
		/// (<c>Hand</c>, <c>LocalPlayer</c>, <c>Player</c>), and it hits every other layer -
		/// level, props, UI, other players - whatever the project matrix says, because a pair disabled there would
		/// otherwise let the hand go through a menu or a panel.
		/// </para>
		/// </summary>
		private void ApplyCollisionRules() {
			var layer = gameObject.layer;
			if (layer < 0)
				return;

			var ignoredMask = Layers.MaskBits(ignoredLayers ?? System.Array.Empty<string>());

			for (var other = 0; other < 32; other++) {
				var ignored = Layers.Contains(ignoredMask, other);

				if (Physics.GetIgnoreLayerCollision(layer, other) != ignored)
					Physics.IgnoreLayerCollision(layer, other, ignored);
			}

			if (hand && hand.Player)
				hand.Player.RefreshHandPartMask();
		}
	}
}
