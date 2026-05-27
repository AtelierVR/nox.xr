using System.Collections;
using Autohand;
using Nox.Avatars.Hand;
using RootMotion.FinalIK;
using UnityEngine;

namespace Nox.XR.Connectors {
	[DefaultExecutionOrder(12), RequireComponent(typeof(VRIK))]
	public class NoxAutoHandVRIK : MonoBehaviour {
		public Hand rightHand;
		public Hand leftHand;
		[Tooltip("The transform of the tracked right VR controller")]
		public Transform rightTrackedController;
		[Tooltip("The transform of the tracked left VR controller")]
		public Transform leftTrackedController;
		[Tooltip("IHand data for the right hand (provides pivot offset)")]
		public IHand rightHandSource;
		[Tooltip("IHand data for the left hand (provides pivot offset)")]
		public IHand leftHandSource;

		private Transform _rightHandOffset;
		private Transform _leftHandOffset;
		private bool _resetQueued = false;
		private Animator _animator;

		public VRIK vrik { get; protected set; }

		protected virtual void Start() {
			vrik = GetComponent<VRIK>();
			_animator = GetComponentInChildren<Animator>();
			SetupIK();
			if (AutoHandPlayer.Instance != null)
				vrik.transform.position -= Vector3.up * AutoHandPlayer.Instance.heightOffset;
			StartCoroutine(RebindAnimatorDelay());
		}

		private IEnumerator RebindAnimatorDelay() {
			yield return new WaitForEndOfFrame();
			yield return new WaitForFixedUpdate();
			_animator.Rebind();
		}

		protected virtual void OnEnable() {
			if (AutoHandPlayer.Instance != null) {
				AutoHandPlayer.Instance.OnSnapTurn += AutoPlayerResetIKEvent;
				AutoHandPlayer.Instance.OnSmoothTurn += AutoPlayerResetIKEvent;
				AutoHandPlayer.Instance.OnTeleported += AutoPlayerResetIKEvent;
			}
			if (rightHand != null) {
				rightHand.OnGrabbed += OnRightGrab;
				rightHand.OnReleased += OnRightRelease;
			}
			if (leftHand != null) {
				leftHand.OnGrabbed += OnLeftGrab;
				leftHand.OnReleased += OnLeftRelease;
			}
			_resetQueued = true;
		}

		protected virtual void OnDisable() {
			if (AutoHandPlayer.Instance != null) {
				AutoHandPlayer.Instance.OnSnapTurn -= AutoPlayerResetIKEvent;
				AutoHandPlayer.Instance.OnSmoothTurn -= AutoPlayerResetIKEvent;
				AutoHandPlayer.Instance.OnTeleported -= AutoPlayerResetIKEvent;
			}
			if (rightHand != null) {
				rightHand.OnGrabbed -= OnRightGrab;
				rightHand.OnReleased -= OnRightRelease;
			}
			if (leftHand != null) {
				leftHand.OnGrabbed -= OnLeftGrab;
				leftHand.OnReleased -= OnLeftRelease;
			}
		}

		protected virtual void OnDestroy() {
			if (_rightHandOffset != null) Destroy(_rightHandOffset.gameObject);
			if (_leftHandOffset  != null) Destroy(_leftHandOffset.gameObject);
		}

		protected virtual void OnRightGrab(Hand hand, Grabbable grab) {
			vrik.solver.rightArm.target = hand.handGrabPoint;
		}

		protected virtual void OnRightRelease(Hand hand, Grabbable grab) {
			vrik.solver.rightArm.target = _rightHandOffset;
		}

		protected virtual void OnLeftGrab(Hand hand, Grabbable grab) {
			vrik.solver.leftArm.target = hand.handGrabPoint;
		}

		protected virtual void OnLeftRelease(Hand hand, Grabbable grab) {
			vrik.solver.leftArm.target = _leftHandOffset;
		}

		protected virtual void AutoPlayerResetIKEvent(AutoHandPlayer player) {
			_resetQueued = true;
		}

		private void Update() {
			if (!_resetQueued) return;
			vrik.solver.Reset();
			_resetQueued = false;
		}

		protected virtual void LateUpdate() {
			if (AutoHandPlayer.Instance == null) return;
			var pos = transform.position;
			pos.y = AutoHandPlayer.Instance.transform.position.y;
			transform.position = pos;
		}

		protected virtual void SetupIK() {
			// The AutoHand hands live on skeleton bones (hand.Anchor).
			// Disable physics movement so the Rigidbody does not fight VRIK's LateUpdate IK.
			SetupHandPhysics(rightHand);
			SetupHandPhysics(leftHand);

			// Create HandOffset children that carry the avatar's pivot offset so the
			// controller position aligns with the hand pivot, not the raw anchor.
			_rightHandOffset = CreateHandOffset(rightTrackedController, rightHandSource);
			_leftHandOffset  = CreateHandOffset(leftTrackedController,  leftHandSource);

			// vrik.references stay untouched — the skeleton chain must remain intact.
			vrik.solver.rightArm.target = _rightHandOffset;
			vrik.solver.leftArm.target  = _leftHandOffset;
		}

		private static Transform CreateHandOffset(Transform parent, IHand source) {
			if (parent == null) return null;
			var go = new GameObject("HandOffset");
			go.transform.SetParent(parent, false);
			var setup = go.AddComponent<HandOffsetSetup>();
			setup.Setup(source);
			return go.transform;
		}

		private static void SetupHandPhysics(Hand hand) {
			if (hand == null) return;
			hand.enableMovement = false;
			var rb = hand.GetComponent<Rigidbody>();
			if (rb == null) return;
			rb.isKinematic = true;
			rb.useGravity  = false;
		}
	}
}
