using Nox.Avatars.Hand;
using UnityEngine;

namespace Nox.XR.Connectors {
	/// <summary>
	/// Carries the avatar pivot offset on a HandOffset GameObject and allows fine-tuning
	/// via <see cref="adjustPosition"/> and <see cref="adjustRotation"/>.
	/// The final transform is: baseOffset + adjust.
	/// Call <see cref="Apply"/> whenever adjustments change programmatically.
	/// </summary>
	public class HandOffsetSetup : MonoBehaviour {
        public static Vector3 defaultRightHandPositionOffset = new(0.008f, 0.05f, -0.08f);
        public static Quaternion defaultRightHandRotationOffset = Quaternion.Euler(0f, 270f, 90f);

        public static Vector3 defaultLeftHandPositionOffset = new(-0.008f, -0.05f, -0.08f);
        public static Quaternion defaultLeftHandRotationOffset = Quaternion.Euler(0f, 270f, 90f);





		[HideInInspector] public Vector3    basePosition;
		[HideInInspector] public Quaternion baseRotation = Quaternion.identity;

		[Tooltip("Additional local-space position offset applied on top of the avatar pivot offset.")]
		public Vector3    adjustPosition;
		[Tooltip("Additional local-space rotation offset applied on top of the avatar pivot offset.")]
		public Quaternion adjustRotation = Quaternion.identity;

		private void Start() => Apply();

		/// <summary>Recomputes localPosition and localRotation from base + adjust.</summary>
		public void Apply() {
			transform.localPosition = basePosition + adjustPosition;
			transform.localRotation = baseRotation * adjustRotation;
		}

		/// <summary>
		/// Initialises the base offsets from an <see cref="IHand"/> source and immediately applies them.
		/// </summary>
		public void Setup(IHand source) {
			if (source != null) {
				var invRot    = Quaternion.Inverse(source.RotationOffset);
				basePosition  = invRot * (-source.PositionOffset);
				baseRotation  = invRot;
			} else {
				basePosition = Vector3.zero;
				baseRotation = Quaternion.identity;
			}
            
            if (source.Type == HandType.Right) {
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
#endif
	}
}
