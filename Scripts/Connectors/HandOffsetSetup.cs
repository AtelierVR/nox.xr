using Nox.Avatars.Hand;
using UnityEngine;

namespace Nox.XR.Connectors {
	/// <summary>
	/// Carries the avatar pivot offset on a HandOffset GameObject and allows fine-tuning
	/// via <see cref="adjustPosition"/> and <see cref="adjustRotation"/>.
	/// The rotation is <c>base × adjust</c>; the position is re-derived from it so that
	/// <see cref="adjustRotation"/> rotates the hand <b>around its pivot</b>.
	/// Call <see cref="Apply"/> whenever adjustments change programmatically.
	/// <para>
	/// The HandOffset is a child of the Controller. Its local transform expresses the
	/// <b>Anchor pose</b> in Controller space — i.e. where the wrist bone (Anchor) sits
	/// relative to the controller, so that the pivot (Anchor + PivotOffset) lands on the
	/// controller's own origin. Once VRIK converges, the pivot gizmo drawn by
	/// <see cref="Hand.OnDrawGizmos"/> will align with the controller axes.
	/// </para>
	/// </summary>
	public class HandOffsetSetup : MonoBehaviour {
        public static Vector3 defaultRightHandPositionOffset = Vector3.zero;
        public static Quaternion defaultRightHandRotationOffset = Quaternion.Euler(0, 270, 180);

        public static Vector3 defaultLeftHandPositionOffset = Vector3.zero;
        public static Quaternion defaultLeftHandRotationOffset = Quaternion.Euler(0, 270, 0);





		public Vector3    basePosition;
		public Quaternion baseRotation = Quaternion.identity;

		[Tooltip("Additional local-space position offset applied on top of the avatar pivot offset.")]
		public Vector3    adjustPosition;
		[Tooltip("Additional local-space rotation offset applied on top of the avatar pivot offset. "
		         + "It rotates the hand AROUND its pivot, so the pivot stays exactly on the controller.")]
		public Quaternion adjustRotation = Quaternion.identity;

		/// <summary>Hand the base offsets were computed from (runtime only, used by <see cref="PrintPivot"/>).</summary>
		private IHand _source;
		/// <summary>Wrist → pivot, in world units (Anchor scale already applied).</summary>
		private Vector3 _pivot;
		/// <summary>Controller world scale: localPosition is written in its units.</summary>
		private Vector3 _parentScale = Vector3.one;
		/// <summary>True once <see cref="Setup"/> cached a source pivot.</summary>
		private bool _hasPivot;

		private void Start() => Apply();

		/// <summary>
		/// Recomputes localPosition and localRotation from base + adjust.
		/// <para>
		/// <b>adjustRotation rotates the hand around its pivot</b>, not around the wrist: the wrist
		/// position is re-derived from the adjusted rotation, so the pivot stays exactly on the
		/// controller. Rotating the wrist in place instead would swing the pivot away by up to twice
		/// the wrist→pivot distance (≈16 cm at the avatar's scale), which is precisely the
		/// « pivot misses the controller » symptom when an adjust rotation is set.
		/// </para>
		/// </summary>
		public void Apply() {
			transform.localRotation = baseRotation * adjustRotation;

			if (!_hasPivot) {
				// No IHand source (prefab/editor): keep the authored base offset untouched.
				transform.localPosition = basePosition + adjustPosition;
				return;
			}

			// Controller → wrist, so that wrist + rotation · (wrist→pivot) lands on the controller origin.
			var local = -(transform.localRotation * _pivot);
			transform.localPosition = new Vector3(
				_parentScale.x != 0f ? local.x / _parentScale.x : local.x,
				_parentScale.y != 0f ? local.y / _parentScale.y : local.y,
				_parentScale.z != 0f ? local.z / _parentScale.z : local.z
			) + adjustPosition;
		}

		/// <summary>
		/// Initialises the base offsets from an <see cref="IHand"/> source and immediately applies them.
		/// <para>
		/// The pivot offset on the Hand component is defined <b>Anchor → Controller</b>.
		/// The HandOffset is a child of the Controller and must express the <b>inverse</b>:
		/// <b>Controller → Anchor</b>, so that once VRIK converges the pivot lands exactly
		/// on the controller's origin.
		/// </para>
		/// <para>
		/// Two scales must be accounted for, otherwise the pivot misses the controller by a few
		/// centimetres:
		/// <list type="number">
		/// <item>the offset lives in the <b>Anchor's local space</b>, so Unity scales it by the
		/// Anchor's <see cref="Transform.lossyScale"/> when converting it to world space
		/// (<c>Anchor.TransformPoint</c>) — avatars are commonly scaled (×1.58 for the bundled
		/// ones), which scales the wrist→pivot vector with it;</item>
		/// <item>the result is written in the <b>Controller's local space</b>, which Unity scales
		/// by the parent's lossy scale — so it is divided back out.</item>
		/// </list>
		/// </para>
		/// </summary>
		public void Setup(IHand source) {
			_source = source;
			if (source != null) {
				baseRotation = Quaternion.Inverse(source.RotationOffset);

				// Wrist → pivot, in world units: the offset lives in the Anchor's local space, so Unity
				// scales it by the Anchor's lossyScale when converting it to world space
				// (Anchor.TransformPoint) — avatars are commonly scaled (×1.58 for the bundled ones),
				// which scales the wrist→pivot vector with it.
				var anchor = source.Anchor;
				_pivot = Vector3.Scale(source.PositionOffset, anchor != null ? anchor.lossyScale : Vector3.one);

				// localPosition is written in the Controller's units (Unity scales it by the parent's
				// lossyScale): cache it to divide the scale back out.
				var parent = transform.parent;
				_parentScale = parent != null ? parent.lossyScale : Vector3.one;
				_hasPivot    = true;

				// Adjust-free wrist position, i.e. what Apply() recomputes for adjustRotation == identity.
				var inverse = baseRotation * -_pivot;
				basePosition = new Vector3(
					_parentScale.x != 0f ? inverse.x / _parentScale.x : inverse.x,
					_parentScale.y != 0f ? inverse.y / _parentScale.y : inverse.y,
					_parentScale.z != 0f ? inverse.z / _parentScale.z : inverse.z
				);
			} else {
				_pivot       = Vector3.zero;
				_parentScale = Vector3.one;
				_hasPivot    = false;
				basePosition = Vector3.zero;
				baseRotation = Quaternion.identity;
			}

            if (source != null && source.Type == HandType.Right) {
                adjustPosition = defaultRightHandPositionOffset;
                adjustRotation = defaultRightHandRotationOffset;
            } else {
                adjustPosition = defaultLeftHandPositionOffset;
                adjustRotation = defaultLeftHandRotationOffset;
            }
			Apply();
		}

#if UNITY_EDITOR
		private void OnValidate() => Apply();

		/// <summary>
		/// Same-frame diagnostic of the whole chain controller → HandOffset → avatar bone.
		/// <para>
		/// <b>setup</b> is the pivot rebuilt from the offsets stored by <see cref="Setup"/>: its Δpos must be
		/// zero. Its Δrot is the angle of <see cref="adjustRotation"/> by construction (the adjust rotates
		/// the hand around the pivot): zero it to check the raw avatar pivot.
		/// <b>bone</b> is the pivot really carried by <c>IHand.Anchor</c>, i.e. after VRIK: its residual
		/// is a solver/target issue (target lag, <c>handGrabPoint</c> while grabbing…), not an offset one.
		/// </para>
		/// <para>
		/// Both lines are measured in the same frame — a single log per print. Comparing the controller
		/// logged here with a pivot logged later only measures how much the hand moved in between.
		/// </para>
		/// </summary>
		[ContextMenu("Print Pivots")]
		public void PrintPivot() {
			var controller = transform.parent != null ? transform.parent : transform;

			// HandOffset pose = where the wrist bone must end up.
			var setupPos = transform.position;
			var setupRot = transform.rotation;
			var bonePos  = setupPos;
			var boneRot  = setupRot;

			if (_source != null) {
				var anchor = _source.Anchor;

				// Pivot rebuilt from the stored offsets, in the units Anchor.TransformPoint uses.
				var offset   = Vector3.Scale(_source.PositionOffset, anchor != null ? anchor.lossyScale : Vector3.one);
				setupPos = transform.position + transform.rotation * offset;
				setupRot = transform.rotation * _source.RotationOffset;

				if (anchor != null) {
					bonePos = anchor.TransformPoint(_source.PositionOffset);
					boneRot = anchor.rotation * _source.RotationOffset;
				}
			}

			Nox.CCK.Utils.Logger.Log(
				$"[pivot {name}] controller {controller.name}: {controller.position} {controller.rotation}"
				+ $"\n  setup pivot: {setupPos} {setupRot}"
				+ $" | Δpos {Mm(setupPos - controller.position)} Δrot {Quaternion.Angle(setupRot, controller.rotation):F2}°"
				+ $"\n  bone  pivot: {bonePos} {boneRot}"
				+ $" | Δpos {Mm(bonePos - controller.position)} Δrot {Quaternion.Angle(boneRot, controller.rotation):F2}°"
			);
		}

		private static string Mm(Vector3 delta)
			=> $"({delta.x * 1000f:F1}, {delta.y * 1000f:F1}, {delta.z * 1000f:F1}) mm";

#endif
	}
}
