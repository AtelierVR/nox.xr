using System;
using System.Collections;
using System.Collections.Generic;
using Nox.CCK.Entities;
using Nox.CCK.Utils;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Serialization;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.XR.Player {
	public enum PlayerRotationType {
		Snap,
		Smooth
	}

	public delegate void PlayerEvent(Player player);

	/// <summary>
	/// Physics-based player body, open-source replacement of AutoHand's <c>AutoHandPlayer</c>.
	/// <para>
	/// The player is <b>not</b> a controller: it never reads input. It receives <see cref="Move"/> and
	/// <see cref="Turn"/> from whatever controller is active (XR controllers, keyboard, AI...) and only
	/// owns the body, the tracked container and the <see cref="Part"/> registry.
	/// </para>
	/// <para>
	/// Every tracked limb is a <see cref="Part"/> identified by its <see cref="EntityRig"/>
	/// (for example <see cref="Head"/>, <see cref="Hand"/> and <see cref="Foot"/>).
	/// This replaces the hardcoded <c>handLeft</c>/<c>handRight</c> fields of AutoHand: hands are simply
	/// <c>EntityRig.LeftHand</c> and <c>EntityRig.RightHand</c> parts.
	/// </para>
	/// <para>
	/// The implementation is split into partial files:
	/// <c>Player.Movement.cs</c>, <c>Player.Grounding.cs</c>, <c>Player.Interaction.cs</c> and
	/// <c>Player.Teleport.cs</c>.
	/// </para>
	/// </summary>
	[RequireComponent(typeof(Rigidbody)), DefaultExecutionOrder(1)]
	[AddComponentMenu("Nox/XR/Player/Player")]
	public partial class Player : MonoBehaviour {
		/// <summary>Active players. There is no singleton: several players can coexist in a scene.</summary>
		private static readonly List<Player> Active = new();

		/// <summary>Every currently enabled <see cref="Player"/> (local player, ghost, NPC player bodies...).</summary>
		public static IReadOnlyList<Player> Instances => Active;

		/// <summary>Number of currently enabled players.</summary>
		public static int InstanceCount => Active.Count;

		[Header("Parts")]
		[Tooltip("Parts this player owns. Every other part is registered by whoever creates it (PlayerPartBinder for hands, feet and trackers).")]
		public Part[] serializedParts = Array.Empty<Part>();

		[Header("Tracking")]
		[SerializeField, Tooltip("Container holding all the tracked objects (head, hands, feet). It must NOT be a child of this body.")]
		private Transform trackingContainer;

		[SerializeField, Tooltip("Transform used for the forward movement direction. Defaults to the head view.")]
		private Transform forwardFollow;

		[Header("Movement")]
		public bool useMovement = true;
		public float maxMoveSpeed = 2.3f;
		public float moveAcceleration = 100000f;

		public PlayerRotationType rotationType = PlayerRotationType.Snap;
		public float snapTurnAngle = 30f;
		public float smoothTurnSpeed = 180f;
		public bool useSmoothStep;
		public float stepSmoothSpeed = 6f;
		public float movementDeadzone = 0.2f;
		public float turnDeadzone = 0.4f;
		public float turnResetzone = 0.3f;

		[Header("Height")]
		public float heightSmoothSpeed = 10f;
		public bool crouching;
		public float crouchHeight = 0.6f;
		public Vector2 minMaxHeight = new Vector2(Constants.MINIMUM_BODY_HEIGHT, Constants.MAXIMUM_BODY_HEIGHT);

		[Header("Environment collision")]
		[Tooltip("Push the parts flagged with environment collision out of level geometry.")]
		public bool useEnvironmentCollision = true;
		[Tooltip("Crouch automatically while a part is under a low ceiling.")]
		public bool autoCrouch = true;
		[Tooltip("Delay before standing up again once nothing squeezes the player anymore.")]
		public float autoCrouchReleaseDelay = 0.25f;

		[Header("Grounding")]
		[Tooltip("Sphere collider placed at the bottom of the body. It is the body collider and the ground probe.")]
		public SphereCollider floorCollider;
		public bool useGrounding = true;
		public bool useFootGrounding = true;
		public float maxStepHeight = 0.3f;
		public float groundingPenetrationOffset = 0.1f;
		public float maxStepAngle = 45f;
		public LayerMask groundLayerMask;
		public float groundedDrag = 10000f;
		public float flyingDrag = 4f;

		[Header("Body colliders")]
		[Tooltip("Colliders of the body itself, authored on the prefab. The parts add their own (see Part.GetBodyColliders).")]
		[SerializeField] private Collider[] bodyColliders = Array.Empty<Collider>();

		[Header("Platforming")]
		public bool allowPlatforms = true;
		public LayerMask platformingLayerMask = ~0;

		[Header("Events")]
		[Tooltip("A part was registered on this player.")]
		public UnityEvent<Part> OnPartRegistered = new();

		[Tooltip("A part was unregistered from this player.")]
		public UnityEvent<Part> OnPartUnregistered = new();

		[Tooltip("The height offset changed: the tracking space moved, and everything bound to it (the avatar, the rig targets) has to follow.")]
		public UnityEvent<float> OnHeightOffset = new();

		[Tooltip("The body moved: the locomotion values (velocity in the frame of the view) changed.")]
		public UnityEvent OnMove = new();

		[Tooltip("The player jumped.")]
		public UnityEvent OnJump = new();

		[Tooltip("The player landed or left the ground.")]
		public UnityEvent<bool> OnGrounded = new();

		public PlayerEvent OnSnapTurn;
		public PlayerEvent OnSmoothTurn;
		public PlayerEvent OnTeleported;

		/// <summary>
		/// Vertical offset applied to the tracking space, in metres.
		/// <para>
		/// The player raises its tracking space to place the camera at the right height: the
		/// avatar must not inherit that offset (it would float above the floor). The offset is
		/// therefore a property that announces its changes, instead of a field the consumers
		/// would have to poll.
		/// </para>
		/// </summary>
		public float heightOffset {
			get => _heightOffset;
			set {
				if (Mathf.Approximately(_heightOffset, value))
					return;

				_heightOffset = value;
				OnHeightOffset.Invoke(value);
			}
		}

		private readonly Dictionary<EntityRig, Part> _parts = new();
		private readonly HashSet<Hand> _subscribedHands = new();
		private readonly HashSet<Part> _allParts = new();

		/// <summary>Parts carrying a follow behaviour, reused by the per-frame passes so they allocate nothing.</summary>
		private readonly Part[] _followed = new Part[4];

		/// <summary>Pose of those parts before the tracking space moved.</summary>
		private readonly Vector3[] _followStarts = new Vector3[4];

		/// <summary>Backing field of <see cref="heightOffset"/>.</summary>
		[FormerlySerializedAs("heightOffset")]
		private float _heightOffset;
		private readonly RaycastHit[] _hitsNonAlloc = new RaycastHit[128];

		private Coroutine _disableGroundingRoutine;
		private bool _trackingStarted;
		private bool _isGrounded;
		private bool _axisReset = true;
		private bool _tempDisableGrounding;
		private bool _lastCrouching;
		private float _lastCrouchingHeight;
		private bool _autoCrouching;
		private float _lastAutoCrouchTime;
		private float _highestPoint;
		private Vector3 _lastUpdatePosition;
		private Vector3 _lastHeadPos;
		private Vector3 _notifiedVelocity;
		private Vector3 _headVelocity;
		private Vector3 _moveDirection;
		private Vector3 _targetTrackedPos;
		private Vector3 _lastPlatformPosition;
		private Quaternion _lastPlatformRotation;
		private float _turningAxis;
		private int _handPlayerMask;
		private int _handPartMask;

		/// <summary>Whether the player crouches, because of the input or of the environment.</summary>
		public bool IsCrouching => crouching || _autoCrouching;

		/// <summary>Radius of the body collider.</summary>
		public float BodyRadius => floorCollider.radius;

		public Rigidbody body { get; protected set; }

		public RaycastHit lastGroundHit { get; protected set; }

		public RaycastHit lastPlatformingHit { get; protected set; }

		public int handPlayerMask => _handPlayerMask;

		/// <summary>
		/// Mask of the layers a <see cref="Hand"/> collides with. Read lazily: the hands change their own rules
		/// when they spawn (see <c>HandPhysics</c>), which is after the player woke up, so a mask captured in
		/// <c>Awake</c> would miss the layers they just turned on (the menu panel, for instance).
		/// </summary>
		public int handPartMask
			=> _handPartMask != 0 ? _handPartMask : RefreshHandPartMask();

		/// <summary>Recomputes <see cref="handPartMask"/> now. Called by the hands when they change their layer rules.</summary>
		public int RefreshHandPartMask() {
			_handPartMask = Layers.LayerExists(Layers.Hand)
				? GetPhysicsLayerMask(LayerMask.NameToLayer(Layers.Hand))
				: _handPlayerMask;

			return _handPartMask;
		}

		/// <summary>Registered parts, keyed by their rig slot.</summary>
		public IReadOnlyDictionary<EntityRig, Part> Parts => _parts;

		public Transform TrackingContainer => trackingContainer;

		/// <summary>
		/// Root the parts carrying a Rigidbody must be parented to.
		/// <para>
		/// The tracking space is moved every frame and a rigidbody must never be moved through its parent:
		/// that corrupts its interpolated pose (the transform flies away from the physics position).
		/// This root is only ever moved together with the whole player rig.
		/// </para>
		/// </summary>
		public Transform RigRoot
			=> trackingContainer ? trackingContainer.parent : transform.parent;

		public Transform ForwardFollow => forwardFollow;

		/// <summary>Camera of the head part, if any (AutoHand compatibility).</summary>
		public Camera headCamera => Head ? Head.Camera : null;

		public Head Head => GetPart(EntityRig.Head) as Head;
		public Hand LeftHand => GetPart(EntityRig.LeftHand) as Hand;
		public Hand RightHand => GetPart(EntityRig.RightHand) as Hand;
		public Foot LeftFoot => GetPart(EntityRig.LeftFoot) as Foot;
		public Foot RightFoot => GetPart(EntityRig.RightFoot) as Foot;

		public Part GetPart(EntityRig rig)
			=> _parts.TryGetValue(rig, out var part) ? part : null;

		public bool TryGetPart<T>(EntityRig rig, out T part) where T : Part {
			part = GetPart(rig) as T;
			return part != null;
		}

		public Hand GetHand(HandSide side)
			=> side == HandSide.Left ? LeftHand : RightHand;

		private readonly List<Collider> _bodyColliders = new();
		private readonly List<Collider> _bodyStaticColliders = new();
		private readonly List<Collider> _bodyPartColliders = new();
		private readonly Dictionary<object, Collider[]> _bodyExtraColliders = new();

		/// <summary>
		/// Colliders of the player's own body: the ones serialized on the body prefab, the ones of the
		/// registered parts and the extra ones a source registered. A held object ignores them (see
		/// <see cref="IGrabberBody"/>), so it can be carried into the player without colliding with - or
		/// pushing - it.
		/// </summary>
		public IReadOnlyList<Collider> BodyColliders
			=> _bodyColliders;

		/// <summary>Seeds the static list from the prefab fields; the parts are read on every rebuild.</summary>
		private void BuildBodyColliders() {
			_bodyStaticColliders.Clear();

			for (var i = 0; i < bodyColliders.Length; i++)
				_bodyStaticColliders.Add(bodyColliders[i]);

			if (floorCollider && !_bodyStaticColliders.Contains(floorCollider))
				_bodyStaticColliders.Add(floorCollider);

			RefreshBodyColliders();
		}

		/// <summary>
		/// Rebuilds the body colliders: the prefab ones, those the registered parts declare (a hand may
		/// expose several) and the extra ones. Called when a part changes, and callable at any time by
		/// whoever changes the colliders of the body (a loaded avatar hand, a worn prop).
		/// </summary>
		public void RefreshBodyColliders() {
			_bodyColliders.Clear();

			for (var i = 0; i < _bodyStaticColliders.Count; i++)
				AddBodyCollider(_bodyStaticColliders[i]);

			_bodyPartColliders.Clear();

			foreach (var part in _allParts)
				if (part)
					part.GetBodyColliders(_bodyPartColliders);

			for (var i = 0; i < _bodyPartColliders.Count; i++)
				AddBodyCollider(_bodyPartColliders[i]);

			foreach (var entry in _bodyExtraColliders) {
				var colliders = entry.Value;

				if (colliders == null)
					continue;

				for (var i = 0; i < colliders.Length; i++)
					AddBodyCollider(colliders[i]);
			}
		}

		/// <summary>
		/// Registers the colliders a source adds to the body (the finger colliders of a loaded avatar
		/// hand, a prop worn by the player). The set is replaced as a whole, and the call is safe at any
		/// time; <c>null</c> or empty removes the source.
		/// </summary>
		/// <param name="owner">Object identifying the source, so it can be replaced or removed.</param>
		/// <param name="colliders">Its colliders, or <c>null</c> to forget them.</param>
		public void SetBodyColliders(object owner, IReadOnlyList<Collider> colliders) {
			if (owner == null)
				return;

			if (colliders is not { Count: > 0 }) {
				if (_bodyExtraColliders.Remove(owner))
					RefreshBodyColliders();

				return;
			}

			var copy = new Collider[colliders.Count];

			for (var i = 0; i < copy.Length; i++)
				copy[i] = colliders[i];

			_bodyExtraColliders[owner] = copy;
			RefreshBodyColliders();
		}

		/// <summary>Forgets the colliders of a source (see <see cref="SetBodyColliders"/>).</summary>
		public void ClearBodyColliders(object owner)
			=> SetBodyColliders(owner, null);

		private void AddBodyCollider(Collider collider) {
			if (collider && !_bodyColliders.Contains(collider))
				_bodyColliders.Add(collider);
		}

		/// <summary>Whether the part registered for this rig is enabled.</summary>
		public bool IsPartEnabled(EntityRig rig)
            => GetPart(rig) is { PartEnabled: true };

		/// <summary>Enable or disable the part registered for this rig.</summary>
		public bool SetPartEnabled(EntityRig rig, bool enabled) {
			var part = GetPart(rig);
			if (!part)
				return false;

			part.SetEnabled(enabled);
			return true;
		}

		/// <summary>Enable or disable a specific part.</summary>
		public void SetPartEnabled(Part part, bool enabled) {
			if (part)
				part.SetEnabled(enabled);
		}

		/// <summary>Set the tracked values of a part identified by its rig.</summary>
		public bool SetPart(EntityRig rig, Vector3 position, Quaternion rotation, Vector3 velocity, Vector3 angularVelocity) {
			var part = GetPart(rig);
			if (!part || !part.PartEnabled)
				return false;

			part.SetPose(position, rotation);
			part.SetVelocity(velocity);
			part.SetAngularVelocity(angularVelocity);
			return true;
		}

		/// <summary>Set the tracked values of a part identified by its rig index (network/loader wire format).</summary>
		public bool SetPart(ushort index, Vector3 position, Quaternion rotation, Vector3 velocity, Vector3 angularVelocity)
			=> SetPart(index.ToEntityRig(), position, rotation, velocity, angularVelocity);

		/// <summary>Apply a transform snapshot to a part identified by its rig.</summary>
		public bool SetPart(EntityRig rig, TransformObject transform) {
			var part = GetPart(rig);
			if (!part || !part.PartEnabled || transform == null)
				return false;

			part.Apply(transform);
			return true;
		}

		/// <summary>Apply a transform snapshot to a part identified by its rig index (network wire format).</summary>
		public bool SetPart(ushort index, TransformObject transform)
			=> SetPart(index.ToEntityRig(), transform);

		/// <summary>Register a part and attach it to this player.</summary>
		public bool RegisterPart(Part part) {
			if (!part)
				return false;

			var rig = part.Rig;
			if (rig == EntityRig.None) {
				Logger.LogWarning($"Cannot register {part.GetType().Name}: it has no rig.", part, tag: nameof(Player));
				return false;
			}

			if (_parts.TryGetValue(rig, out var existing) && existing) {
				if (existing == part)
					return true;

				UnregisterPart(existing);
			}

			_parts[rig] = part;
			_allParts.Add(part);
			part.Attach(this);
			part.EnabledChanged.RemoveListener(OnPartEnabledChanged);
			part.EnabledChanged.AddListener(OnPartEnabledChanged);

			RefreshBodyColliders();

			if (part is Hand hand) {
				_handPartMask = 0;
				SubscribeHand(hand);
			}

			OnPartRegistered.Invoke(part);
			return true;
		}

		/// <summary>Unregister a part from this player.</summary>
		public bool UnregisterPart(Part part) {
			if (!part)
				return false;

			var removed = _allParts.Remove(part);

			if (_parts.TryGetValue(part.Rig, out var current) && current == part)
				_parts.Remove(part.Rig);

			if (part is Hand hand) {
				_handPartMask = 0;
				UnsubscribeHand(hand);
			}

			part.EnabledChanged.RemoveListener(OnPartEnabledChanged);
			part.Detach(this);

			RefreshBodyColliders();

			if (removed)
				OnPartUnregistered.Invoke(part);

			return removed;
		}

		protected virtual void Awake() {
			_lastUpdatePosition = transform.position;

			body = GetComponent<Rigidbody>();

			BuildBodyColliders();

			_handPlayerMask = GetPhysicsLayerMask(gameObject.layer);
			RefreshHandPartMask();

			RegisterParts();

			if (!Head)
				Logger.LogError($"No {nameof(Head)} part found: the player cannot track anything.", this, tag: nameof(Player));

			if (!floorCollider)
				Logger.LogError($"No {nameof(floorCollider)} assigned: the player has no body collider.", this, tag: nameof(Player));

			if (!trackingContainer)
				Logger.LogError($"No {nameof(trackingContainer)} assigned: the player cannot move its tracking space.", this, tag: nameof(Player));

			if (!forwardFollow)
				Logger.LogError($"No {nameof(forwardFollow)} assigned: the player has no movement direction.", this, tag: nameof(Player));

			if (trackingContainer)
				_targetTrackedPos = trackingContainer.position;
		}

		protected virtual void Start() {
			if (Head)
				StartCoroutine(WaitFlagForTrackingStart());
		}

		protected virtual void OnEnable() {
			if (!Active.Contains(this))
				Active.Add(this);

			foreach (var part in _parts.Values)
				if (part is Hand hand)
					SubscribeHand(hand);
		}

		protected virtual void OnDisable() {
			Active.Remove(this);

			var hands = new List<Hand>(_subscribedHands);
			for (var i = 0; i < hands.Count; i++)
				UnsubscribeHand(hands[i]);

			_subscribedHands.Clear();
		}

		protected virtual void OnDestroy() {
			Active.Remove(this);
		}

		protected virtual void OnValidate() {
			if (serializedParts == null)
				serializedParts = Array.Empty<Part>();

			if (minMaxHeight.x < 0.1f)
				minMaxHeight.x = 0.1f;
			if (minMaxHeight.y < minMaxHeight.x)
				minMaxHeight.y = minMaxHeight.x;
		}

		/// <summary>
		/// Registers the parts this player is wired to explicitly (<see cref="serializedParts"/>).
		/// <para>
		/// Nothing is discovered by scanning the hierarchy: the head is referenced by the prefab, and the
		/// hands, feet and trackers are registered by <see cref="PlayerPartBinder"/> when it creates them.
		/// </para>
		/// </summary>
		private void RegisterParts()
			=> RegisterPartRange(serializedParts);

		private void RegisterPartRange(Part[] parts) {
			if (parts == null)
				return;

			for (var i = 0; i < parts.Length; i++)
				if (parts[i])
					RegisterPart(parts[i]);
		}

		private void SubscribeHand(Hand hand) {
			if (!hand || !hand.PartEnabled || !_subscribedHands.Add(hand))
				return;
		}

		private void OnPartEnabledChanged(Part part, bool enabled) {
			if (part is not Hand hand)
				return;

			RefreshBodyColliders();

			if (enabled)
				SubscribeHand(hand);
			else
				UnsubscribeHand(hand);
		}

		private void UnsubscribeHand(Hand hand) {
			if (!hand || !_subscribedHands.Remove(hand))
				return;
		}

		protected virtual void Update() {
			foreach (var part in _parts.Values)
				if (part.PartEnabled)
					part.OnPlayerUpdate();
		}

		protected virtual void LateUpdate() {
			if (useMovement && Head && trackingContainer) {
				UpdateTrackedObjects();
				UpdateTurn(Time.deltaTime);
			}

			ResolveEnvironmentCollisions();

			foreach (var part in _parts.Values)
				if (part.PartEnabled)
					part.OnPlayerLateUpdate();
		}

		protected virtual void FixedUpdate() {
			foreach (var part in _parts.Values)
				if (part.PartEnabled)
					part.OnPlayerFixedUpdate();

			UpdatePlayerHeight();

			if (!useMovement) {
				SetHeadVelocity(Vector3.zero);
				return;
			}

			UpdateRigidbody();
			SyncBodyHead();
			UpdatePlatform();
			Ground();
		}

		private IEnumerator WaitFlagForTrackingStart() {
			yield return new WaitForEndOfFrame();
			yield return new WaitForFixedUpdate();

			if (!Head)
				yield break;

			_lastHeadPos = Head.Position;
			while (!_trackingStarted) {
				if (Head.Position != _lastHeadPos)
					_trackingStarted = true;

				_lastHeadPos = Head.Position;
				yield return new WaitForEndOfFrame();
			}
		}

		private static int GetPhysicsLayerMask(int currentLayer) {
			var finalMask = 0;
			for (var i = 0; i < 32; i++)
				if (!Physics.GetIgnoreLayerCollision(currentLayer, i))
					finalMask |= 1 << i;
			return finalMask;
		}
	}
}
