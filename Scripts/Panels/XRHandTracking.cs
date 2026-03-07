using Nox.CCK.XR;
using UnityEngine;

namespace Nox.XR.Panels {
	/// <summary>
	/// Classe statique pour récupérer les positions des mains XR
	/// Basé sur XRInputs de Nox.CCK.XR qui gère déjà l'offset du XR Origin via le provider
	/// </summary>
	public static class XRHandTracking {
		/// <summary>
		/// Obtient la position d'une main dans l'espace world
		/// Utilise XRInputs comme source de données (l'offset XR Origin est géré par le provider)
		/// </summary>
		/// <param name="handType">Type de main (Left/Right)</param>
		/// <param name="position">Position en world space</param>
		/// <param name="rotation">Rotation en world space</param>
		/// <returns>True si la position a été obtenue avec succès</returns>
		public static bool TryGetAbsoluteHandPosition(XRHandType handType, out Vector3 position, out Quaternion rotation) {
			return handType switch {
				XRHandType.Left => XRInputs.GetLeftHandPose(out position, out rotation),
				XRHandType.Right => XRInputs.GetRightHandPose(out position, out rotation),
				_ => (position = Vector3.zero, rotation = Quaternion.identity, false).Item3
			};
		}

		/// <summary>
		/// Obtient la position relative (tracking space) d'une main via XRInputs
		/// Alias de TryGetAbsoluteHandPosition car le provider gère déjà la transformation
		/// </summary>
		/// <param name="handType">Type de main (Left/Right)</param>
		/// <param name="position">Position en world space</param>
		/// <param name="rotation">Rotation en world space</param>
		/// <returns>True si la position a été obtenue avec succès</returns>
		public static bool TryGetRelativeHandPosition(XRHandType handType, out Vector3 position, out Quaternion rotation) {
			return TryGetAbsoluteHandPosition(handType, out position, out rotation);
		}

		/// <summary>
		/// Obtient la position absolue de la main gauche
		/// </summary>
		public static bool TryGetLeftHandPosition(out Vector3 position, out Quaternion rotation) {
			return XRInputs.GetLeftHandPose(out position, out rotation);
		}

		/// <summary>
		/// Obtient la position absolue de la main droite
		/// </summary>
		public static bool TryGetRightHandPosition(out Vector3 position, out Quaternion rotation) {
			return XRInputs.GetRightHandPose(out position, out rotation);
		}

		/// <summary>
		/// Vérifie si une main est trackée via XRInputs
		/// </summary>
		public static bool HasHand(XRHandType handType) {
			return handType switch {
				XRHandType.Left => XRInputs.HasHandLeft,
				XRHandType.Right => XRInputs.HasHandRight,
				_ => false
			};
		}

		/// <summary>
		/// Obtient la position de la tête/caméra XR
		/// </summary>
		public static bool TryGetHeadPosition(out Vector3 position, out Quaternion rotation) {
			return XRInputs.GetHeadsetPose(out position, out rotation);
		}

		/// <summary>
		/// Obtient la distance entre les deux mains en world space
		/// </summary>
		public static bool TryGetHandDistance(out float distance) {
			distance = 0f;

			if (XRInputs.GetLeftHandPose(out Vector3 leftPos, out _) &&
				XRInputs.GetRightHandPose(out Vector3 rightPos, out _)) {
				distance = Vector3.Distance(leftPos, rightPos);
				return true;
			}

			return false;
		}

		/// <summary>
		/// Obtient le point central entre les deux mains en world space
		/// </summary>
		public static bool TryGetHandCenter(out Vector3 center, out Quaternion averageRotation) {
			center = Vector3.zero;
			averageRotation = Quaternion.identity;

			if (XRInputs.GetLeftHandPose(out Vector3 leftPos, out Quaternion leftRot) &&
				XRInputs.GetRightHandPose(out Vector3 rightPos, out Quaternion rightRot)) {
				
				center = (leftPos + rightPos) / 2f;
				averageRotation = Quaternion.Slerp(leftRot, rightRot, 0.5f);
				return true;
			}

			return false;
		}

		/// <summary>
		/// Debug : affiche les informations de tracking
		/// </summary>
		public static string GetDebugInfo() {
			string info = $"XR Hand Tracking Debug:\n";
			info += $"- XRInputs Provider: {(XRInputs.Provider != null ? XRInputs.Provider.GetType().Name : "NULL")}\n";
			info += $"- Has Headset: {XRInputs.HasHeadset}\n";

			// Main gauche
			if (XRInputs.HasHandLeft) {
				if (XRInputs.GetLeftHandPose(out Vector3 leftPos, out Quaternion leftRot)) {
					info += $"- Left Hand: {leftPos}\n";
					info += $"  Rotation: {leftRot.eulerAngles}\n";
				}
			} else {
				info += $"- Left Hand: NOT TRACKED\n";
			}

			// Main droite
			if (XRInputs.HasHandRight) {
				if (XRInputs.GetRightHandPose(out Vector3 rightPos, out Quaternion rightRot)) {
					info += $"- Right Hand: {rightPos}\n";
					info += $"  Rotation: {rightRot.eulerAngles}\n";
				}
			} else {
				info += $"- Right Hand: NOT TRACKED\n";
			}

			// Distance entre les mains
			if (TryGetHandDistance(out float distance)) {
				info += $"- Hand Distance: {distance:F3}m\n";
			}

			return info;
		}
	}
}

