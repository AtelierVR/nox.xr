using Nox.CCK.Entities;
using Nox.CCK.Utils;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.XR.Player {
	/// <summary>
	/// Base class of every tracked part of a <see cref="Player"/> (head, hands, feet...).
	/// <para>
	/// Parts replace the hardcoded AutoHand references: the player never depends on a concrete
	/// interaction implementation, it only resolves parts by <see cref="EntityRig"/>.
	/// </para>
	/// </summary>
	[DisallowMultipleComponent]
	public abstract class Part : MonoBehaviour {
		[SerializeField, Tooltip("Optional rig override. Keep None to use the part default.")]
		private EntityRig rig = EntityRig.None;

		[SerializeField, Tooltip("Optional transform carrying the raw tracked pose (controller, tracker, headset...).")]
		private Transform trackingSource;

		[SerializeField, Tooltip("Whether the player drives this part. Disable it to keep the part in the registry but stop all tracking/interaction callbacks.")]
		private bool partEnabled = true;

		[Header("Environment")]
		[SerializeField, Tooltip("Keep this part out of level geometry: the player pushes it back when it enters a wall, the floor or a ceiling.")]
		private bool collideWithEnvironment = true;

		[SerializeField, Tooltip("Volume of this part in the environment queries (a trigger collider is fine).")]
		private Collider environmentCollider;

		[SerializeField, Tooltip("Extra colliders of this part that belong to the body (finger capsules, encapsulation box...). The environment volume is always added.")]
		private Collider[] bodyColliders = Array.Empty<Collider>();

		[SerializeField, Tooltip("Crouch the player while this part is under a ceiling lower than its standing clearance (for example a hand reaching in a crawlspace).")]
		private bool crouchUnderCeiling;

		[SerializeField, Min(0f), Tooltip("Headroom needed above this part to stay standing.")]
		private float standingClearance = 0.35f;

		private Vector3 _environmentOffset;
		private Vector3 _rawPosition;
		private bool _hasRawPosition;

		/// <summary>Whether this part is currently driven by its player.</summary>
		public bool PartEnabled => partEnabled;

		/// <summary>
		/// Whether this part stays alive when its device stops tracking or leaves.
		/// <para>
		/// A part spawned from a template is destroyed with its device: a hand that is off has no node to keep.
		/// A part that the prefab owns and that carries something the rig cannot rebuild - the head, which
		/// holds the view camera - is only unregistered, then registered again when the device comes back.
		/// </para>
		/// </summary>
		public virtual bool PersistsWithoutSource => false;

		/// <summary>Rig slot this part occupies.</summary>
		public virtual EntityRig Rig => rig != EntityRig.None ? rig : DefaultRig;

		/// <summary>Rig slot used when no explicit override is set.</summary>
		protected abstract EntityRig DefaultRig { get; }

		/// <summary>Player this part is attached to, or <c>null</c>.</summary>
		public Player Player { get; private set; }

		public bool IsAttached => Player != null;

		/// <summary>Raw tracked pose, falling back to the part transform.</summary>
		public virtual Transform TrackingSource => trackingSource ? trackingSource : transform;

		/// <summary>
		/// Transform the avatar rig must drive for this part: the tracked pose itself, by default.
		/// <para>
		/// The part <b>is</b> the target: the solver reads this transform, so the tracked pose
		/// reaches the bones without anybody copying it every frame. A part whose rig target is
		/// not its own transform (the head, whose target is the head bone and not the eyes)
		/// returns a dedicated node here.
		/// </para>
		/// </summary>
		public virtual Transform RigTarget => transform;

		/// <summary>Serialized tracking override, usable by subclasses that have a better default.</summary>
		protected Transform TrackingSourceOverride => trackingSource;

		/// <summary>Whether the player keeps this part out of level geometry.</summary>
		public bool CollideWithEnvironment {
			get => collideWithEnvironment;
			set => collideWithEnvironment = value;
		}

		/// <summary>Whether a low ceiling above this part makes the player crouch.</summary>
		public bool CrouchUnderCeiling {
			get => crouchUnderCeiling;
			set => crouchUnderCeiling = value;
		}

		/// <summary>Headroom needed above this part to stay standing.</summary>
		public float StandingClearance {
			get => standingClearance;
			set => standingClearance = Mathf.Max(0f, value);
		}

		/// <summary>Set by the player when there is less headroom than <see cref="StandingClearance"/> above this part.</summary>
		public bool IsUnderLowCeiling { get; internal set; }

		/// <summary>Volume this part occupies for the environment queries.</summary>
		public virtual Collider EnvironmentCollider => environmentCollider;

		/// <summary>
		/// Colliders of this part that belong to the player's body, authored on the prefab. A part may own
		/// several volumes, and the player reads them again every time it rebuilds its body colliders.
		/// </summary>
		public virtual void GetBodyColliders(List<Collider> results) {
			if (results == null)
				return;

			for (var i = 0; i < bodyColliders.Length; i++)
				results.Add(bodyColliders[i]);

			var collider = EnvironmentCollider;

			if (collider)
				results.Add(collider);
		}

		/// <summary>World center of the environment volume.</summary>
		public virtual Vector3 CollisionCenter
			=> environmentCollider
				? environmentCollider.transform.TransformPoint(EnvironmentLocalCenter)
				: TrackingSource ? TrackingSource.position : transform.position;

		/// <summary>World radius of the environment volume (0 when the part has none).</summary>
		public virtual float CollisionRadius {
			get {
				if (!environmentCollider)
					return 0f;

				var scale = environmentCollider.transform.lossyScale;
				var maxScale = Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));

				return environmentCollider switch {
					SphereCollider sphere     => sphere.radius * maxScale,
					CapsuleCollider capsule   => Mathf.Max(capsule.radius, capsule.height * 0.5f) * maxScale,
					BoxCollider box           => Mathf.Max(box.size.x, Mathf.Max(box.size.y, box.size.z)) * 0.5f * maxScale,
					_                         => environmentCollider.bounds.extents.magnitude
				};
			}
		}

		private Vector3 EnvironmentLocalCenter
			=> environmentCollider switch {
				SphereCollider sphere      => sphere.center,
				CapsuleCollider capsule    => capsule.center,
				BoxCollider box            => box.center,
				_                          => Vector3.zero
			};

		/// <summary>
		/// Last tracked position, <b>without</b> the environment correction.
		/// Environment queries must use it: measuring the pose we already corrected feeds the correction
		/// back into itself and makes the part bounce frame after frame.
		/// </summary>
		public Vector3 RawPosition
			=> _hasRawPosition
				? _rawPosition
				: TrackingSource ? TrackingSource.position : transform.position;

		/// <summary>Correction the player applies on top of the tracked pose to keep the part out of geometry.</summary>
		public Vector3 EnvironmentOffset => _environmentOffset;

		/// <summary>Set (or clear, with <see cref="Vector3.zero"/>) the correction applied to the tracked pose.</summary>
		public void SetEnvironmentOffset(Vector3 offset)
			=> _environmentOffset = offset;

		/// <summary>
		/// Whether the part carries a follow behaviour: the tracking space can move or turn under it, and the part
		/// has to keep its place in the world. Hands answer true; the head and a plain tracker do not.
		/// </summary>
		public virtual bool CanFollow
			=> false;

		/// <summary>
		/// Called after the tracking space moved, for the parts that <see cref="CanFollow"/>: keeps the part out
		/// of the geometry the move pushed it through.
		/// </summary>
		/// <param name="previousPosition">Pose of the part before the tracking space moved.</param>
		public virtual void FollowTrackingSpace(Vector3 previousPosition) { }

		/// <summary>Called after the tracking space turned, for the parts that <see cref="CanFollow"/>.</summary>
		public virtual void FollowTurn() { }

		public virtual bool IsTracked => partEnabled && isActiveAndEnabled;

		/// <summary>Raised when the part is attached to a player.</summary>
		public UnityEvent<Part> Attached = new();

		/// <summary>Raised when the part is detached from its player.</summary>
		public UnityEvent<Part> Detached = new();

		/// <summary>Raised when the part is enabled or disabled (see <see cref="SetEnabled"/>).</summary>
		public UnityEvent<Part, bool> EnabledChanged = new();

		/// <summary>Enable or disable this part without detaching it.</summary>
		public virtual void SetEnabled(bool value) {
			if (partEnabled == value)
				return;

			partEnabled = value;
			OnEnabledChanged(value);
			EnabledChanged.Invoke(this, value);
		}

		protected virtual void OnEnabledChanged(bool value) { }

		/// <summary>Attach the part to a player. Called by <see cref="Player"/>.</summary>
		public void Attach(Player player) {
			if (!player || Player == player)
				return;

			if (Player)
				Player.UnregisterPart(this);

			Player = player;
			OnAttach();
			Attached.Invoke(this);
		}

		/// <summary>Detach the part from a player. Called by <see cref="Player"/>.</summary>
		public void Detach(Player player) {
			if (Player != player)
				return;

			OnDetach();
			Player = null;
			Detached.Invoke(this);
		}

		protected virtual void OnAttach() { }
		protected virtual void OnDetach() { }

		protected virtual void OnEnable() { }
		protected virtual void OnDisable() { }

		/// <summary>Called by the player from <c>Update</c>.</summary>
		public virtual void OnPlayerUpdate() { }

		/// <summary>Called by the player from <c>LateUpdate</c>.</summary>
		public virtual void OnPlayerLateUpdate() { }

		/// <summary>Called by the player from <c>FixedUpdate</c>.</summary>
		public virtual void OnPlayerFixedUpdate() { }

		/// <summary>Called after the player was moved or rotated externally.</summary>
		public virtual void OnPlayerTeleport() { }

		/// <summary>Called after the player turned around its head.</summary>
		public virtual void OnPlayerTurn(float angle) { }

		/// <summary>Set the tracked position of this part. The environment correction is applied on top.</summary>
		public virtual void SetPosition(Vector3 position) {
			SetRawPosition(position);

			var source = TrackingSource;
			if (source)
				source.position = position + _environmentOffset;
		}

		/// <summary>
		/// Records the tracked position <b>without</b> moving anything, so a part that writes its pose somewhere
		/// else (the hand writes its follow target) still reports the raw pose the environment queries need.
		/// </summary>
		protected void SetRawPosition(Vector3 position) {
			_rawPosition = position;
			_hasRawPosition = true;
		}

		/// <summary>Set the tracked rotation of this part.</summary>
		public virtual void SetRotation(Quaternion rotation) {
			var source = TrackingSource;
			if (source)
				source.rotation = rotation;
		}

		/// <summary>Set the linear velocity of this part (no-op when the part has no body).</summary>
		public virtual void SetVelocity(Vector3 velocity) { }

		/// <summary>Set the angular velocity of this part (no-op when the part has no body).</summary>
		public virtual void SetAngularVelocity(Vector3 angularVelocity) { }

		/// <summary>Set both the tracked position and rotation.</summary>
		public virtual void SetPose(Vector3 position, Quaternion rotation) {
			SetPosition(position);
			SetRotation(rotation);
		}

		/// <summary>Apply a transform snapshot coming from a loader, a binding or the network.</summary>
		public virtual void Apply(TransformObject transform) {
			if (transform == null)
				return;

			if (transform.Flags.HasFlag(TransformFlags.Position))
				SetPosition(transform.GetPosition());
			if (transform.Flags.HasFlag(TransformFlags.Rotation))
				SetRotation(transform.GetRotation());
			if (transform.Flags.HasFlag(TransformFlags.Velocity))
				SetVelocity(transform.GetVelocity());
			if (transform.Flags.HasFlag(TransformFlags.Angular))
				SetAngularVelocity(transform.GetAngular());
		}

		/// <summary>
		/// Configure an instance created from a template prefab: name and rig slot the instance occupies.
		/// Templates are prefab assets (components, layers, rigidbody settings), never built at runtime.
		/// </summary>
		protected void Configure(string objectName, EntityRig rigSlot) {
			name = objectName;
			rig = rigSlot;
		}

		protected virtual void OnValidate() {
			if (rig != EntityRig.None && !SupportsRig(rig)) {
				Logger.LogWarning($"{rig} is not supported by this part, falling back to its default rig.", this, tag: nameof(Part));
				rig = EntityRig.None;
			}
		}

		protected virtual bool SupportsRig(EntityRig value) => true;

		protected virtual void OnDrawGizmosSelected() {
			Gizmos.color = Color.cyan;
			Gizmos.DrawWireSphere(TrackingSource.position, 0.02f);
		}
	}
}
