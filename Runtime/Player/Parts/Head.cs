using Nox.CCK.Entities;
using UnityEngine;

namespace Nox.XR.Player {
	/// <summary>
	/// Tracked head of a <see cref="Player"/>: camera pose, view height and the collision volume
	/// used by the player to depenetrate its head.
	/// </summary>
	[AddComponentMenu("Nox/XR/Player/Head Part")]
	public class Head : Part {
		[SerializeField, Tooltip("Camera carrying the head pose.")]
		private Camera viewCamera;

		[SerializeField, Tooltip("Optional anchor for the collision sphere. Defaults to the camera.")]
		private Transform collisionAnchor;

		[SerializeField, Min(0.01f)]
		private float radius = 0.15f;

		[SerializeField, Tooltip("Trigger sphere used for the head depenetration queries.")]
		private SphereCollider collisionVolume;

		protected override EntityRig DefaultRig => EntityRig.Head;

		/// <summary>The head carries the view camera: it outlives its device and is only unregistered.</summary>
		public override bool PersistsWithoutSource => true;

		public Camera Camera => viewCamera;

		/// <summary>Raw head pose used by the player (camera transform when available).</summary>
		public Transform View => Camera ? Camera.transform : transform;

		/// <summary>
		/// Node the avatar rig drives for the head: the head <b>bone</b> pose, not the eye pose.
		/// <para>
		/// The rig expects the head bone: the eyes sit above and in front of it, and a rig fed
		/// with the eye pose pushes the head (and its mesh) forward. The node is a child of the
		/// tracked view, so it follows the head without any per-frame work: only its local offset
		/// is set, when the avatar is loaded.
		/// </para>
		/// </summary>
		private Transform _rigTarget;

		/// <summary>
		/// Offset between the eyes and the head bone, expressed in the tracked view frame.
		/// <para>
		/// It is a property of the <b>avatar</b> (the model, and its scale), not of the player:
		/// the controller reads it from the camera module of the avatar and hands it over.
		/// </para>
		/// </summary>
		public Vector3 EyeOffset {
			get => _eyeOffset;
			set {
				if (_eyeOffset == value)
					return;

				_eyeOffset = value;
				ApplyRigTarget();
			}
		}

		/// <summary>Backing field of <see cref="EyeOffset"/>.</summary>
		private Vector3 _eyeOffset;

		/// <summary>
		/// Transform the avatar rig must drive for the head: the head bone node.
		/// </summary>
		public override Transform RigTarget
			=> _rigTarget ? _rigTarget : base.RigTarget;

		private void Awake()
			=> ApplyRigTarget();

		/// <summary>
		/// Creates (once) the head bone node and places it on the head bone of the avatar.
		/// </summary>
		private void ApplyRigTarget() {
			var view = View;
			if (!view)
				return;

			if (!_rigTarget) {
				var node = new GameObject("Head Target");
				node.transform.SetParent(view, false);
				_rigTarget = node.transform;
			}

			// The eyes sit above the head bone: the target is the bone, so the offset is subtracted.
			_rigTarget.localPosition = -_eyeOffset;
			_rigTarget.localRotation = Quaternion.identity;
		}

		public Transform CollisionAnchor => collisionAnchor ? collisionAnchor : View;

		public override Transform TrackingSource => TrackingSourceOverride ? TrackingSourceOverride : View;

		public float Radius {
			get => radius;
			set {
				radius = Mathf.Max(0.01f, value);
				if (collisionVolume)
					collisionVolume.radius = radius;
			}
		}

		/// <summary>World position of the head collision volume.</summary>
		public Vector3 Position => CollisionAnchor.position;

		public override Collider EnvironmentCollider
			=> collisionVolume ? collisionVolume : base.EnvironmentCollider;

		public override Vector3 CollisionCenter => Position;

		public override float CollisionRadius => radius;

		public float EyeHeight => Player ? Position.y - Player.transform.position.y : 0f;

		/// <summary>
		/// Trigger sphere used only for <see cref="Physics.ComputePenetration"/> queries.
		/// It never participates in physics (and is ignored by the player layer mask).
		/// </summary>
		public SphereCollider CollisionVolume => collisionVolume;

		protected override bool SupportsRig(EntityRig value) => value == EntityRig.Head;

		protected override void OnDrawGizmosSelected() {
			base.OnDrawGizmosSelected();
			Gizmos.color = CollideWithEnvironment ? Color.yellow : Color.gray;
			Gizmos.DrawWireSphere(Position, Radius);
		}
	}
}
