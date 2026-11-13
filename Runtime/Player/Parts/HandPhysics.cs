using Nox.CCK.Utils;
using UnityEngine;

namespace Nox.XR.Player {
	/// <summary>
	/// Physical body of a hand: the solid volume that collides with the world, the props and the menus.
	/// <para>
	/// It lives on the hand part itself - the rigidbody that follows the controller - so it is one compound
	/// body, not a set of static colliders on the avatar bones. The collision rules are the ones a controller
	/// needs: hands collide with everything <b>except</b> the player's own rig (its other hand, head, feet,
	/// body) and the other players, which would otherwise block or push the hand for no reason.
	/// </para>
	/// </summary>
	[AddComponentMenu("Nox/XR/Player/Hand Physics")]
	[RequireComponent(typeof(Hand))]
	public class HandPhysics : MonoBehaviour {
		[SerializeField, Tooltip("Hand part carrying the body. Found on this GameObject when empty.")]
		private Hand hand;

		[Header("Body")]
		[SerializeField, Tooltip("Add a box collider to the hand when it has none.")]
		private bool addBody = true;

		[SerializeField, Tooltip("Size of that box, in metres.")]
		private Vector3 bodySize = new(0.06f, 0.05f, 0.10f);

		[SerializeField, Tooltip("Centre of that box, in the local space of the hand.")]
		private Vector3 bodyCenter = new(0f, 0f, 0.02f);

		[SerializeField, Min(0.005f), Tooltip("Radius of the sphere the player keeps the hand out of the geometry with " +
		                                       "(level, props, menus). It must stay smaller than the palm, so a finger " +
		                                       "can still reach a button in front of the hand.")]
		private float environmentRadius = 0.035f;

		[Header("Collision")]
		[SerializeField, Tooltip("Layer the hand body lives on. Empty keeps the layer of the prefab.")]
		private string bodyLayer = "Hand";

		[SerializeField, Tooltip("Layers a hand never collides with: the player rig itself, the players, and the grabbing volume used to detect what it holds.")]
		private string[] ignoredLayers = { "Hand", "HandPlayer", "Player", "LocalAvatar", "RemoteAvatar", "Grabbing" };

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

		/// <summary>
		/// Solid volume of the hand: the collider the player depenetrates with (see
		/// <c>Part.EnvironmentCollider</c>). Without it the hand has no environment volume and its pose, written
		/// every frame, goes straight through the menu and the walls.
		/// </summary>
		public Collider BodyCollider
			=> _body ? _body : _body = gameObject.GetComponent<Collider>();

		/// <summary>Radius the player keeps the hand out of the geometry with.</summary>
		public float EnvironmentRadius {
			get => environmentRadius;
			set => environmentRadius = Mathf.Max(0.005f, value);
		}

		private Collider _body;

		private void Reset()
			=> hand = GetComponent<Hand>();

		private void OnValidate() {
			if (!hand)
				hand = GetComponent<Hand>();
		}

		private void Awake() {
			if (!hand)
				hand = GetComponent<Hand>();

			EnsureBody();
			ApplyCollisionRules();
		}

		private void OnEnable() {
			EnsureBody();
			ApplyCollisionRules();
		}

		/// <summary>
		/// Puts the hand on its physical layer and gives it a volume: the rigidbody follows the controller and
		/// must not be a ghost, or it pushes nothing and stops nothing.
		/// </summary>
		private void EnsureBody() {
			var layer = Layers.LayerExists(bodyLayer) ? LayerMask.NameToLayer(bodyLayer) : gameObject.layer;
			gameObject.layer = layer;

			var existing = gameObject.GetComponent<Collider>();
			if (existing) {
				_body = existing;
				return;
			}

			if (!addBody)
				return;

			var box = gameObject.AddComponent<BoxCollider>();
			box.size = bodySize;
			box.center = bodyCenter;
			_body = box;
		}

		/// <summary>
		/// Cuts the collisions that would only jam the hand, and makes sure every other one happens.
		/// <para>
		/// A hand collides with everything: the level, the props, the menus. Only the player's own body (its
		/// other hand, head, feet, capsule) and the other players are ignored - they would block or push the
		/// hand for no reason. Collisions are <b>forced on</b> for the rest, because the project matrix may
		/// disable some pairs (UI, Grabbing...) that a hand still has to hit.
		/// </para>
		/// </summary>
		private void ApplyCollisionRules() {
			var layer = gameObject.layer;
			if (layer < 0)
				return;

			var ignoredMask = Layers.MaskBits(ignoredLayers);

			for (var other = 0; other < 32; other++) {
				var ignored = Layers.Contains(ignoredMask, other);

				if (Physics.GetIgnoreLayerCollision(layer, other) != ignored)
					Physics.IgnoreLayerCollision(layer, other, ignored);
			}
		}
	}
}
