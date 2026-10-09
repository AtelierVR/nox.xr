using Autohand;
using Nox.CCK.Development;
using UnityEngine;
using Gizmos = Nox.CCK.Development.Gizmos;

namespace Nox.XR.Runtime.Connectors {
	/// <summary>
	/// Companion for a <see cref="Grabbable"/> that adds grab conditions on top of the grabbable's own ones.
	/// <para>
	/// Place it on (or under) a grabbable; <see cref="HandGrabConnector"/> finds it on the highlighted
	/// grabbable and calls <see cref="CanGrab"/> just before grabbing. The grabbable's own
	/// <see cref="Grabbable.CanGrab(Hand)"/> is kept as the base, then these conditions are added, tested
	/// against a collider (default: the grabbable's first collider):
	/// </para>
	/// <list type="bullet">
	/// <item><see cref="allowedSide"/>: the palm must be on that local side of the collider (e.g. behind a panel);</item>
	/// <item><see cref="maxDistance"/>: the palm must be within that distance of the collider;</item>
	/// <item><see cref="allowedForward"/> / <see cref="maxAngle"/>: the palm forward must follow that local direction.</item>
	/// </list>
	/// </summary>
	[DisallowMultipleComponent]
	public class AdditionalGrabbable : MonoBehaviour, IGizmos {
		[Tooltip("Collider the conditions are tested against. Empty: the first collider found on the grabbable.")]
		public Collider conditionCollider;

		[Header("Position")]
		[Tooltip("Local direction (collider space) pointing to the side a hand may grab from. Zero disables it.")]
		public Vector3 allowedSide = Vector3.zero;

		[Tooltip("Metres the palm may sit on the wrong side of that plane (0 = strictly on the allowed side).")]
		public float sideTolerance = 0f;

		[Tooltip("Max distance (metres) between the palm and the collider. 0 disables it.")]
		public float maxDistance = 0f;

		[Header("Angle")]
		[Tooltip("Local direction (collider space) the palm forward must follow. Zero disables it.")]
		public Vector3 allowedForward = Vector3.zero;

		[Tooltip("Max angle (degrees) between the palm forward and allowedForward.")]
		public float maxAngle = 45f;

		private Collider _collider;

		/// <summary>Grabbable this component adds conditions to (looked up on the parents).</summary>
		public Grabbable GetGrabbable()
			=> GetComponentInParent<Grabbable>();

		/// <summary>Collider the conditions are tested against (default: the grabbable's first collider).</summary>
		public Collider ConditionCollider {
			get {
				if (conditionCollider != null)
					return conditionCollider;

				if (_collider == null) {
					_collider = GetComponent<Collider>();
					if (_collider == null) {
						var grab = GetGrabbable();
						if (grab != null)
							_collider = grab.GetComponentInChildren<Collider>();
					}
				}

				return _collider;
			}
		}

		/// <summary>
		/// Extra grab conditions. Keeps the grabbable's own <see cref="Grabbable.CanGrab(Hand)"/> as the base,
		/// then applies the position / distance / angle conditions. Returns true when the grab is allowed.
		/// </summary>
		public bool CanGrab(Hand hand) {
			var grabbable = GetGrabbable();
			if (grabbable != null && !grabbable.CanGrab(hand))
				return false;

			if (hand == null || hand.palmTransform == null)
				return true;

			var col = ConditionCollider;
			if (col == null)
				return true;

			var palm = hand.palmTransform;

			if (allowedSide != Vector3.zero) {
				var normal = col.transform.TransformDirection(allowedSide.normalized);
				var depth  = Vector3.Dot(palm.position - col.bounds.center, normal);
				if (depth < -sideTolerance)
					return false;
			}

			if (maxDistance > 0f && Vector3.Distance(palm.position, col.ClosestPoint(palm.position)) > maxDistance)
				return false;

			if (allowedForward != Vector3.zero) {
				var direction = col.transform.TransformDirection(allowedForward.normalized);
				if (Vector3.Angle(palm.forward, direction) > maxAngle)
					return false;
			}

			return true;
		}

		private static Vector3 Abs(Vector3 v)
			=> new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

		/// <summary>Radius of the circle inscribed in the collider bounds, perpendicular to <paramref name="normal"/>.</summary>
		private static float InscribedRadius(Vector3 extents, Vector3 normal) {
			var n  = normal.normalized;
			var px = Vector3.ProjectOnPlane(new Vector3(extents.x, 0f, 0f), n).magnitude;
			var py = Vector3.ProjectOnPlane(new Vector3(0f, extents.y, 0f), n).magnitude;
			var pz = Vector3.ProjectOnPlane(new Vector3(0f, 0f, extents.z), n).magnitude;

			var min = Mathf.Min(px, Mathf.Min(py, pz));
			var max = Mathf.Max(px, Mathf.Max(py, pz));
			return Mathf.Max(px + py + pz - min - max, 0.01f);   // the middle value
		}

		/// <summary>
		/// Scene-view helpers: the tested collider, the allowed side (circle on the face + direction cone), the
		/// distance range and the allowed forward range. Colors: grey = tested collider, teal = allowed side,
		/// blue = distance, yellow = forward.
		/// </summary>
		public void OnDrawGizmos() {
			var col = ConditionCollider;
			if (col == null)
				return;

			var centre = col.bounds.center;
			var reach  = Mathf.Max(col.bounds.extents.magnitude, 0.05f);

			// Collider the conditions are tested against.
			Gizmos.Color = new Color(0.75f, 0.75f, 0.75f, 1f);
			Gizmos.DrawWireCube(centre, col.bounds.size);

			// Allowed side: circle inscribed on that face + a cone towards it (VRChat-like).
			if (allowedSide != Vector3.zero) {
				var normal     = col.transform.TransformDirection(allowedSide.normalized);
				var halfDepth  = Vector3.Dot(col.bounds.extents, Abs(normal));
				var faceCentre = centre + normal * halfDepth;
				var radius     = InscribedRadius(col.bounds.extents, normal);

				Gizmos.Color = new Color(0.15f, 0.85f, 0.75f, 1f);
				Gizmos.DrawSideRange(faceCentre, normal, radius);
				Gizmos.DrawLabel(faceCentre + normal * radius, "grab side", 11, new Color(0.15f, 0.85f, 0.75f, 1f));
			}

			// Distance range allowed around the collider.
			if (maxDistance > 0f) {
				Gizmos.Color = new Color(0.2f, 0.6f, 1f, 1f);
				Gizmos.DrawRange(centre, maxDistance);
			}

			// Palm forward range allowed around the collider.
			if (allowedForward != Vector3.zero) {
				var direction = col.transform.TransformDirection(allowedForward.normalized);
				Gizmos.Color = new Color(1f, 0.85f, 0.1f, 1f);
				Gizmos.DrawArrow(centre, direction, reach);
				if (maxAngle > 0f && maxAngle < 180f)
					Gizmos.DrawAngleRange(centre, direction, maxAngle * 2f, reach);
				Gizmos.DrawLabel(centre + direction * reach, "forward ±" + maxAngle.ToString("0") + "°", 11, Color.yellow);
			}
		}
	}
}
