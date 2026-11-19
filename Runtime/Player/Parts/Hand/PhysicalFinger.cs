using Nox.Avatars.Hand;
using Nox.CCK.Utils;
using Nox.XR.Runtime.Connectors;
using UnityEngine;

namespace Nox.XR.Player {
	/// <summary>
	/// One finger of the physical hand: its bones, their colliders and its poke.
	/// <para>
	/// The shape comes from the default hand of the prefab, or from the avatar finger it is fitted on: the
	/// local lengths, the rest pose and the blend poses (opened/closed) are the avatar's, and the finger gives
	/// its own rotations back to the avatar bones every frame.
	/// </para>
	/// </summary>
	[AddComponentMenu("Nox/XR/Player/Physical Finger")]
	public class PhysicalFinger : MonoBehaviour, IFinger {
		private const int BONE_COUNT = 4;

		private static readonly float[] _curlShare = { 0.55f, 0.3f, 0.15f };

		private sealed class ChainPose : IPose {
			public FingerCurl Curl { get; set; }
			public Quaternion[] Values { get; set; }
		}

		[SerializeField, Tooltip("Finger this chain stands for.")]
		private FingerType type = FingerType.Index;

		[SerializeField, Tooltip("Proximal, intermediate, distal and tip bone. Created as children when empty.")]
		private Transform[] bones = new Transform[BONE_COUNT];

		[SerializeField, Tooltip("Give the finger its colliders and its poke.")]
		private bool equip = true;

		[SerializeField, Range(0f, 120f), Tooltip("How far each phalanx folds at full curl, in degrees.")]
		private float curlAngle = 80f;

		private CapsuleCollider[] _capsules;
		private BoxCollider[] _boxes;
		private PokeInteractor _poke;

		private Vector3[] _defaultPosition;
		private Quaternion[] _defaultRotation;
		private Vector3[] _restPosition;
		private Quaternion[] _restRotation;
		private Transform[] _avatar;
		private IFinger _finger;
		private Vector3[] _sourcePosition;
		private Vector3[] _sourceScale;
		private bool _equipped = true;
		private float defaultRadius = 0.009f;

		public FingerType Type {
			get => type;
			set => type = value;
		}

		public Transform Proximal
			=> Bone(0);

		public Transform Intermediate
			=> Bone(1);

		public Transform Distal
			=> Bone(2);

		public Transform Tip
			=> Bone(3);

		public float TipRadius
			=> Radius;

		public IPose[] Poses { get; set; }

		/// <summary>Unscaled radius of the finger colliders and of the tip poke.</summary>
		public float Radius { get; set; } = 0.009f;

		public PokeInteractor Poke
			=> _poke;

		/// <summary>Avatar bones this finger is fitted on, or null when it uses the default hand.</summary>
		public Transform[] Avatar
			=> _avatar;

		public bool HasAvatar
			=> _avatar != null;

		private Transform Bone(int index)
			=> bones != null && index < bones.Length ? bones[index] : null;

		public void Initialize(FingerType fingerType, float radius, float foldAngle, Vector3[] defaultPosition, Quaternion[] defaultRotation) {
			type = fingerType;
			Radius = radius;
			defaultRadius = radius;
			curlAngle = foldAngle;

			CreateBones();

			_defaultPosition = defaultPosition;
			_defaultRotation = defaultRotation;
			_restPosition = (Vector3[])defaultPosition.Clone();
			_restRotation = (Quaternion[])defaultRotation.Clone();

			CreateColliders();

			Poses = DefaultPoses();
			Rest();
			Size();
		}

		private void CreateBones() {
			if (bones == null || bones.Length != BONE_COUNT)
				bones = new Transform[BONE_COUNT];

			for (var i = 0; i < BONE_COUNT; i++) {
				if (bones[i])
					continue;

				var node = new GameObject(i == BONE_COUNT - 1 ? $"{type} Tip" : $"{type} {i}");
				node.layer = gameObject.layer;

				var bone = node.transform;
				bone.SetParent(i == 0 ? transform : bones[i - 1], false);
				bones[i] = bone;
			}
		}

		private void CreateColliders() {
			if (!equip)
				return;

			_capsules ??= new CapsuleCollider[3];
			_boxes ??= new BoxCollider[3];

			for (var i = 0; i < _capsules.Length; i++) {
				if (!_capsules[i])
					_capsules[i] = bones[i].gameObject.GetOrAddComponent<CapsuleCollider>();

				if (!_boxes[i]) {
					var box = bones[i].gameObject.GetOrAddComponent<BoxCollider>();
					box.enabled = false;
					_boxes[i] = box;
				}
			}

			_poke ??= bones[BONE_COUNT - 1].gameObject.GetOrAddComponent<PokeInteractor>();

			var attach = _poke.attachTransform;
			if (attach && attach != _poke.transform) {
				attach.gameObject.Destroy();
				_poke.attachTransform = null;
			}

			var hand = GetComponentInParent<PhysicalHand>();
			if (hand)
				_poke.gripMaterial = hand.PokeGrip;

			_poke.gripCollider = _capsules[_capsules.Length - 1];
		}

		/// <summary>Fits the chain on an avatar finger: its lengths, its rest pose and its blend poses.</summary>
		public void Fit(IFinger finger) {
			if (finger == null || !finger.Proximal || !finger.Intermediate || !finger.Distal) {
				Unfit();
				return;
			}

			_avatar = new[] { finger.Proximal, finger.Intermediate, finger.Distal, finger.Tip };
			_finger = finger;

			if (finger.Poses != null)
				Poses = finger.Poses;

			var frame = _avatar[0].parent;
			var scale = frame ? frame.lossyScale : Vector3.one;
			var own = transform.lossyScale;

			_sourcePosition ??= new Vector3[BONE_COUNT];
			_sourceScale ??= new Vector3[BONE_COUNT];

			for (var i = 0; i < BONE_COUNT; i++) {
				var bone = _avatar[i];
				if (!bone)
					continue;

				var parent = bone.parent;
				var local = parent ? parent.lossyScale : scale;
				var position = bone.localPosition;

				_sourcePosition[i] = position;
				_sourceScale[i] = local;

				_restPosition[i] = new Vector3(
					position.x * Ratio(local.x, own.x),
					position.y * Ratio(local.y, own.y),
					position.z * Ratio(local.z, own.z));

				_restRotation[i] = bone.localRotation;
			}

			var tipScale = _avatar[BONE_COUNT - 1]
				? _avatar[BONE_COUNT - 1].lossyScale
				: scale;
			Radius = finger.TipRadius > 0f
				? finger.TipRadius * Ratio(tipScale.x, own.x)
				: defaultRadius;

			if (_poke)
				_poke.localRadius = Radius;

			Rest();
			Size();
			SetEquipped(IsActive());
		}

		/// <summary>
		/// Follows the avatar finger it was fitted on: its enable state, and a change of the offset or of the scale of
		/// one of its bones (the avatar was re-scaled, a finger was re-rigged). The rotation is <b>not</b> looked at:
		/// this hand writes it back to the avatar every frame, so it differs from the fitted rest by design.
		/// </summary>
		public bool Sync() {
			if (_finger == null || _avatar == null)
				return false;

			var changed = false;

			for (var i = 0; i < BONE_COUNT && !changed; i++) {
				var bone = _avatar[i];
				if (!bone)
					continue;

				var local = bone.parent ? bone.parent.lossyScale : Vector3.one;

				changed = Vector3.Distance(bone.localPosition, _sourcePosition[i]) > 1e-4f
				          || Vector3.Distance(local, _sourceScale[i]) > 1e-4f;
			}

			if (changed)
				Fit(_finger);

			var active = IsActive();
			var equipped = active != _equipped;

			SetEquipped(active);
			return changed || equipped;
		}

		/// <summary>Whether every bone of the avatar finger is present and active.</summary>
		public bool IsActive() {
			if (_avatar == null)
				return true;

			for (var i = 0; i < BONE_COUNT; i++) {
				var bone = _avatar[i];

				if (!bone || !bone.gameObject.activeInHierarchy)
					return false;
			}

			return true;
		}

		private void SetEquipped(bool value) {
			if (_equipped == value)
				return;

			_equipped = value;

			if (_capsules != null)
				for (var i = 0; i < _capsules.Length; i++)
					if (_capsules[i])
						_capsules[i].enabled = value;

			if (_poke)
				_poke.enabled = value;
		}

		/// <summary>Brings the finger back to the default shape of the prefab.</summary>
		public void Unfit() {
			_avatar = null;
			_finger = null;
			Radius = defaultRadius;

			if (_defaultPosition != null && _defaultRotation != null) {
				System.Array.Copy(_defaultPosition, _restPosition, BONE_COUNT);
				System.Array.Copy(_defaultRotation, _restRotation, BONE_COUNT);
			}

			Poses = DefaultPoses();

			if (_poke)
				_poke.localRadius = Radius;

			Rest();
			Size();
			SetEquipped(true);
		}

		private static float Ratio(float from, float to)
			=> Mathf.Abs(to) > 1e-6f ? from / to : 1f;

		private IPose[] DefaultPoses() {
			if (_defaultRotation == null)
				return null;

			var opened = new ChainPose {
				Curl = FingerCurl.Opened,
				Values = (Quaternion[])_defaultRotation.Clone()
			};

			var closed = new ChainPose {
				Curl = FingerCurl.Closed,
				Values = new Quaternion[BONE_COUNT]
			};

			for (var i = 0; i < BONE_COUNT; i++)
				closed.Values[i] = i < _curlShare.Length
					? _defaultRotation[i] * Quaternion.Euler(curlAngle * _curlShare[i], 0f, 0f)
					: _defaultRotation[i];

			return new IPose[] { opened, closed };
		}

		public void Rest() {
			if (_restPosition == null || _restRotation == null)
				return;

			for (var i = 0; i < BONE_COUNT; i++) {
				var bone = Bone(i);
				if (!bone)
					continue;

				bone.localPosition = _restPosition[i];
				bone.localRotation = _restRotation[i];
			}
		}

		/// <summary>Folds the finger from its rest pose, for a hand with no pose engine.</summary>
		public void Bend(float curl) {
			Rest();

			if (curl <= 0f || _restRotation == null)
				return;

			for (var i = 0; i < BONE_COUNT - 1; i++) {
				var angle = curl * curlAngle * _curlShare[i];
				bones[i].localRotation = _restRotation[i] * Quaternion.Euler(angle, 0f, 0f);
			}
		}

		/// <summary>Gives the pose of the chain back to the avatar bones it was fitted on.</summary>
		public void Mirror() {
			if (_avatar == null)
				return;

			for (var i = 0; i < BONE_COUNT; i++) {
				var bone = _avatar[i];
				var own = Bone(i);

				if (bone && own)
					bone.localRotation = own.localRotation;
			}
		}

		/// <summary>
		/// Sizes the colliders on the chain, the way the AutoHand conversion does: a capsule and its transition
		/// box on every phalanx. The tip carries none: its poke is the fingertip, and the grip of that poke is
		/// taken by the capsule of the last phalanx. Lengths are measured in the local space of the bone, so a
		/// scaled hand part keeps finger sized colliders.
		/// </summary>
		public void Size() {
			if (!equip || _capsules == null)
				return;

			for (var i = 0; i < _capsules.Length; i++) {
				var capsule = _capsules[i];
				var box = _boxes != null ? _boxes[i] : null;
				var from = Bone(i);
				var to = Bone(i + 1);

				if (!from || !to)
					continue;

				var localDirection = from.InverseTransformPoint(to.position);
				var length = localDirection.magnitude;

				if (length < 0.001f)
					continue;

				localDirection /= length;

				var absolute = new Vector3(Mathf.Abs(localDirection.x), Mathf.Abs(localDirection.y), Mathf.Abs(localDirection.z));
				var axis = absolute.x >= absolute.y && absolute.x >= absolute.z ? 0 : absolute.y >= absolute.z ? 1 : 2;

				var radius = Mathf.Max(Radius, 0.005f);

				if (capsule) {
					capsule.direction = axis;
					capsule.radius = radius;
					capsule.height = Mathf.Max(length - radius, radius * 2f);

					var center = Vector3.zero;
					center[axis] = capsule.height * 0.5f * Mathf.Sign(localDirection[axis]);
					capsule.center = center;
				}

				if (box && capsule) {
					var center = Vector3.zero;
					center[axis] = length * 0.5f * Mathf.Sign(localDirection[axis]);

					box.center = center;
					box.size = new Vector3(capsule.radius * 2f, capsule.radius * 2f, capsule.radius * 2f) { [capsule.direction] = length };
					box.enabled = false;
				}
			}
		}
	}
}
