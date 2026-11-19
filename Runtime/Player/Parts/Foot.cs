using Nox.CCK.Entities;
using Nox.CCK.Utils;
using UnityEngine;

namespace Nox.XR.Player {
	public enum FootSide {
		Left,
		Right
	}

	/// <summary>
	/// Tracked foot of a <see cref="Player"/>.
	/// <para>
	/// Feet give the player a real ground source: instead of only sweeping the body capsule
	/// (AutoHand behaviour), <see cref="Player"/> asks each foot for its ground hit first.
	/// </para>
	/// </summary>
	[AddComponentMenu("Nox/XR/Player/Foot Part")]
	public class Foot : Part {
		[SerializeField]
		private FootSide side = FootSide.Left;

		[SerializeField, Tooltip("Bottom of the foot. Defaults to this transform.")]
		private Transform sole;

		[SerializeField, Min(0.01f)]
		private float radius = 0.08f;

		[SerializeField, Min(0f), Tooltip("Extra probe distance above the sole.")]
		private float probeHeight = 0.1f;

		public Transform Sole => sole ? sole : transform;

		public bool IsGrounded { get; private set; }

		public RaycastHit GroundHit { get; private set; }

		protected override EntityRig DefaultRig => side == FootSide.Left ? EntityRig.LeftFoot : EntityRig.RightFoot;

		public FootSide Side {
			get => side;
			set => side = value;
		}

		public float Radius {
			get => radius;
			set => radius = Mathf.Max(0.01f, value);
		}

		public override void SetPosition(Vector3 position)
			=> transform.position = position;

		public override void SetRotation(Quaternion rotation)
			=> transform.rotation = rotation;

		/// <summary>Instantiate a runtime foot from its template prefab.</summary>
		/// <param name="template">Foot template prefab.</param>
		/// <param name="rig">Rig slot the foot occupies (<see cref="EntityRig.LeftFoot"/> or <see cref="EntityRig.RightFoot"/>).</param>
		/// <param name="parent">Tracking container receiving the instance.</param>
		public static Foot Create(Foot template, EntityRig rig, Transform parent) {
			var foot = template.gameObject.Instantiate<Foot>(parent);
			var footSide = rig == EntityRig.LeftFoot ? FootSide.Left : FootSide.Right;

			foot.side = footSide;
			foot.Configure(footSide == FootSide.Left ? "FootLeft" : "FootRight", rig);
			return foot;
		}

		/// <summary>
		/// Probes the ground below the sole.
		/// </summary>
		/// <param name="mask">Layers considered as ground.</param>
		/// <param name="maxStepHeight">Highest point the foot can reach.</param>
		/// <param name="maxAngle">Maximum slope angle.</param>
		/// <param name="hit">Ground hit.</param>
		public bool TryGetGround(LayerMask mask, float maxStepHeight, float maxAngle, out RaycastHit hit) {
			var bottom = Sole.position;
			var origin = bottom + Vector3.up * (probeHeight + radius);
			var distance = probeHeight + maxStepHeight + radius * 2f;

			if (Physics.SphereCast(origin, radius, Vector3.down, out hit, distance, mask, QueryTriggerInteraction.Ignore)
			    && Vector3.Angle(hit.normal, Vector3.up) <= maxAngle) {
				IsGrounded = true;
				GroundHit = hit;
				return true;
			}

			IsGrounded = false;
			GroundHit = default;
			return false;
		}

		protected override bool SupportsRig(EntityRig value)
			=> value == EntityRig.LeftFoot || value == EntityRig.RightFoot;

		protected override void OnDrawGizmosSelected() {
			base.OnDrawGizmosSelected();
			Gizmos.color = IsGrounded ? Color.green : Color.red;
			Gizmos.DrawWireSphere(Sole.position, radius);
		}
	}
}
