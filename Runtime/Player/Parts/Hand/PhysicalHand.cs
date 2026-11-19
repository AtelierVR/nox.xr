using System.Collections.Generic;
using Nox.Avatars.Hand;
using Nox.CCK.Utils;
using Nox.XR.Runtime.Connectors;
using UnityEngine;

namespace Nox.XR.Player {
	/// <summary>
	/// Physical hand of a hand part: the node that carries the fingers, their colliders and the palm volume.
	/// <para>
	/// It is the hand the interaction pipeline poses (it answers <see cref="IHand"/>) and the one the avatar
	/// follows. Fingers can be authored on the prefab or built from the default shape: an avatar only changes
	/// their dimensions, their blend poses and which fingers exist.
	/// </para>
	/// </summary>
	[AddComponentMenu("Nox/XR/Player/Physical Hand")]
	public class PhysicalHand : MonoBehaviour, IHand {
		private static readonly FingerType[] _fingerOrder = {
			FingerType.Thumb,
			FingerType.Index,
			FingerType.Middle,
			FingerType.Ring,
			FingerType.Pinky
		};

		[SerializeField, Tooltip("Fingers of this hand. Found in the children when empty.")]
		private PhysicalFinger[] fingers = System.Array.Empty<PhysicalFinger>();

		[SerializeField, Tooltip("Palm volume of the hand: the body the player depenetrates with. Created when empty.")]
		private BoxCollider palm;

		[SerializeField, Tooltip("Physics material of every collider of the hand: no friction, so the hand slides on what " +
								  "it touches instead of sticking to it.")]
		private PhysicsMaterial material;

		[SerializeField, Tooltip("Physics material a fingertip takes while its poke is active: a bit of grip settles the " +
								  "finger on the surface instead of letting it tremble.")]
		private PhysicsMaterial pokeGrip;

		[Header("Default hand")]
		[SerializeField, Tooltip("Palm width, thickness and length, in metres.")]
		private Vector3 palmSize = new(0.085f, 0.03f, 0.09f);

		[SerializeField, Min(0f), Tooltip("Distance between two knuckles, in metres.")]
		private float knuckleSpacing = 0.019f;

		[SerializeField, Min(0.001f), Tooltip("Radius of the finger colliders and of the tip pokes, in metres.")]
		private float fingerRadius = 0.009f;

		[SerializeField, Tooltip("Length of the proximal, intermediate and distal phalanx of a finger, in metres.")]
		private Vector3 phalanx = new(0.038f, 0.024f, 0.019f);

		[SerializeField, Tooltip("Length of each finger as a factor of the phalanges: thumb, index, middle, ring, pinky.")]
		private float[] fingerScales = { 0.9f, 1f, 1.05f, 1f, 0.88f };

		[SerializeField, Range(0f, 45f), Tooltip("Spread of the default fingers, in degrees: the middle stays straight, the sides fan out.")]
		private float spreadAngle = 9f;

		[SerializeField, Range(-90f, 90f), Tooltip("Angle of the default thumb away from the palm, in degrees.")]
		private float thumbAngle = 55f;

		[SerializeField, Range(0f, 120f), Tooltip("How far a default finger folds at full curl, in degrees.")]
		private float curlAngle = 80f;

		[Header("Equipment")]
		[SerializeField, Tooltip("Give each finger its colliders and its poke.")]
		private bool equip = true;

		private IFinger[] _fingers = System.Array.Empty<IFinger>();
		private readonly List<(string key, PokeInteractor poke)> _pokes = new();
		private readonly List<Collider> _colliders = new();
		private BoxCollider encapsulation;
		private HandSide _keySide;
		private bool _keySideKnown;

		/// <summary>Fingers currently carried by the hand.</summary>
		public IFinger[] Fingers
			=> _fingers;

		/// <summary>Solid colliders of the hand: the palm and the fingers.</summary>
		public IReadOnlyList<Collider> Colliders
			=> _colliders;

		/// <summary>Solid volume of the palm, used as the body of the hand.</summary>
		public Collider BodyCollider
			=> palm;

		/// <summary>
		/// Physics material given to every solid collider of the hand: frictionless, the way AutoHand's own hand is
		/// set up. Without it the servo keeps pressing the hand into what it touches, the friction turns that
		/// pressure into a tangential force, and the hand sticks instead of sliding along the surface.
		/// </summary>
		public PhysicsMaterial Material {
			get => material;
			set => material = value;
		}

		/// <summary>Physics material a fingertip takes while its poke is active.</summary>
		public PhysicsMaterial PokeGrip {
			get => pokeGrip;
			set => pokeGrip = value;
		}

		/// <summary>
		/// Volume enclosing every solid collider of the hand, the one the player keeps out of the geometry: a
		/// trigger, so it never collides by itself, sized by <see cref="AlignEncapsulation"/>.
		/// </summary>
		public BoxCollider EncapsulationBox {
			get {
				if (encapsulation)
					return encapsulation;

				var node = transform.Find("Encapsulation");
				return encapsulation = node ? node.GetComponent<BoxCollider>() : null;
			}
		}

		/// <summary>Physical finger components of the hand.</summary>
		public IReadOnlyList<PhysicalFinger> PhysicalFingers
			=> fingers;

		public HandType Type
			=> GetComponentInParent<Hand>() is { } hand && hand.Side == HandSide.Left ? HandType.Left : HandType.Right;

		public Transform Anchor
			=> transform;

		public Transform Palm
			=> transform;

		public Vector3 PositionOffset
			=> Vector3.zero;

		public Quaternion RotationOffset
			=> Quaternion.identity;

		public Transform NearFar
			=> null;

		private Hand Part
			=> GetComponentInParent<Hand>();

		private void Awake() {
			Collect();
			Refresh();
			RefreshColliders();
		}

		private void Reset() {
			Collect();
			Refresh();
		}

		private void Collect() {
			if (fingers != null && fingers.Length > 0)
				return;

			var found = GetComponentsInChildren<PhysicalFinger>(true);
			if (found.Length > 0)
				fingers = found;
		}

		private void Refresh() {
			var result = new List<IFinger>(fingers.Length);

			for (var i = 0; i < _fingerOrder.Length; i++) {
				var finger = Find(_fingerOrder[i]);
				if (finger)
					result.Add(finger);
			}

			_fingers = result.ToArray();
			var part = Part;
			var side = part && part.Side == HandSide.Left ? "left" : "right";

			_keySide      = side == "left" ? HandSide.Left : HandSide.Right;
			_keySideKnown = true;

			_pokes.Clear();

			for (var i = 0; i < fingers.Length; i++) {
				var finger = fingers[i];

				if (finger && finger.Poke)
					_pokes.Add(($"finger.{side}.{finger.Type.ToString().ToLowerInvariant()}", finger.Poke));
			}		}

		/// <summary>Finger of a type, or null when the hand does not carry it.</summary>
		public PhysicalFinger Find(FingerType type) {
			var list = fingers ?? System.Array.Empty<PhysicalFinger>();

			for (var i = 0; i < list.Length; i++)
				if (list[i] && list[i].Type == type)
					return list[i];

			return null;
		}

		/// <summary>Finger of a type, built from the default shape when the hand does not carry it yet.</summary>
		public PhysicalFinger Require(FingerType type) {
			var finger = Find(type);
			if (finger)
				return finger;

			var node = new GameObject(type.ToString());
			node.layer = gameObject.layer;
			node.transform.SetParent(transform, false);

			finger = node.AddComponent<PhysicalFinger>();

			var scale = ScaleOf(type);
			var hand = Part;

			var position = new Vector3[4];
			var rotation = new Quaternion[4];
			DefaultPose(type, hand, position, rotation, scale);

			finger.Initialize(type, fingerRadius, curlAngle, position, rotation);
			Add(finger);
			return finger;
		}

		/// <summary>Removes a finger from the hand and destroys it.</summary>
		public void Remove(FingerType type) {
			var finger = Find(type);
			if (!finger)
				return;

			var list = new List<PhysicalFinger>(fingers);
			list.Remove(finger);
			fingers = list.ToArray();

			Refresh();
			finger.gameObject.Destroy();
		}

		private void Add(PhysicalFinger finger) {
			var list = new List<PhysicalFinger>(fingers) { finger };
			fingers = list.ToArray();

			Refresh();
		}

		/// <summary>Restores the default shape of every finger of the hand.</summary>
		public void ResetFingers() {
			for (var i = 0; i < fingers.Length; i++)
				if (fingers[i])
					fingers[i].Unfit();
		}

		/// <summary>Whether the finger is carried by the hand.</summary>
		public bool Contains(FingerType type)
			=> Find(type) != null;

		/// <summary>
		/// Collects the colliders of the hand: the palm and the finger volumes, never the triggers nor the
		/// disabled transition boxes. This is the list the player uses as the body of the hand.
		/// </summary>
		public void RefreshColliders() {
			_colliders.Clear();

			var handMaterial = Material;

			foreach (var collider in GetComponentsInChildren<Collider>(true)) {
				if (!collider || collider.isTrigger || !collider.enabled)
					continue;

				if (handMaterial)
					collider.sharedMaterial = handMaterial;

				_colliders.Add(collider);
			}
		}

		/// <summary>Adds every solid collider of the hand to <paramref name="results"/>.</summary>
		public void GetBodyColliders(List<Collider> results) {
			if (results == null)
				return;

			if (_colliders.Count == 0)
				RefreshColliders();

			for (var i = 0; i < _colliders.Count; i++)
				results.Add(_colliders[i]);
		}

		private float ScaleOf(FingerType type) {
			var index = System.Array.IndexOf(_fingerOrder, type);
			return fingerScales != null && index >= 0 && index < fingerScales.Length ? fingerScales[index] : 1f;
		}

		private void DefaultPose(FingerType type, Hand hand, Vector3[] positions, Quaternion[] rotations, float scale) {
			var index = System.Array.IndexOf(_fingerOrder, type);
			var mirror = hand && hand.Side == HandSide.Left ? -1f : 1f;

			Vector3 knuckle;
			float yaw;

			if (type == FingerType.Thumb) {
				knuckle = new Vector3(mirror * -(palmSize.x * 0.5f + knuckleSpacing * 0.4f), 0f, palmSize.z * 0.15f);
				yaw = mirror * thumbAngle;
			} else {
				knuckle = new Vector3(mirror * (index - 2.5f) * knuckleSpacing, 0f, palmSize.z * 0.5f);
				yaw = mirror * (index - 2.5f) * spreadAngle;
			}

			positions[0] = knuckle;
			rotations[0] = Quaternion.Euler(0f, yaw, 0f);

			positions[1] = new Vector3(0f, 0f, phalanx.x * scale);
			positions[2] = new Vector3(0f, 0f, phalanx.y * scale);
			positions[3] = new Vector3(0f, 0f, phalanx.z * scale);

			rotations[1] = Quaternion.identity;
			rotations[2] = Quaternion.identity;
			rotations[3] = Quaternion.identity;
		}

		/// <summary>Sizes the palm volume on the knuckles of the fingers it carries.</summary>
		public void SizePalm() {
			if (!equip)
				return;

			var palm = PalmVolume();

			if (fingers == null || fingers.Length == 0) {
				palm.size = new Vector3(0.08f, 0.02f, 0.08f);
				palm.center = Vector3.zero;
				return;
			}

			var bounds = new Bounds(Vector3.zero, Vector3.zero);
			var any = true;
			var averageTipRadius = 0f;
			var count = 0;

			for (var i = 0; i < fingers.Length; i++) {
				var finger = fingers[i];
				if (!finger || !finger.Proximal)
					continue;

				var local = transform.InverseTransformPoint(finger.Proximal.position);

				if (!any) {
					bounds = new Bounds(local, Vector3.zero);
					any = true;
				} else {
					bounds.Encapsulate(local);
				}

				averageTipRadius += finger.Radius;
				count++;
			}

			if (!any)
				return;

			if (count > 0)
				averageTipRadius /= count;

			var thickness = Mathf.Max(averageTipRadius * 3f, 0.015f);
			var size = bounds.size;

			size.x = Mathf.Max(size.x, 0.02f);
			size.y = Mathf.Max(size.y, 0.02f);
			size.z = Mathf.Max(size.z, 0.02f);

			var thin = size.x <= size.y && size.x <= size.z ? 0 : size.y <= size.z ? 1 : 2;
			size[thin] = Mathf.Max(size[thin], thickness);

			palm.center = bounds.center;
			palm.size = size;
		}

		/// <summary>Palm volume of the hand, built as a child node like the AutoHand palm collider.</summary>
		private BoxCollider PalmVolume() {
			if (palm)
				return palm;

			var node = transform.Find("Palm");

			if (!node) {
				var created = new GameObject("Palm");
				created.layer = gameObject.layer;
				node = created.transform;
				node.SetParent(transform, false);
			}

			palm = node.gameObject.GetOrAddComponent<BoxCollider>();
			return palm;
		}

		/// <summary>
		/// Recomputes the query volume of the whole hand from its solid colliders, the way the AutoHand
		/// conversion does: a trigger, disabled, used for box casts rather than for collisions.
		/// </summary>
		public void AlignEncapsulation() {
			if (!equip)
				return;

			var node = transform.Find("Encapsulation");

			if (!node) {
				var created = new GameObject("Encapsulation");
				created.layer = gameObject.layer;
				node = created.transform;
				node.SetParent(transform, false);
			}

			var box = node.gameObject.GetOrAddComponent<BoxCollider>();
			box.isTrigger = true;
			box.enabled = true;
			encapsulation = box;

			var local = new Bounds();
			var any = false;

			foreach (var collider in transform.GetComponentsInChildren<Collider>(true)) {
				if (collider == box || collider.isTrigger || !collider.enabled)
					continue;

				Encapsulate(collider, ref local, ref any);
			}

			if (!any)
				return;

			box.center = local.center;
			box.size = local.size;
		}

		/// <summary>
		/// Grows <paramref name="bounds"/> with a collider, from its own local shape rather than from
		/// <c>Collider.bounds</c>: the physics only refreshes those AABBs on its own tick, so reading them right
		/// after the bones and the colliders were rebuilt gives the shape of the previous frame.
		/// </summary>
		private void Encapsulate(Collider collider, ref Bounds bounds, ref bool any) {
			if (!collider)
				return;

			Vector3 center;
			Vector3 extents;

			switch (collider) {
				case BoxCollider box:
					center = box.center;
					extents = box.size * 0.5f;
					break;

				case SphereCollider sphere:
					center = sphere.center;
					extents = Vector3.one * sphere.radius;
					break;

				case CapsuleCollider capsule:
					center = capsule.center;
					extents = Vector3.one * capsule.radius;
					extents[capsule.direction] = capsule.height * 0.5f;
					break;

				default:
					center = collider.bounds.center;
					extents = collider.bounds.extents;
					Append(collider.transform.TransformPoint(center), transform.InverseTransformVector(extents), ref bounds, ref any);
					return;
			}

			var frame = collider.transform;

			for (var i = 0; i < 8; i++) {
				var sign = new Vector3(
					(i & 1) == 0 ? -1f : 1f,
					(i & 2) == 0 ? -1f : 1f,
					(i & 4) == 0 ? -1f : 1f);

				var corner = transform.InverseTransformPoint(frame.TransformPoint(center + Vector3.Scale(extents, sign)));
				Append(corner, Vector3.zero, ref bounds, ref any);
			}
		}

		private static void Append(Vector3 point, Vector3 extents, ref Bounds bounds, ref bool any) {
			extents = new Vector3(Mathf.Abs(extents.x), Mathf.Abs(extents.y), Mathf.Abs(extents.z));
			var current = new Bounds(point, extents * 2f);

			if (!any) {
				bounds = current;
				any = true;
			} else {
				bounds.Encapsulate(current);
			}
		}

		/// <summary>Pokes of the fingers of the hand.</summary>
		public IReadOnlyList<(string key, PokeInteractor poke)> Pokes
			=> _pokes;

		/// <summary>
		/// Rebuilds the poke binding keys when the side of the hand was not known yet when they were built.
		/// <para>
		/// The keys are <c>finger.&lt;side&gt;.&lt;type&gt;</c>, and the side comes from the hand part. A hand is
		/// instantiated from its prefab, which is authored on the right side, and only then given its side
		/// (<see cref="Hand.Create"/>): the pokes were keyed on <c>finger.right.*</c> on both hands, so the left
		/// pokes were driven by the <b>right</b> hand's fingers - they stopped interacting as soon as the right
		/// index was bent or the right hand opened.
		/// </para>
		/// </summary>
		/// <returns>True when the keys were rebuilt, so the connector has to be given the new ones.</returns>
		public bool SyncKeys(HandSide side) {
			if (_keySideKnown && side == _keySide)
				return false;

			Refresh();
			return true;
		}

		/// <summary>
		/// Whether one of the fingers is pressing something with its poke. The grip of the follower damps the hand
		/// while it is true, instead of relying on the friction of a fingertip material: friction only bites on the
		/// contact, so it holds, slips, holds again, and the hand stutters along the surface. A drag settles the small
		/// movements smoothly and fades out as soon as the hand is actually sliding.
		/// </summary>
		public bool Gripping {
			get {
				for (var i = 0; i < _pokes.Count; i++) {
					var poke = _pokes[i].poke;

					if (poke && poke.isActiveAndEnabled && poke.pokeStateData.Value.meetsRequirements)
						return true;
				}

				return false;
			}
		}
	}
}
